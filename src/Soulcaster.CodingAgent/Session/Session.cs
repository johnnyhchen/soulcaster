using System.Text.Json;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.CodingAgent;

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
    public Exception? LastError { get; private set; }

    private readonly Queue<string> _steeringQueue = new();
    private readonly Queue<string> _followUpQueue = new();
    private readonly List<Subagent> _subagents = new();
    private readonly SemaphoreSlim _processLock = new(1, 1);

    public IReadOnlyList<Subagent> Subagents => _subagents;
    public int Depth { get; init; }

    public Session(
        IProviderAdapter llmClient,
        IProviderProfile providerProfile,
        IExecutionEnvironment executionEnv,
        SessionConfig? config = null)
    {
        LlmClient = llmClient;
        ProviderProfile = new SessionOwnedProfile(providerProfile);
        ExecutionEnv = executionEnv;
        Config = config ?? new SessionConfig();
        EventEmitter.SessionId = Id;
    }

    /// <summary>
    /// Spawns a child subagent with its own session.
    /// </summary>
    public Subagent SpawnSubagent(string? model = null)
    {
        var maxSubagentDepth = Config.MaxSubagentDepth > 0 ? Config.MaxSubagentDepth : Subagent.DefaultMaxDepth;
        if (Depth >= maxSubagentDepth)
            throw new InvalidOperationException($"Max subagent depth ({maxSubagentDepth}) reached.");

        var childSession = new Session(
            LlmClient,
            ProviderProfile,
            ExecutionEnv,
            Config)
        { Depth = Depth + 1 };

        if (model is not null)
            childSession.ProviderProfile.Model = model;

        var subagent = new Subagent(childSession, Depth + 1);
        _subagents.Add(subagent);
        return subagent;
    }

    /// <summary>
    /// Gets a subagent by ID.
    /// </summary>
    public Subagent? GetSubagent(string id) => _subagents.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Closes and removes a subagent.
    /// </summary>
    public void CloseSubagent(string id)
    {
        var subagent = _subagents.FirstOrDefault(s => s.Id == id);
        if (subagent is not null)
        {
            subagent.Close();
            _subagents.Remove(subagent);
        }
    }

    /// <summary>
    /// Closes this session and all child subagents.
    /// </summary>
    public void Close()
    {
        if (State == SessionState.Closed)
            return;

        foreach (var subagent in _subagents.ToList())
            subagent.Close();
        _subagents.Clear();

        State = SessionState.Closed;
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
    public Task<AssistantTurn> ProcessInputAsync(string userInput, CancellationToken ct = default) =>
        ProcessInputAsync(Message.UserMsg(userInput), requestOptions: null, ct);

    /// <summary>
    /// The core agentic loop for multimodal user input. Processes user content through the LLM,
    /// executes tool calls, and iterates until the assistant produces a final response or limits are reached.
    /// </summary>
    public async Task<AssistantTurn> ProcessInputAsync(Message userMessage, CancellationToken ct = default)
    {
        return await ProcessInputAsync(userMessage, requestOptions: null, ct);
    }

    public async Task<AssistantTurn> ProcessInputAsync(
        Message userMessage,
        SessionRequestOptions? requestOptions,
        CancellationToken ct = default)
    {
        if (userMessage.Role != Role.User)
            throw new ArgumentException("ProcessInputAsync requires a user-role message.", nameof(userMessage));

        try
        {
            await _processLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(ct);
        }

        try
        {
            if (State == SessionState.Closed)
                throw new InvalidOperationException("Session is closed.");

            State = SessionState.Processing;
            LastError = null;

            await EventEmitter.EmitAsync(EventKind.UserInput, new Dictionary<string, object?>
            {
                ["content"] = userMessage.Text
            });

            // Add user turn to history
            History.Add(new UserTurn(
                userMessage.Text,
                DateTimeOffset.UtcNow,
                ShouldPersistParts(userMessage.Content) ? new List<ContentPart>(userMessage.Content) : null));

            var totalTurns = 0;
            var toolRounds = 0;
            var explorationStallWarningIssued = false;

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

                    if (Config.EnableLoopDetection)
                    {
                        var explorationRounds = LoopDetection.CountConsecutiveExplorationOnlyRounds(History);
                        var explorationToolCalls = LoopDetection.CountConsecutiveExplorationOnlyToolCalls(History);
                        var roundLimitHit = Config.MaxConsecutiveExplorationRounds > 0 &&
                                            explorationRounds >= Config.MaxConsecutiveExplorationRounds;
                        var toolCallLimitHit = Config.MaxConsecutiveExplorationToolCalls > 0 &&
                                               explorationToolCalls >= Config.MaxConsecutiveExplorationToolCalls;

                        if (roundLimitHit || toolCallLimitHit)
                        {
                            var stallMetric = toolCallLimitHit
                                ? $"{explorationToolCalls} consecutive exploration-only tool calls"
                                : $"{explorationRounds} consecutive exploration-only tool rounds";

                            await EventEmitter.EmitAsync(EventKind.LoopDetection, new Dictionary<string, object?>
                            {
                                ["reason"] = "exploration_stall",
                                ["rounds"] = explorationRounds,
                                ["tool_calls"] = explorationToolCalls
                            });

                            if (explorationStallWarningIssued)
                            {
                                return CreateFinalTurn(
                                    $"[Exploration stall detected after {stallMetric}]");
                            }

                            History.Add(new SteeringTurn(
                                $"You have spent {stallMetric} only reading or searching files. Stop gathering more context and either make a concrete change, run a validating command, ask a blocking question, or provide a final answer.",
                                DateTimeOffset.UtcNow));
                            explorationStallWarningIssued = true;
                        }
                        else
                        {
                            explorationStallWarningIssued = false;
                        }

                        // Loop detection
                        if (LoopDetection.DetectLoop(History, Config.LoopDetectionWindow))
                        {
                            await EventEmitter.EmitAsync(EventKind.LoopDetection);

                            // Inject a steering message to break the loop
                            History.Add(new SteeringTurn(
                                "You appear to be in a loop repeating the same tool calls. Please try a different approach or ask the user for clarification.",
                                DateTimeOffset.UtcNow));
                        }
                    }

                    // Build messages and call LLM
                    var messages = ConvertHistoryToMessages();
                    var projectDocs = ProjectDocs.Discover(ExecutionEnv.WorkingDirectory);
                    var systemPrompt = ProviderProfile.BuildSystemPrompt(ExecutionEnv, projectDocs.Count > 0 ? projectDocs : null);

                    var tools = ProviderProfile.Tools().ToList();
                    var request = new Request
                    {
                        Model = ProviderProfile.Model,
                        Messages = messages,
                        Tools = tools,
                        ToolChoice = requestOptions?.ToolChoice ?? (tools.Count > 0 ? ToolChoice.Auto : ToolChoice.NoneChoice),
                        OutputModalities = requestOptions?.OutputModalities?.ToList(),
                        ReasoningEffort = Config.ReasoningEffort,
                        ProviderOptions = ProviderProfile.ProviderOptions()
                    };

                    // Insert system message at position 0
                    request.Messages.Insert(0, Message.SystemMsg(systemPrompt));

                    await EventEmitter.EmitAsync(EventKind.AssistantTextStart);

                    Response response;
                    try
                    {
                        using var providerCts = Config.MaxProviderResponseMs > 0
                            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                            : null;
                        if (providerCts is not null)
                            providerCts.CancelAfter(Config.MaxProviderResponseMs);

                        response = await LlmClient.CompleteAsync(request, providerCts?.Token ?? ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && Config.MaxProviderResponseMs > 0)
                    {
                        LastError = new TimeoutException("Model response timeout reached.");
                        await EventEmitter.EmitAsync(EventKind.Error, new Dictionary<string, object?>
                        {
                            ["error"] = "Model response timeout reached.",
                            ["timeout_ms"] = Config.MaxProviderResponseMs
                        });
                        return CreateFinalTurn("[Model response timeout reached]");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex;
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
                        thinkingParts.Count > 0 ? thinkingParts : null,
                        ShouldPersistParts(response.Message.Content) ? new List<ContentPart>(response.Message.Content) : null);

                    History.Add(assistantTurn);

                    // If no tool calls, this is the final response
                    if (toolCalls.Count == 0 || requestOptions?.ExecuteToolCalls == false)
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
                State = SessionState.Closed;
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex;
                await EventEmitter.EmitAsync(EventKind.Error, new Dictionary<string, object?>
                {
                    ["error"] = ex.Message,
                    ["exception"] = ex
                });
                State = SessionState.Idle;
                throw;
            }
        }
        finally
        {
            _processLock.Release();
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
        // Execute tool calls in parallel when the profile supports it and there are multiple calls
        if (ProviderProfile.SupportsParallelToolCalls &&
            toolCalls.Count > 1 &&
            toolCalls.All(toolCall => !IsSessionManagedTool(toolCall.Name)))
        {
            var tasks = toolCalls.Select(async toolCall =>
            {
                await EventEmitter.EmitAsync(EventKind.ToolCallStart, new Dictionary<string, object?>
                {
                    ["toolCallId"] = toolCall.Id,
                    ["toolName"] = toolCall.Name,
                    ["arguments"] = toolCall.Arguments
                });

                var result = await ExecuteSingleToolAsync(toolCall, ct);
                var truncatedContent = OutputTruncation.Truncate(
                    result.Content, toolCall.Name, Config.ToolOutputLimits);
                var finalResult = result with { Content = truncatedContent };

                await EventEmitter.EmitAsync(EventKind.ToolCallEnd, new Dictionary<string, object?>
                {
                    ["toolCallId"] = toolCall.Id,
                    ["toolName"] = toolCall.Name,
                    ["isError"] = finalResult.IsError,
                    ["outputLength"] = finalResult.Content.Length
                });

                return finalResult;
            }).ToList();

            var parallelResults = await Task.WhenAll(tasks);
            return parallelResults.ToList();
        }

        // Sequential execution
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
        var sessionManagedResult = await ExecuteSessionManagedToolAsync(toolCall, ct);
        if (sessionManagedResult is not null)
            return sessionManagedResult;

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

    private async Task<ToolResultData?> ExecuteSessionManagedToolAsync(ToolCallData toolCall, CancellationToken ct)
    {
        if (!IsSessionManagedTool(toolCall.Name))
            return null;

        using var json = JsonDocument.Parse(toolCall.Arguments);
        var root = json.RootElement;

        try
        {
            return toolCall.Name switch
            {
                "spawn_agent" => await ExecuteSpawnAgentToolAsync(toolCall.Id, root, ct),
                "send_input" => await ExecuteSendInputToolAsync(toolCall.Id, root, ct),
                "wait_agent" => await ExecuteWaitAgentToolAsync(toolCall.Id, root, ct),
                "close_agent" => ExecuteCloseAgentTool(toolCall.Id, root),
                _ => null
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ToolResultData(toolCall.Id, $"Error: {ex.Message}", IsError: true);
        }
        catch (ArgumentException ex)
        {
            return new ToolResultData(toolCall.Id, $"Error: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResultData> ExecuteSpawnAgentToolAsync(string toolCallId, JsonElement args, CancellationToken ct)
    {
        var prompt = GetRequiredString(args, "prompt");
        string? model = TryGetOptionalString(args, "model");

        var subagent = SpawnSubagent(model);
        await subagent.EnqueueInputAsync(prompt, ct);

        return new ToolResultData(
            toolCallId,
            $"Agent {subagent.Id} spawned.\nState: {FormatSubagentState(subagent.State)}\nPending inputs: {subagent.PendingInputCount}",
            IsError: false);
    }

    private async Task<ToolResultData> ExecuteSendInputToolAsync(string toolCallId, JsonElement args, CancellationToken ct)
    {
        var agentId = GetRequiredString(args, "agent_id");
        var message = GetRequiredString(args, "message");
        var subagent = GetSubagent(agentId);
        if (subagent is null)
        {
            return new ToolResultData(
                toolCallId,
                $"Error: Agent '{agentId}' not found.",
                IsError: true);
        }

        await subagent.EnqueueInputAsync(message, ct);
        return new ToolResultData(
            toolCallId,
            $"Agent {agentId} accepted input.\nState: {FormatSubagentState(subagent.State)}\nPending inputs: {subagent.PendingInputCount}",
            IsError: false);
    }

    private async Task<ToolResultData> ExecuteWaitAgentToolAsync(string toolCallId, JsonElement args, CancellationToken ct)
    {
        var agentId = GetRequiredString(args, "agent_id");
        var subagent = GetSubagent(agentId);
        if (subagent is null)
        {
            return new ToolResultData(
                toolCallId,
                $"Error: Agent '{agentId}' not found.",
                IsError: true);
        }

        var output = await subagent.WaitForCompletionAsync(ct);
        return new ToolResultData(toolCallId, output, IsError: false);
    }

    private ToolResultData ExecuteCloseAgentTool(string toolCallId, JsonElement args)
    {
        var agentId = GetRequiredString(args, "agent_id");
        var subagent = GetSubagent(agentId);
        if (subagent is null)
        {
            return new ToolResultData(
                toolCallId,
                $"Error: Agent '{agentId}' not found.",
                IsError: true);
        }

        CloseSubagent(agentId);
        return new ToolResultData(toolCallId, $"Agent '{agentId}' closed.", IsError: false);
    }

    private static bool IsSessionManagedTool(string toolName) => toolName is "spawn_agent" or "send_input" or "wait_agent" or "close_agent";

    private static string FormatSubagentState(SubagentState state) => state.ToString().ToLowerInvariant();

    private static string GetRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"'{name}' is required.", name);

        return value.GetString()!;
    }

    private static string? TryGetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.GetString() : null;

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
                    if (userTurn.Parts is { Count: > 0 })
                        messages.Add(new Message(Role.User, new List<ContentPart>(userTurn.Parts)));
                    else
                        messages.Add(Message.UserMsg(userTurn.Content));
                    break;

                case AssistantTurn assistantTurn:
                {
                    if (assistantTurn.Parts is { Count: > 0 })
                    {
                        messages.Add(new Message(
                            Role.Assistant,
                            new List<ContentPart>(assistantTurn.Parts),
                            ResponseId: assistantTurn.ResponseId));
                        break;
                    }

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
                        messages.Add(new Message(Role.Assistant, parts, ResponseId: assistantTurn.ResponseId));
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

    private static bool ShouldPersistParts(List<ContentPart> parts)
    {
        if (parts.Count == 0)
            return false;

        return parts.Any(p => p.Kind != ContentKind.Text || !string.IsNullOrWhiteSpace(p.Signature));
    }
}
