using System.Text.Json;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public class Session
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public IProviderProfile ProviderProfile { get; }
    public IExecutionEnvironment ExecutionEnv { get; }
    public List<ITurn> History { get; } = new();
    public EventEmitter EventEmitter { get; } = new();
    public SessionConfig Config { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public IProviderAdapter LlmClient { get; }

    private readonly Queue<string> _steeringQueue = new();
    private readonly Queue<string> _followUpQueue = new();
    private readonly List<SubAgent> _subagents = new();

    public IReadOnlyList<SubAgent> Subagents => _subagents;

    public Session(
        IProviderAdapter llmClient,
        IProviderProfile providerProfile,
        IExecutionEnvironment executionEnv,
        SessionConfig? config = null)
    {
        LlmClient = llmClient;
        ProviderProfile = providerProfile;
        ExecutionEnv = executionEnv;
        Config = config ?? new SessionConfig();
        EventEmitter.SessionId = Id;
    }

    /// <summary>
    /// Queues a steering message to be injected before the next LLM call.
    /// </summary>
    public void Steer(string message)
    {
        _steeringQueue.Enqueue(message);
    }

    /// <summary>
    /// Queues a follow-up message to be sent after the current assistant turn completes.
    /// </summary>
    public void FollowUp(string message)
    {
        _followUpQueue.Enqueue(message);
    }

    /// <summary>
    /// The core agentic loop. Processes user input through the LLM, executes tool calls,
    /// and iterates until the assistant produces a final text response or limits are reached.
    /// </summary>
    public async Task<AssistantTurn> ProcessInputAsync(string userInput, CancellationToken ct = default)
    {
        if (State == SessionState.Closed)
            throw new InvalidOperationException("Session is closed.");

        State = SessionState.Processing;

        await EventEmitter.EmitAsync(EventKind.UserInput, new Dictionary<string, object?>
        {
            ["content"] = userInput
        });

        // Add user turn to history
        History.Add(new UserTurn(userInput, DateTimeOffset.UtcNow));

        var totalTurns = 0;
        var toolRounds = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Check turn limit
                if (Config.MaxTurns > 0 && totalTurns >= Config.MaxTurns)
                {
                    await EventEmitter.EmitAsync(EventKind.TurnLimit, new Dictionary<string, object?>
                    {
                        ["turns"] = totalTurns,
                        ["limit"] = Config.MaxTurns
                    });
                    return CreateFinalTurn("[Turn limit reached]");
                }

                // Check tool round limit
                if (Config.MaxToolRoundsPerInput > 0 && toolRounds >= Config.MaxToolRoundsPerInput)
                {
                    await EventEmitter.EmitAsync(EventKind.TurnLimit, new Dictionary<string, object?>
                    {
                        ["toolRounds"] = toolRounds,
                        ["limit"] = Config.MaxToolRoundsPerInput
                    });
                    return CreateFinalTurn("[Tool round limit reached]");
                }

                // Drain steering messages into history
                await DrainSteeringAsync();

                // Loop detection
                if (Config.EnableLoopDetection && LoopDetection.DetectLoop(History, Config.LoopDetectionWindow))
                {
                    await EventEmitter.EmitAsync(EventKind.LoopDetection);

                    // Inject a steering message to break the loop
                    History.Add(new SteeringTurn(
                        "You appear to be in a loop repeating the same tool calls. Please try a different approach or ask the user for clarification.",
                        DateTimeOffset.UtcNow));
                }

                // Build messages and call LLM
                var messages = ConvertHistoryToMessages();
                var systemPrompt = ProviderProfile.BuildSystemPrompt(ExecutionEnv);

                var request = new Request
                {
                    Model = ProviderProfile.Model,
                    Messages = messages,
                    Tools = ProviderProfile.Tools().ToList(),
                    ToolChoice = ToolChoice.Auto,
                    ReasoningEffort = Config.ReasoningEffort
                };

                // Insert system message at position 0
                request.Messages.Insert(0, Message.SystemMsg(systemPrompt));

                await EventEmitter.EmitAsync(EventKind.AssistantTextStart);

                Response response;
                try
                {
                    response = await LlmClient.CompleteAsync(request, ct);
                }
                catch (Exception ex)
                {
                    await EventEmitter.EmitAsync(EventKind.Error, new Dictionary<string, object?>
                    {
                        ["error"] = ex.Message,
                        ["exception"] = ex
                    });
                    return CreateFinalTurn($"[Error: {ex.Message}]");
                }

                totalTurns++;

                var text = response.Text;
                var toolCalls = response.ToolCalls;
                var reasoning = response.Reasoning;
                var usage = response.Usage;
                var thinkingParts = response.Message.Content
                    .Where(p => (p.Kind == ContentKind.Thinking || p.Kind == ContentKind.RedactedThinking) && p.Thinking is not null)
                    .Select(p => p.Thinking!)
                    .ToList();

                // Emit text delta if there is text content
                if (!string.IsNullOrEmpty(text))
                {
                    await EventEmitter.EmitAsync(EventKind.AssistantTextDelta, new Dictionary<string, object?>
                    {
                        ["text"] = text
                    });
                }

                await EventEmitter.EmitAsync(EventKind.AssistantTextEnd);

                // Create assistant turn and add to history
                var assistantTurn = new AssistantTurn(
                    text,
                    toolCalls,
                    reasoning,
                    usage,
                    response.Id,
                    DateTimeOffset.UtcNow,
                    thinkingParts.Count > 0 ? thinkingParts : null);

                History.Add(assistantTurn);

                // If no tool calls, this is the final response
                if (toolCalls.Count == 0)
                {
                    // Check for follow-up messages
                    if (_followUpQueue.Count > 0)
                    {
                        var followUp = _followUpQueue.Dequeue();
                        History.Add(new UserTurn(followUp, DateTimeOffset.UtcNow));
                        continue;
                    }

                    State = SessionState.Idle;
                    return assistantTurn;
                }

                // Execute tool calls
                toolRounds++;
                var toolResults = await ExecuteToolCallsAsync(toolCalls, ct);

                // Add tool results to history
                History.Add(new ToolResultsTurn(toolResults, DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException)
        {
            State = SessionState.Idle;
            throw;
        }
        catch (Exception ex)
        {
            await EventEmitter.EmitAsync(EventKind.Error, new Dictionary<string, object?>
            {
                ["error"] = ex.Message,
                ["exception"] = ex
            });
            State = SessionState.Idle;
            throw;
        }
    }

    /// <summary>
    /// Drains all queued steering messages into the history as SteeringTurns.
    /// </summary>
    private async Task DrainSteeringAsync()
    {
        while (_steeringQueue.Count > 0)
        {
            var msg = _steeringQueue.Dequeue();
            History.Add(new SteeringTurn(msg, DateTimeOffset.UtcNow));

            await EventEmitter.EmitAsync(EventKind.SteeringInjected, new Dictionary<string, object?>
            {
                ["content"] = msg
            });
        }
    }

    /// <summary>
    /// Executes all tool calls from the assistant response and returns their results.
    /// </summary>
    private async Task<List<ToolResultData>> ExecuteToolCallsAsync(
        List<ToolCallData> toolCalls,
        CancellationToken ct)
    {
        var results = new List<ToolResultData>();

        foreach (var toolCall in toolCalls)
        {
            ct.ThrowIfCancellationRequested();

            await EventEmitter.EmitAsync(EventKind.ToolCallStart, new Dictionary<string, object?>
            {
                ["toolCallId"] = toolCall.Id,
                ["toolName"] = toolCall.Name,
                ["arguments"] = toolCall.Arguments
            });

            var result = await ExecuteSingleToolAsync(toolCall, ct);

            // Apply output truncation
            var truncatedContent = OutputTruncation.Truncate(
                result.Content,
                toolCall.Name,
                Config.ToolOutputLimits);

            var finalResult = result with { Content = truncatedContent };
            results.Add(finalResult);

            await EventEmitter.EmitAsync(EventKind.ToolCallEnd, new Dictionary<string, object?>
            {
                ["toolCallId"] = toolCall.Id,
                ["toolName"] = toolCall.Name,
                ["isError"] = finalResult.IsError,
                ["outputLength"] = finalResult.Content.Length
            });
        }

        return results;
    }

    /// <summary>
    /// Executes a single tool call and returns the result.
    /// </summary>
    private async Task<ToolResultData> ExecuteSingleToolAsync(ToolCallData toolCall, CancellationToken ct)
    {
        var tool = ProviderProfile.ToolRegistry.Get(toolCall.Name);
        if (tool is null)
        {
            return new ToolResultData(
                toolCall.Id,
                $"Error: Unknown tool '{toolCall.Name}'",
                IsError: true);
        }

        try
        {
            var output = await tool.Execute(toolCall.Arguments, ExecutionEnv);
            return new ToolResultData(toolCall.Id, output, IsError: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResultData(
                toolCall.Id,
                $"Error executing {toolCall.Name}: {ex.Message}",
                IsError: true);
        }
    }

    /// <summary>
    /// Converts the session history into the LLM message format.
    /// </summary>
    private List<Message> ConvertHistoryToMessages()
    {
        var messages = new List<Message>();

        foreach (var turn in History)
        {
            switch (turn)
            {
                case UserTurn userTurn:
                    messages.Add(Message.UserMsg(userTurn.Content));
                    break;

                case AssistantTurn assistantTurn:
                {
                    var parts = new List<ContentPart>();

                    // Add thinking/reasoning parts if present (preserve signatures for round-trip)
                    if (assistantTurn.ThinkingParts is { Count: > 0 })
                    {
                        foreach (var tp in assistantTurn.ThinkingParts)
                            parts.Add(ContentPart.ThinkingPart(tp));
                    }
                    else if (assistantTurn.Reasoning is not null)
                    {
                        parts.Add(ContentPart.ThinkingPart(
                            new ThinkingData(assistantTurn.Reasoning, null, false)));
                    }

                    // Add text content
                    if (!string.IsNullOrEmpty(assistantTurn.Content))
                    {
                        parts.Add(ContentPart.TextPart(assistantTurn.Content));
                    }

                    // Add tool calls
                    foreach (var tc in assistantTurn.ToolCalls)
                    {
                        parts.Add(ContentPart.ToolCallPart(tc));
                    }

                    if (parts.Count > 0)
                    {
                        messages.Add(new Message(Role.Assistant, parts));
                    }
                    break;
                }

                case ToolResultsTurn toolResults:
                {
                    foreach (var result in toolResults.Results)
                    {
                        messages.Add(Message.ToolResultMsg(
                            result.ToolCallId,
                            result.Content,
                            result.IsError));
                    }
                    break;
                }

                case SystemTurn systemTurn:
                    messages.Add(Message.SystemMsg(systemTurn.Content));
                    break;

                case SteeringTurn steeringTurn:
                    // Steering messages are injected as user messages with a special prefix
                    messages.Add(Message.UserMsg($"[System Guidance]: {steeringTurn.Content}"));
                    break;
            }
        }

        return messages;
    }

    /// <summary>
    /// Creates a final AssistantTurn with default/empty values for tool calls, usage, etc.
    /// </summary>
    private AssistantTurn CreateFinalTurn(string content)
    {
        var turn = new AssistantTurn(
            content,
            new List<ToolCallData>(),
            null,
            Usage.Empty,
            null,
            DateTimeOffset.UtcNow);

        History.Add(turn);
        State = SessionState.Idle;
        return turn;
    }
}
