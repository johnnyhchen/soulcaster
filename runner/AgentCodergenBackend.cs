using System.Text.Json;
using System.Text.RegularExpressions;
using Soulcaster.Attractor;
using Soulcaster.CodingAgent;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.Runner;

internal static class RunnerSessionDefaults
{
    public static readonly int MaxProviderResponseMs = new SessionConfig().MaxProviderResponseMs;
}

public static class RunnerBackendFactory
{
    public static AgentCodergenBackend Create(string workingDir, string projectRoot, RunOptions options)
    {
        Func<string?, string?, string?, CodergenExecutionOptions?, Session>? sessionFactory = null;
        if (string.Equals(options.BackendMode, "scripted", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.BackendScriptPath))
                throw new InvalidOperationException("The scripted backend requires --backend-script <path>.");

            var plan = ScriptedBackendPlan.Load(options.BackendScriptPath!);
            sessionFactory = (provider, model, reasoningEffort, executionOptions) =>
            {
                var resolvedProvider = string.IsNullOrWhiteSpace(provider) ? "scripted" : provider;
                var resolvedModel = string.IsNullOrWhiteSpace(model) ? "scripted-model" : Client.ResolveModelAlias(model);
                var env = new LocalExecutionEnvironment(projectRoot);
                var profile = new ScriptedProfile(resolvedProvider, resolvedModel);
                var config = new SessionConfig(
                    MaxTurns: 50,
                    MaxToolRoundsPerInput: 50,
                    DefaultCommandTimeoutMs: 30_000,
                    MaxCommandTimeoutMs: 120_000,
                    MaxProviderResponseMs: executionOptions?.MaxProviderResponseMs ?? RunnerSessionDefaults.MaxProviderResponseMs,
                    ReasoningEffort: reasoningEffort);
                return new Session(new ScriptedProviderAdapter(plan), profile, env, config);
            };
        }

        return new AgentCodergenBackend(
            workingDir: workingDir,
            projectRoot: projectRoot,
            initialSteerText: options.SteerText,
            sessionFactory: sessionFactory,
            steerFilePath: options.SteerFilePath);
    }
}

public class AgentCodergenBackend : ICodergenBackend, IDisposable, ISessionControlBackend
{
    private const int MaxStatusParseAttempts = 3;
    private const int MaxHelperRounds = 2;
    private static readonly Regex ProviderStatusCodePattern = new(@"\b(?<status>[45]\d\d)\b", RegexOptions.Compiled);

    private readonly string _workingDir;
    private readonly string _projectRoot;
    private readonly string? _initialSteerText;
    private readonly Func<string?, string?, string?, CodergenExecutionOptions?, Session>? _sessionFactory;
    private readonly SessionPool _sessionPool = new();
    private readonly string? _steerFilePath;
    private int _initialSteerUsed;

    public AgentCodergenBackend(
        string workingDir,
        string projectRoot,
        string? initialSteerText = null,
        Func<string?, string?, string?, CodergenExecutionOptions?, Session>? sessionFactory = null,
        string? steerFilePath = null)
    {
        _workingDir = workingDir;
        _projectRoot = projectRoot;
        _initialSteerText = initialSteerText;
        _sessionFactory = sessionFactory;
        _steerFilePath = steerFilePath;
    }

    public void Dispose()
    {
        _sessionPool.Dispose();
    }

    public bool ResetThread(string threadId)
    {
        return _sessionPool.Discard(threadId);
    }

    public async Task<CodergenResult> RunAsync(
        string prompt,
        string? model = null,
        string? provider = null,
        string? reasoningEffort = null,
        CancellationToken ct = default,
        CodergenExecutionOptions? options = null)
    {
        var hints = ParseRuntimeHints(prompt);
        var resolvedProvider = provider ?? InferProvider(model);
        var carryoverMode = hints.Fidelity;
        var shouldPool = ShouldPoolSession(hints);
        Session? session = null;
        var pooledSession = false;
        var retainedForCarryover = false;
        var helperArtifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        var helperTelemetry = new List<Dictionary<string, object?>>();

        if (hints.ResumeMode.Equals("resume", StringComparison.OrdinalIgnoreCase) && shouldPool)
        {
            shouldPool = false;
            carryoverMode = "summary:high";
        }

        try
        {
            session = GetSession(
                shouldPool: shouldPool,
                hints: hints,
                resolvedProvider: resolvedProvider,
                requestedModel: model,
                reasoningEffort: reasoningEffort,
                options: options,
                pooledSession: out pooledSession);

            ApplyInitialSteer(session);
            ApplySteerFile(session);

            var carryover = BuildCarryoverPreamble(hints.ThreadId, carryoverMode);
            var effectivePrompt = string.IsNullOrWhiteSpace(carryover)
                ? prompt
                : carryover + "\n\n" + prompt;
            var stageStartedAt = DateTimeOffset.UtcNow;
            var historyStartIndex = session.History.Count;

            Console.Error.WriteLine(
                $"  [codergen] Starting agent session (model={model ?? "default"}, fidelity={hints.Fidelity}, thread={hints.ThreadId}, pooled={pooledSession})");

            StageStatusContract? parsedStatus = null;
            var parseError = string.Empty;
            var firstResponse = string.Empty;
            var finalResponse = string.Empty;
            AssistantTurn? finalTurn = null;
            string? pendingInput = effectivePrompt;
            var helperRounds = 0;
            var attemptsUsed = 0;

            while (attemptsUsed < MaxStatusParseAttempts)
            {
                attemptsUsed++;

                var input = pendingInput;
                if (string.IsNullOrWhiteSpace(input))
                    input = attemptsUsed == 1 ? effectivePrompt : BuildStageStatusReminder(parseError);
                pendingInput = null;

                ApplySteerFile(session);
                finalTurn = await session.ProcessInputAsync(input!, ct);
                finalResponse = finalTurn.Content ?? "[no response]";
                if (string.IsNullOrWhiteSpace(firstResponse))
                    firstResponse = finalResponse;

                if (IsTerminalSessionSentinel(finalResponse))
                    break;

                if (StageStatusContract.TryParseAssistantResponse(finalResponse, out parsedStatus, out parseError))
                {
                    if (parsedStatus?.BlockingQuestion is not null &&
                        helperRounds < MaxHelperRounds &&
                        await TryResolveBlockingQuestionAsync(
                            session,
                            hints,
                            parsedStatus.BlockingQuestion,
                            prompt,
                            resolvedProvider,
                            model,
                            reasoningEffort,
                            helperRounds + 1,
                            helperArtifacts,
                            helperTelemetry,
                            ct))
                    {
                        helperRounds++;
                        pendingInput = BuildAutoAnswerFollowUp(parsedStatus.BlockingQuestion, helperArtifacts[$"helper-answer-{helperRounds}.md"]);
                        continue;
                    }

                    break;
                }

                if (helperRounds < MaxHelperRounds &&
                    BlockingQuestionDetector.TryExtract(finalResponse, out var blockingQuestion) &&
                    await TryResolveBlockingQuestionAsync(
                        session,
                        hints,
                        blockingQuestion,
                        prompt,
                        resolvedProvider,
                        model,
                        reasoningEffort,
                        helperRounds + 1,
                        helperArtifacts,
                        helperTelemetry,
                        ct))
                {
                    helperRounds++;
                    pendingInput = BuildAutoAnswerFollowUp(blockingQuestion, helperArtifacts[$"helper-answer-{helperRounds}.md"]);
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(firstResponse))
                firstResponse = finalResponse;

            if (finalTurn is not null)
                Console.Error.WriteLine($"  [codergen] Session complete ({finalTurn.ToolCalls.Count} tool calls made)");

            var classification = ClassifyTerminalResponse(finalResponse, session.LastError);
            var status = parsedStatus?.Status ?? classification.Status;
            var telemetry = BuildStageTelemetry(session, historyStartIndex, stageStartedAt);
            telemetry["provider"] = session.ProviderProfile.Id;
            telemetry["model"] = session.ProviderProfile.Model;
            telemetry["provider_state"] = classification.ProviderState;
            if (!telemetry.ContainsKey("verification_state"))
                telemetry["verification_state"] = classification.DefaultVerificationState;
            telemetry["provider_timeout_ms"] = session.Config.MaxProviderResponseMs;
            if (!string.IsNullOrWhiteSpace(classification.FailureKind))
                telemetry["failure_kind"] = classification.FailureKind;
            if (classification.ProviderStatusCode is not null)
                telemetry["provider_status_code"] = classification.ProviderStatusCode.Value;
            if (classification.ProviderRetryable is not null)
                telemetry["provider_retryable"] = classification.ProviderRetryable.Value;
            if (!string.IsNullOrWhiteSpace(classification.ErrorMessage))
                telemetry["provider_error_message"] = classification.ErrorMessage;
            if (helperTelemetry.Count > 0)
            {
                telemetry["helper_session_count"] = helperTelemetry.Count;
                telemetry["helper_sessions"] = helperTelemetry;
            }

            if (!pooledSession && session.History.Count > 0)
            {
                PreserveCarryoverSession(hints.ThreadId, session);
                retainedForCarryover = true;
            }

            return new CodergenResult(
                Response: firstResponse,
                Status: status,
                ContextUpdates: parsedStatus?.ContextUpdates,
                PreferredLabel: parsedStatus?.PreferredNextLabel,
                SuggestedNextIds: parsedStatus?.SuggestedNextIds,
                StageStatus: parsedStatus,
                RawAssistantResponse: finalResponse,
                Telemetry: telemetry,
                Artifacts: helperArtifacts.Count == 0 ? null : helperArtifacts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (pooledSession)
                _sessionPool.Discard(hints.ThreadId);
            throw;
        }
        catch (Exception ex)
        {
            if (pooledSession)
                _sessionPool.Discard(hints.ThreadId);
            Console.Error.WriteLine($"  [codergen] Error: {ex.Message}");
            var classification = ClassifyException(ex);
            return new CodergenResult(
                Response: $"Agent error: {ex.Message}",
                Status: classification.Status,
                Telemetry: new Dictionary<string, object?>
                {
                    ["provider_state"] = classification.ProviderState,
                    ["verification_state"] = classification.DefaultVerificationState,
                    ["failure_kind"] = classification.FailureKind,
                    ["provider_status_code"] = classification.ProviderStatusCode,
                    ["provider_retryable"] = classification.ProviderRetryable,
                    ["provider_error_message"] = classification.ErrorMessage
                });
        }
        finally
        {
            if (!pooledSession && session is not null && !retainedForCarryover)
                session.Close();
        }
    }

    private Session GetSession(
        bool shouldPool,
        RuntimeHints hints,
        string? resolvedProvider,
        string? requestedModel,
        string? reasoningEffort,
        CodergenExecutionOptions? options,
        out bool pooledSession)
    {
        pooledSession = false;

        if (!shouldPool)
            return CreateSession(resolvedProvider, requestedModel, reasoningEffort, options);

        if (_sessionPool.TryGet(hints.ThreadId, out var existing) &&
            existing is not null &&
            IsCompatible(existing, resolvedProvider, requestedModel, reasoningEffort, options))
        {
            pooledSession = true;
            return existing;
        }

        if (existing is not null)
            _sessionPool.Discard(hints.ThreadId);

        pooledSession = true;
        return _sessionPool.GetOrCreate(
            hints.ThreadId,
            () => CreateSession(resolvedProvider, requestedModel, reasoningEffort, options));
    }

    private Session CreateSession(
        string? resolvedProvider,
        string? model,
        string? reasoningEffort,
        CodergenExecutionOptions? options)
    {
        if (_sessionFactory is not null)
            return _sessionFactory(resolvedProvider, model, reasoningEffort, options);

        var (adapter, profile) = BuildProvider(resolvedProvider);

        if (!string.IsNullOrEmpty(model))
            SetProfileModel(profile, model);

        var env = new LocalExecutionEnvironment(_projectRoot);
        var sessionConfig = new SessionConfig(
            MaxTurns: 200,
            MaxToolRoundsPerInput: 120,
            DefaultCommandTimeoutMs: 30_000,
            MaxCommandTimeoutMs: 600_000,
            MaxProviderResponseMs: options?.MaxProviderResponseMs ?? RunnerSessionDefaults.MaxProviderResponseMs,
            ReasoningEffort: reasoningEffort);

        var session = new Session(adapter, profile, env, sessionConfig);
        SubscribeSessionEvents(session);
        return session;
    }

    private static (IProviderAdapter Adapter, IProviderProfile Profile) BuildProvider(string? resolvedProvider)
    {
        var effectiveProvider = string.IsNullOrWhiteSpace(resolvedProvider)
            ? ResolveDefaultProviderFromEnvironment()
            : resolvedProvider;

        switch (effectiveProvider?.ToLowerInvariant())
        {
            case "openai":
                var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
                var openAiBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
                return (new OpenAIAdapter(openAiKey, string.IsNullOrWhiteSpace(openAiBaseUrl) ? "https://api.openai.com" : openAiBaseUrl), new OpenAIProfile());

            case "gemini":
                var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                    ?? throw new InvalidOperationException("GEMINI_API_KEY not set.");
                var geminiBaseUrl = Environment.GetEnvironmentVariable("GEMINI_BASE_URL");
                return (new GeminiAdapter(geminiKey, string.IsNullOrWhiteSpace(geminiBaseUrl) ? "https://generativelanguage.googleapis.com" : geminiBaseUrl), new GeminiProfile());

            default:
                var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
                var anthropicBaseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
                return (new AnthropicAdapter(anthropicKey, string.IsNullOrWhiteSpace(anthropicBaseUrl) ? "https://api.anthropic.com" : anthropicBaseUrl), new AnthropicProfile());
        }
    }

    private static string ResolveDefaultProviderFromEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return "anthropic";

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            return "openai";

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
            return "gemini";

        throw new InvalidOperationException("No provider API key is configured.");
    }

    private static void SubscribeSessionEvents(Session session)
    {
        session.EventEmitter.Subscribe(evt =>
        {
            switch (evt.Kind)
            {
                case EventKind.ToolCallStart:
                    var toolName = evt.Data.GetValueOrDefault("toolName");
                    Console.Error.WriteLine($"  [tool] {toolName}");
                    break;
                case EventKind.AssistantTextDelta:
                    var text = evt.Data.GetValueOrDefault("text") as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var preview = text.Length > 200 ? text[..200] + "..." : text;
                        Console.Error.Write(preview);
                    }
                    break;
            }

            return Task.CompletedTask;
        });
    }

    private bool IsCompatible(
        Session session,
        string? resolvedProvider,
        string? requestedModel,
        string? reasoningEffort,
        CodergenExecutionOptions? options)
    {
        var requestedProvider = (resolvedProvider ?? "anthropic").ToLowerInvariant();
        if (!session.ProviderProfile.Id.Equals(requestedProvider, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            var resolvedModel = Client.ResolveModelAlias(requestedModel);
            if (!session.ProviderProfile.Model.Equals(resolvedModel, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.Equals(session.Config.ReasoningEffort, reasoningEffort, StringComparison.OrdinalIgnoreCase))
            return false;

        if (session.Config.MaxProviderResponseMs != (options?.MaxProviderResponseMs ?? RunnerSessionDefaults.MaxProviderResponseMs))
            return false;

        return session.State != SessionState.Closed;
    }

    private bool ShouldPoolSession(RuntimeHints hints)
    {
        return hints.Fidelity.Equals("full", StringComparison.OrdinalIgnoreCase);
    }

    private void PreserveCarryoverSession(string threadId, Session session)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return;

        _sessionPool.Discard(threadId);
        _sessionPool.GetOrCreate(threadId, () => session);
    }

    private void ApplyInitialSteer(Session session)
    {
        if (!string.IsNullOrWhiteSpace(_initialSteerText) &&
            Interlocked.CompareExchange(ref _initialSteerUsed, 1, 0) == 0)
        {
            session.Steer(_initialSteerText!);
        }
    }

    private void ApplySteerFile(Session session)
    {
        if (string.IsNullOrWhiteSpace(_steerFilePath) || !File.Exists(_steerFilePath))
            return;

        try
        {
            var text = File.ReadAllText(_steerFilePath);
            File.Delete(_steerFilePath);
            if (!string.IsNullOrWhiteSpace(text))
                session.Steer($"[Worker Steering]\n{text.Trim()}");
        }
        catch
        {
            // Best effort. Steering files must not fail the stage.
        }
    }

    private string BuildCarryoverPreamble(string threadId, string fidelity)
    {
        if (fidelity.Equals("full", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!_sessionPool.TryGet(threadId, out var prior) || prior is null || prior.History.Count == 0)
            return string.Empty;

        var maxTurns = fidelity.StartsWith("summary:high", StringComparison.OrdinalIgnoreCase) ? 8 : 4;
        if (fidelity.Equals("truncate", StringComparison.OrdinalIgnoreCase))
            maxTurns = 2;

        var summaryLines = new List<string>();
        foreach (var turn in prior.History.TakeLast(maxTurns))
        {
            switch (turn)
            {
                case UserTurn userTurn:
                    summaryLines.Add($"- User: {TrimLine(userTurn.Content)}");
                    break;
                case AssistantTurn assistantTurn:
                    summaryLines.Add($"- Assistant: {TrimLine(assistantTurn.Content)}");
                    break;
                case SteeringTurn steeringTurn:
                    summaryLines.Add($"- Steering: {TrimLine(steeringTurn.Content)}");
                    break;
            }
        }

        if (summaryLines.Count == 0)
            return string.Empty;

        return string.Join("\n", new[]
        {
            "[CONTEXT CARRYOVER]",
            $"Thread: {threadId}",
            $"Mode: {fidelity}",
            "Prior thread summary:",
            string.Join("\n", summaryLines),
            "[/CONTEXT CARRYOVER]"
        });
    }

    private static string TrimLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Replace('\n', ' ').Trim();
        return trimmed.Length > 240 ? trimmed[..240] + "..." : trimmed;
    }

    private static string BuildStageStatusReminder(string parseError)
    {
        if (string.IsNullOrWhiteSpace(parseError))
            parseError = "Missing structured stage status JSON.";

        return
            "Your previous response did not provide valid stage status JSON.\n" +
            $"Reason: {parseError}\n" +
            "Reply ONLY with a JSON object containing keys: status, preferred_next_label, suggested_next_ids, context_updates, notes, failure_reason, blocking_question.";
    }

    private async Task<bool> TryResolveBlockingQuestionAsync(
        Session primarySession,
        RuntimeHints hints,
        BlockingQuestion blockingQuestion,
        string stagePrompt,
        string? resolvedProvider,
        string? requestedModel,
        string? reasoningEffort,
        int helperIndex,
        IDictionary<string, string> helperArtifacts,
        ICollection<Dictionary<string, object?>> helperTelemetry,
        CancellationToken ct)
    {
        var helperSession = CreateSession(
            !string.IsNullOrWhiteSpace(hints.HelperProvider) ? hints.HelperProvider : resolvedProvider,
            !string.IsNullOrWhiteSpace(hints.HelperModel) ? hints.HelperModel : requestedModel,
            !string.IsNullOrWhiteSpace(hints.HelperReasoningEffort) ? hints.HelperReasoningEffort : reasoningEffort,
            options: null);

        try
        {
            var helperStartedAt = DateTimeOffset.UtcNow;
            var helperHistoryStart = helperSession.History.Count;
            var helperTurn = await helperSession.ProcessInputAsync(
                BuildHelperPrompt(blockingQuestion, stagePrompt),
                ct);

            var helperAnswer = helperTurn.Content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(helperAnswer))
                return false;

            helperArtifacts[$"primary-question-{helperIndex}.md"] = blockingQuestion.Text;
            helperArtifacts[$"helper-answer-{helperIndex}.md"] = helperAnswer;

            var helperMetrics = BuildStageTelemetry(helperSession, helperHistoryStart, helperStartedAt);
            helperMetrics["question"] = blockingQuestion.Text;
            helperMetrics["provider"] = helperSession.ProviderProfile.Id;
            helperMetrics["model"] = helperSession.ProviderProfile.Model;
            helperTelemetry.Add(helperMetrics);

            primarySession.Steer($"Use the auto-answer for the blocking question: {blockingQuestion.Text}");
            return true;
        }
        finally
        {
            helperSession.Close();
        }
    }

    private static string BuildHelperPrompt(BlockingQuestion blockingQuestion, string stagePrompt)
    {
        return string.Join(
            "\n",
            "[SOULCASTER HELPER QUESTION]",
            "The primary coding session asked a blocking question and Soulcaster needs a concise answer so the stage can continue.",
            $"Question: {blockingQuestion.Text}",
            string.IsNullOrWhiteSpace(blockingQuestion.Context) ? string.Empty : $"Context: {blockingQuestion.Context}",
            string.IsNullOrWhiteSpace(blockingQuestion.DesiredFormat) ? string.Empty : $"Desired format: {blockingQuestion.DesiredFormat}",
            "",
            "Stage prompt excerpt:",
            TrimLine(stagePrompt),
            "",
            "Return ONLY the answer the primary session should use.",
            "[/SOULCASTER HELPER QUESTION]");
    }

    private static string BuildAutoAnswerFollowUp(BlockingQuestion blockingQuestion, string helperAnswer)
    {
        return string.Join(
            "\n",
            "[AUTO-ANSWER]",
            $"Blocking question: {blockingQuestion.Text}",
            "Helper answer:",
            helperAnswer,
            "",
            "Continue the stage using this answer and return ONLY valid stage status JSON.",
            "[/AUTO-ANSWER]");
    }

    private Dictionary<string, object?> BuildStageTelemetry(Session session, int historyStartIndex, DateTimeOffset stageStartedAt)
    {
        var stageTurns = session.History.Skip(historyStartIndex).ToList();
        var assistantTurns = stageTurns.OfType<AssistantTurn>().ToList();
        var toolCalls = assistantTurns.SelectMany(t => t.ToolCalls).ToList();
        var toolResults = stageTurns.OfType<ToolResultsTurn>().SelectMany(t => t.Results).ToList();
        var toolCallById = toolCalls.ToDictionary(tc => tc.Id, StringComparer.Ordinal);

        var usage = assistantTurns
            .Select(t => t.Usage)
            .Aggregate(Usage.Empty, (acc, next) => acc + next);

        var byTool = toolCalls
            .GroupBy(tc => tc.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (object?)g.Count(), StringComparer.OrdinalIgnoreCase);

        var allTouchedFiles = ExtractTouchedFiles(toolCalls).ToList();
        var touchedFiles = allTouchedFiles
            .Take(50)
            .Cast<object?>()
            .ToList();
        var verificationCommands = ExtractVerificationCommands(toolCalls).ToList();
        var verificationCallIds = new HashSet<string>(
            verificationCommands.Select(command => command.ToolCallId),
            StringComparer.Ordinal);
        var successfulToolCallIds = new HashSet<string>(
            toolResults
                .Where(result => !result.IsError)
                .Select(result => result.ToolCallId),
            StringComparer.Ordinal);
        var queuedValidationChecks = ExtractQueuedValidationChecks(toolCalls, successfulToolCallIds).ToList();
        var verificationErrors = toolResults.Count(result =>
            result.IsError &&
            verificationCallIds.Contains(result.ToolCallId) &&
            toolCallById.ContainsKey(result.ToolCallId));
        var verificationState = verificationCommands.Count == 0
            ? "not_run"
            : verificationErrors > 0 ? "failed" : "passed";

        return new Dictionary<string, object?>
        {
            ["duration_ms"] = (long)Math.Max(0, (DateTimeOffset.UtcNow - stageStartedAt).TotalMilliseconds),
            ["assistant_turns"] = assistantTurns.Count,
            ["tool_calls"] = toolCalls.Count,
            ["tool_errors"] = toolResults.Count(r => r.IsError),
            ["tool_by_name"] = byTool,
            ["touched_files"] = touchedFiles,
            ["touched_files_count"] = allTouchedFiles.Count,
            ["verification_commands"] = verificationCommands.Select(command => (object?)command.Command).ToList(),
            ["verification_errors"] = verificationErrors,
            ["verification_state"] = verificationState,
            ["queued_validation_checks"] = queuedValidationChecks,
            ["queued_validation_check_count"] = queuedValidationChecks.Count,
            ["token_usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = usage.InputTokens,
                ["output_tokens"] = usage.OutputTokens,
                ["total_tokens"] = usage.TotalTokens,
                ["reasoning_tokens"] = usage.ReasoningTokens,
                ["cache_read_tokens"] = usage.CacheReadTokens,
                ["cache_write_tokens"] = usage.CacheWriteTokens
            }
        };
    }

    private IEnumerable<string> ExtractTouchedFiles(IEnumerable<ToolCallData> toolCalls)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var toolCall in toolCalls)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolCall.Arguments);
                var root = doc.RootElement;
                switch (toolCall.Name.ToLowerInvariant())
                {
                    case "write_file":
                        if (TryGetString(root, out var writePath, "file_path", "path"))
                            files.Add(NormalizeTouchedPath(writePath));
                        break;
                    case "edit_file":
                        if (TryGetString(root, out var editPath, "file_path", "path"))
                            files.Add(NormalizeTouchedPath(editPath));
                        break;
                    case "apply_patch":
                        if (TryGetString(root, out var patchText, "patch"))
                            AddPatchFiles(patchText, files);
                        break;
                }
            }
            catch
            {
                // Telemetry should not fail stage execution.
            }
        }

        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<VerificationCommand> ExtractVerificationCommands(IEnumerable<ToolCallData> toolCalls)
    {
        foreach (var toolCall in toolCalls)
        {
            if (!toolCall.Name.Equals("shell", StringComparison.OrdinalIgnoreCase) &&
                !toolCall.Name.Equals("bash", StringComparison.OrdinalIgnoreCase))
                continue;

            string? command = null;
            try
            {
                using var doc = JsonDocument.Parse(toolCall.Arguments);
                if (TryGetString(doc.RootElement, out var parsedCommand, "command", "cmd"))
                    command = parsedCommand;
            }
            catch
            {
                // Verification telemetry should be best-effort only.
            }

            if (string.IsNullOrWhiteSpace(command) || !LooksLikeVerificationCommand(command))
                continue;

            yield return new VerificationCommand(toolCall.Id, command);
        }
    }

    private IEnumerable<RuntimeValidationCheckRegistration> ExtractQueuedValidationChecks(
        IEnumerable<ToolCallData> toolCalls,
        ISet<string> successfulToolCallIds)
    {
        var ordinal = 0;
        foreach (var toolCall in toolCalls)
        {
            if (!toolCall.Name.Equals("queue_validation_check", StringComparison.OrdinalIgnoreCase) ||
                !successfulToolCallIds.Contains(toolCall.Id))
            {
                continue;
            }

            if (!TryParseQueuedValidationCheck(toolCall, ordinal + 1, out var check))
                continue;

            ordinal++;
            yield return check!;
        }
    }

    private static bool TryParseQueuedValidationCheck(
        ToolCallData toolCall,
        int ordinal,
        out RuntimeValidationCheckRegistration? check)
    {
        check = null;

        try
        {
            using var doc = JsonDocument.Parse(toolCall.Arguments);
            var root = doc.RootElement;
            var kind = TryGetString(root, out var parsedKind, "kind")
                ? RuntimeValidationCheckKinds.Normalize(parsedKind)
                : RuntimeValidationCheckKinds.Command;
            var name = TryGetString(root, out var parsedName, "name") && !string.IsNullOrWhiteSpace(parsedName)
                ? parsedName
                : $"model-{kind}-{ordinal}";
            var command = TryGetString(root, out var parsedCommand, "command") ? parsedCommand : null;
            var path = TryGetString(root, out var parsedPath, "path") ? parsedPath : null;
            var paths = TryReadStringList(root, "paths");
            var workdir = TryGetString(root, out var parsedWorkdir, "workdir") ? parsedWorkdir : null;
            var containsText = TryGetString(root, out var parsedContainsText, "contains_text", "containsText") ? parsedContainsText : null;
            var matchesRegex = TryGetString(root, out var parsedRegex, "matches_regex", "matchesRegex") ? parsedRegex : null;
            var jsonPath = TryGetString(root, out var parsedJsonPath, "json_path", "jsonPath") ? parsedJsonPath : null;
            var expectedValueJson = TryGetString(root, out var parsedExpectedValueJson, "expected_value_json", "expectedValueJson") ? parsedExpectedValueJson : null;
            var expectedSchemaJson = TryGetString(root, out var parsedExpectedSchemaJson, "expected_schema_json", "expectedSchemaJson") ? parsedExpectedSchemaJson : null;
            var requireAnyChange = TryReadBoolean(root, "require_any_change", "requireAnyChange") ?? false;
            var timeoutMs = TryReadInt(root, "timeout_ms");
            var required = root.TryGetProperty("required", out var requiredElement) &&
                           requiredElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                           requiredElement.GetBoolean();

            var pathCount = (string.IsNullOrWhiteSpace(path) ? 0 : 1) + (paths?.Count ?? 0);
            var isValid = kind switch
            {
                RuntimeValidationCheckKinds.Command => !string.IsNullOrWhiteSpace(command),
                RuntimeValidationCheckKinds.FileExists => pathCount > 0,
                RuntimeValidationCheckKinds.FileContent => pathCount == 1 &&
                                                           (!string.IsNullOrWhiteSpace(containsText) ||
                                                            !string.IsNullOrWhiteSpace(matchesRegex) ||
                                                            !string.IsNullOrWhiteSpace(jsonPath)),
                RuntimeValidationCheckKinds.Diff => true,
                RuntimeValidationCheckKinds.Artifact => pathCount > 0,
                RuntimeValidationCheckKinds.Schema => pathCount == 1 && !string.IsNullOrWhiteSpace(expectedSchemaJson),
                _ => false
            };
            if (!isValid)
                return false;

            check = new RuntimeValidationCheckRegistration(
                Kind: kind,
                Name: name,
                Command: command,
                Path: path,
                Paths: paths,
                Workdir: workdir,
                ContainsText: containsText,
                MatchesRegex: matchesRegex,
                JsonPath: jsonPath,
                ExpectedValueJson: expectedValueJson,
                ExpectedSchemaJson: expectedSchemaJson,
                RequireAnyChange: requireAnyChange,
                TimeoutMs: timeoutMs,
                Required: required,
                Source: RuntimeValidationCheckSources.ModelRequested,
                SourceReference: toolCall.Id);
            return true;
        }
        catch
        {
            // Structured validation telemetry is best-effort.
            return false;
        }
    }

    private string NormalizeTouchedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try
        {
            var absolute = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, _projectRoot);

            if (absolute.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(_projectRoot, absolute);

            return absolute;
        }
        catch
        {
            return path;
        }
    }

    private void AddPatchFiles(string patch, HashSet<string> files)
    {
        using var reader = new StringReader(patch ?? string.Empty);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryExtractPatchPath(line, out var patchPath))
                files.Add(NormalizeTouchedPath(patchPath));
        }
    }

    private static bool TryExtractPatchPath(string line, out string path)
    {
        path = string.Empty;
        var prefixes = new[]
        {
            "*** Update File: ",
            "*** Add File: ",
            "*** Delete File: ",
            "--- ",
            "+++ "
        };

        foreach (var prefix in prefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var candidate = line[prefix.Length..].Trim();
            if (candidate.StartsWith("a/", StringComparison.Ordinal) || candidate.StartsWith("b/", StringComparison.Ordinal))
                candidate = candidate[2..];

            if (candidate == "/dev/null" || string.IsNullOrWhiteSpace(candidate))
                return false;

            path = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement root, out string value, params string[] keys)
    {
        value = string.Empty;
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                continue;

            var text = el.GetString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            value = text;
            return true;
        }

        return false;
    }

    private static int? TryReadInt(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var number))
                return number;

            if (el.ValueKind == JsonValueKind.String &&
                int.TryParse(el.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? TryReadBoolean(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var el))
                continue;

            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return el.GetBoolean();

            if (el.ValueKind == JsonValueKind.String &&
                bool.TryParse(el.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadStringList(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.String)
            {
                var single = el.GetString();
                return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single };
            }

            if (el.ValueKind != JsonValueKind.Array)
                continue;

            return el.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList();
        }

        return null;
    }

    private static TerminalResponseClassification ClassifyTerminalResponse(string response, Exception? error = null)
    {
        if (response.StartsWith("[Model response timeout reached]", StringComparison.Ordinal))
            return new TerminalResponseClassification(OutcomeStatus.Fail, "timeout", "provider_timeout");

        if (error is not null)
            return ClassifyException(error);

        if (response.StartsWith("[Error:", StringComparison.Ordinal))
            return ClassifyErrorMessage(ExtractErrorText(response));

        if (response.StartsWith("[Turn limit reached]", StringComparison.Ordinal))
            return new TerminalResponseClassification(OutcomeStatus.Retry, "completed", "turn_limit");

        if (response.StartsWith("[Tool round limit reached]", StringComparison.Ordinal))
            return new TerminalResponseClassification(OutcomeStatus.Retry, "completed", "tool_round_limit");

        if (response.StartsWith("[Exploration stall detected", StringComparison.Ordinal))
            return new TerminalResponseClassification(OutcomeStatus.Retry, "completed", "exploration_stall");

        return new TerminalResponseClassification(OutcomeStatus.Success, "completed", null);
    }

    private static TerminalResponseClassification ClassifyException(Exception error)
    {
        if (error is TimeoutException)
            return new TerminalResponseClassification(OutcomeStatus.Fail, "timeout", "provider_timeout", ErrorMessage: error.Message);

        return error switch
        {
            AuthenticationError auth =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "auth_failed",
                    "provider_auth",
                    ProviderStatusCode: (int)auth.StatusCode,
                    ProviderRetryable: auth.Retryable,
                    ErrorMessage: auth.Message),
            RateLimitError rateLimit =>
                new TerminalResponseClassification(
                    OutcomeStatus.Retry,
                    "rate_limited",
                    "provider_rate_limit",
                    ProviderStatusCode: (int)rateLimit.StatusCode,
                    ProviderRetryable: rateLimit.Retryable,
                    ErrorMessage: rateLimit.Message),
            NotFoundError notFound =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "not_found",
                    "provider_not_found",
                    ProviderStatusCode: (int)notFound.StatusCode,
                    ProviderRetryable: notFound.Retryable,
                    ErrorMessage: notFound.Message),
            ContentFilterError contentFilter =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "content_filtered",
                    "provider_content_filter",
                    ProviderStatusCode: (int)contentFilter.StatusCode,
                    ProviderRetryable: contentFilter.Retryable,
                    ErrorMessage: contentFilter.Message),
            ConfigurationError configuration =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "config_error",
                    "configuration_error",
                    ErrorMessage: configuration.Message),
            ProviderError providerError when providerError.Retryable =>
                new TerminalResponseClassification(
                    OutcomeStatus.Retry,
                    "transient_error",
                    "provider_transient_error",
                    ProviderStatusCode: (int)providerError.StatusCode,
                    ProviderRetryable: providerError.Retryable,
                    ErrorMessage: providerError.Message),
            ProviderError providerError when LooksLikeNotFoundMessage(providerError.Message) =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "not_found",
                    "provider_not_found",
                    ProviderStatusCode: (int)providerError.StatusCode,
                    ProviderRetryable: providerError.Retryable,
                    ErrorMessage: providerError.Message),
            ProviderError providerError =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "error",
                    "provider_error",
                    ProviderStatusCode: (int)providerError.StatusCode,
                    ProviderRetryable: providerError.Retryable,
                    ErrorMessage: providerError.Message),
            _ =>
                new TerminalResponseClassification(
                    OutcomeStatus.Fail,
                    "runtime_error",
                    "runtime_error",
                    ErrorMessage: error.Message)
        };
    }

    private static TerminalResponseClassification ClassifyErrorMessage(string errorText)
    {
        var normalized = errorText.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return new TerminalResponseClassification(OutcomeStatus.Fail, "error", "provider_error");

        var providerStatusCode = TryExtractProviderStatusCode(errorText, out var parsedStatusCode)
            ? parsedStatusCode
            : normalized.Contains("too many requests", StringComparison.Ordinal) ? 429
            : normalized.Contains("high demand", StringComparison.Ordinal) ? 503
            : (int?)null;

        if (normalized.Contains("rate limit", StringComparison.Ordinal) ||
            normalized.Contains("too many requests", StringComparison.Ordinal) ||
            normalized.Contains("high demand", StringComparison.Ordinal))
        {
            return new TerminalResponseClassification(
                OutcomeStatus.Retry,
                "rate_limited",
                "provider_rate_limit",
                ProviderStatusCode: providerStatusCode,
                ProviderRetryable: true,
                ErrorMessage: errorText);
        }

        if (providerStatusCode is 401 or 403 ||
            normalized.Contains("unauthorized", StringComparison.Ordinal) ||
            normalized.Contains("forbidden", StringComparison.Ordinal) ||
            normalized.Contains("api key", StringComparison.Ordinal) ||
            normalized.Contains("authentication", StringComparison.Ordinal))
        {
            return new TerminalResponseClassification(
                OutcomeStatus.Fail,
                "auth_failed",
                "provider_auth",
                ProviderStatusCode: providerStatusCode,
                ProviderRetryable: false,
                ErrorMessage: errorText);
        }

        if (providerStatusCode == 404 ||
            normalized.Contains("not found", StringComparison.Ordinal) ||
            normalized.Contains("is not found", StringComparison.Ordinal) ||
            normalized.Contains("does not exist", StringComparison.Ordinal))
        {
            return new TerminalResponseClassification(
                OutcomeStatus.Fail,
                "not_found",
                "provider_not_found",
                ProviderStatusCode: providerStatusCode,
                ProviderRetryable: false,
                ErrorMessage: errorText);
        }

        if (normalized.Contains("blocked", StringComparison.Ordinal) ||
            normalized.Contains("content filter", StringComparison.Ordinal) ||
            normalized.Contains("safety", StringComparison.Ordinal))
        {
            return new TerminalResponseClassification(
                OutcomeStatus.Fail,
                "content_filtered",
                "provider_content_filter",
                ProviderStatusCode: providerStatusCode,
                ProviderRetryable: false,
                ErrorMessage: errorText);
        }

        if (normalized.Contains("missing thought_signature", StringComparison.Ordinal) ||
            normalized.Contains("thought signature", StringComparison.Ordinal))
        {
            return new TerminalResponseClassification(
                OutcomeStatus.Fail,
                "error",
                "provider_protocol_error",
                ProviderStatusCode: providerStatusCode,
                ProviderRetryable: false,
                ErrorMessage: errorText);
        }

        if (providerStatusCode >= 500)
        {
            return new TerminalResponseClassification(
                OutcomeStatus.Retry,
                "transient_error",
                "provider_transient_error",
                ProviderStatusCode: providerStatusCode,
                ProviderRetryable: true,
                ErrorMessage: errorText);
        }

        return new TerminalResponseClassification(
            OutcomeStatus.Fail,
            "error",
            "provider_error",
            ProviderStatusCode: providerStatusCode,
            ProviderRetryable: providerStatusCode is not null ? false : null,
            ErrorMessage: errorText);
    }

    private static bool TryExtractProviderStatusCode(string errorText, out int statusCode)
    {
        var match = ProviderStatusCodePattern.Match(errorText);
        if (match.Success &&
            int.TryParse(match.Groups["status"].Value, out statusCode))
        {
            return true;
        }

        statusCode = default;
        return false;
    }

    private static string ExtractErrorText(string response)
    {
        if (!response.StartsWith("[Error:", StringComparison.Ordinal))
            return response;

        var trimmed = response.Trim();
        if (trimmed.EndsWith("]", StringComparison.Ordinal))
            trimmed = trimmed[..^1];

        return trimmed["[Error:".Length..].Trim();
    }

    private static bool LooksLikeNotFoundMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized = message.Trim().ToLowerInvariant();
        return normalized.Contains("not found", StringComparison.Ordinal) ||
               normalized.Contains("is not found", StringComparison.Ordinal) ||
               normalized.Contains("does not exist", StringComparison.Ordinal);
    }

    private static bool IsTerminalSessionSentinel(string response)
    {
        return response.StartsWith("[Error:", StringComparison.Ordinal) ||
               response.StartsWith("[Turn limit reached]", StringComparison.Ordinal) ||
               response.StartsWith("[Tool round limit reached]", StringComparison.Ordinal) ||
               response.StartsWith("[Model response timeout reached]", StringComparison.Ordinal) ||
               response.StartsWith("[Exploration stall detected", StringComparison.Ordinal);
    }

    private static RuntimeHints ParseRuntimeHints(string prompt)
    {
        var fidelity = ExtractHint(prompt, "Runtime fidelity") ?? "full";
        var thread = ExtractHint(prompt, "Runtime thread") ?? "default";
        var resumeMode = ExtractHint(prompt, "Resume mode") ?? "fresh";
        var helperProvider = ExtractHint(prompt, "Helper provider");
        var helperModel = ExtractHint(prompt, "Helper model");
        var helperReasoningEffort = ExtractHint(prompt, "Helper reasoning effort");

        return new RuntimeHints(
            Fidelity: fidelity.Trim(),
            ThreadId: thread.Trim(),
            ResumeMode: resumeMode.Trim(),
            HelperProvider: helperProvider?.Trim(),
            HelperModel: helperModel?.Trim(),
            HelperReasoningEffort: helperReasoningEffort?.Trim());
    }

    private static string? ExtractHint(string prompt, string key)
    {
        var pattern = @"^\s*" + Regex.Escape(key) + @"\s*:\s*(.+?)\s*$";
        var match = Regex.Match(prompt, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? InferProvider(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return null;

        var lower = model.ToLowerInvariant();
        if (lower.StartsWith("claude", StringComparison.Ordinal))
            return "anthropic";
        if (lower.StartsWith("gpt", StringComparison.Ordinal) || lower.StartsWith("o1", StringComparison.Ordinal) || lower.StartsWith("o3", StringComparison.Ordinal) || lower.StartsWith("o4", StringComparison.Ordinal) || lower.StartsWith("codex", StringComparison.Ordinal))
            return "openai";
        if (lower.StartsWith("gemini", StringComparison.Ordinal))
            return "gemini";
        return null;
    }

    private static bool LooksLikeVerificationCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var separators = new[] { "&&", "||", ";", "\r\n", "\n" };
        var segments = command.Split(separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(LooksLikeVerificationSegment);
    }

    private static bool LooksLikeVerificationSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var normalized = Regex.Replace(segment.ToLowerInvariant(), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.StartsWith("cd ", StringComparison.Ordinal) ||
            normalized.StartsWith("pwd", StringComparison.Ordinal) ||
            normalized.StartsWith("echo ", StringComparison.Ordinal) ||
            normalized.StartsWith("export ", StringComparison.Ordinal) ||
            normalized.StartsWith("set ", StringComparison.Ordinal) ||
            normalized.StartsWith("source ", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("dotnet test", StringComparison.Ordinal) ||
               normalized.Contains("dotnet build", StringComparison.Ordinal) ||
               normalized.Contains("dotnet msbuild", StringComparison.Ordinal) ||
               normalized.Contains("dotnet format --verify-no-changes", StringComparison.Ordinal) ||
               normalized.Contains("cargo test", StringComparison.Ordinal) ||
               normalized.Contains("cargo check", StringComparison.Ordinal) ||
               normalized.Contains("cargo clippy", StringComparison.Ordinal) ||
               normalized.Contains("cargo nextest", StringComparison.Ordinal) ||
               normalized.Contains("cargo fmt --check", StringComparison.Ordinal) ||
               normalized.Contains("go test", StringComparison.Ordinal) ||
               normalized.Contains("go vet", StringComparison.Ordinal) ||
               normalized.Contains("go build", StringComparison.Ordinal) ||
               normalized.Contains("pytest", StringComparison.Ordinal) ||
               normalized.Contains("python -m pytest", StringComparison.Ordinal) ||
               normalized.Contains("python -m unittest", StringComparison.Ordinal) ||
               normalized.Contains("uv run pytest", StringComparison.Ordinal) ||
               normalized.Contains("uv run ruff", StringComparison.Ordinal) ||
               normalized.Contains("uv run mypy", StringComparison.Ordinal) ||
               normalized.Contains("ruff check", StringComparison.Ordinal) ||
               normalized.Contains("mypy", StringComparison.Ordinal) ||
               normalized.Contains("eslint", StringComparison.Ordinal) ||
               normalized.Contains("tsc --noemit", StringComparison.Ordinal) ||
               normalized.Contains("jest", StringComparison.Ordinal) ||
               normalized.Contains("vitest", StringComparison.Ordinal) ||
               normalized.Contains("playwright", StringComparison.Ordinal) ||
               normalized.Contains("cypress", StringComparison.Ordinal) ||
               normalized.Contains("gradle test", StringComparison.Ordinal) ||
               normalized.Contains("gradle build", StringComparison.Ordinal) ||
               normalized.Contains("gradle check", StringComparison.Ordinal) ||
               normalized.Contains("mvn test", StringComparison.Ordinal) ||
               normalized.Contains("mvn verify", StringComparison.Ordinal) ||
               normalized.Contains("bazel test", StringComparison.Ordinal) ||
               normalized.Contains("bazel build", StringComparison.Ordinal) ||
               normalized.Contains("npm test", StringComparison.Ordinal) ||
               normalized.Contains("pnpm test", StringComparison.Ordinal) ||
               normalized.Contains("yarn test", StringComparison.Ordinal) ||
               normalized.Contains("bun test", StringComparison.Ordinal) ||
               normalized.Contains("npm run build", StringComparison.Ordinal) ||
               normalized.Contains("pnpm build", StringComparison.Ordinal) ||
               normalized.Contains("yarn build", StringComparison.Ordinal) ||
               normalized.Contains("npm run lint", StringComparison.Ordinal) ||
               normalized.Contains("pnpm lint", StringComparison.Ordinal) ||
               normalized.Contains("yarn lint", StringComparison.Ordinal) ||
               normalized.Contains("pnpm exec", StringComparison.Ordinal) ||
               LooksLikeVerificationTargetCommand(normalized) ||
               LooksLikeVerificationScriptInvocation(normalized);
    }

    private static bool LooksLikeVerificationTargetCommand(string normalized)
    {
        foreach (var prefix in new[] { "make ", "just ", "task ", "npm run ", "pnpm run ", "yarn ", "bun run " })
        {
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var target = normalized[prefix.Length..].Trim();
            return target.StartsWith("test", StringComparison.Ordinal) ||
                   target.StartsWith("build", StringComparison.Ordinal) ||
                   target.StartsWith("check", StringComparison.Ordinal) ||
                   target.StartsWith("verify", StringComparison.Ordinal) ||
                   target.StartsWith("validate", StringComparison.Ordinal) ||
                   target.StartsWith("lint", StringComparison.Ordinal) ||
                   target.StartsWith("ci", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool LooksLikeVerificationScriptInvocation(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var scriptToken = tokens[0];
        if ((scriptToken is "bash" or "sh" or "zsh" or "pwsh" or "python" or "python3") && tokens.Length > 1)
            scriptToken = tokens[1];

        var fileName = Path.GetFileNameWithoutExtension(scriptToken.Trim('"', '\''));
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var lowered = fileName.ToLowerInvariant();
        return lowered.Contains("test", StringComparison.Ordinal) ||
               lowered.Contains("build", StringComparison.Ordinal) ||
               lowered.Contains("check", StringComparison.Ordinal) ||
               lowered.Contains("verify", StringComparison.Ordinal) ||
               lowered.Contains("validate", StringComparison.Ordinal) ||
               lowered.Contains("lint", StringComparison.Ordinal) ||
               lowered.Equals("ci", StringComparison.Ordinal);
    }

    private static void SetProfileModel(IProviderProfile profile, string model)
    {
        var resolved = Client.ResolveModelAlias(model);

        switch (profile)
        {
            case AnthropicProfile ap:
                ap.Model = resolved;
                break;
            case OpenAIProfile op:
                op.Model = resolved;
                break;
            case GeminiProfile gp:
                gp.Model = resolved;
                break;
            case ScriptedProfile sp:
                sp.Model = resolved;
                break;
        }
    }

    private sealed record RuntimeHints(
        string Fidelity,
        string ThreadId,
        string ResumeMode,
        string? HelperProvider,
        string? HelperModel,
        string? HelperReasoningEffort);

    private sealed record VerificationCommand(string ToolCallId, string Command);

    private sealed record TerminalResponseClassification(
        OutcomeStatus Status,
        string ProviderState,
        string? FailureKind,
        int? ProviderStatusCode = null,
        bool? ProviderRetryable = null,
        string? ErrorMessage = null)
    {
        public string DefaultVerificationState => "not_run";
    }
}

internal static class BlockingQuestionDetector
{
    private static readonly Regex QuestionLinePattern =
        new(@"^(?<line>.*\?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static bool TryExtract(string text, out BlockingQuestion question)
    {
        question = default!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            return false;

        var matches = QuestionLinePattern.Matches(trimmed);
        if (matches.Count == 0)
            return false;

        var candidate = matches[^1].Groups["line"].Value.Trim();
        if (candidate.Length < 6)
            return false;

        question = new BlockingQuestion(candidate);
        return true;
    }
}

public sealed class ScriptedBackendPlan
{
    public Dictionary<string, List<ScriptedResponsePlan>> Nodes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<ScriptedHelperPlan> Helpers { get; init; } = new();

    public ScriptedResponsePlan? DefaultResponse { get; init; }

    public static ScriptedBackendPlan Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ScriptedBackendPlan>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
               new ScriptedBackendPlan();
    }
}

public sealed class ScriptedResponsePlan
{
    public string? AssistantText { get; init; }
    public List<string>? MustContain { get; init; }
    public List<string>? MustNotContain { get; init; }
    public int DelayMs { get; init; }
}

public sealed class ScriptedHelperPlan
{
    public string MatchContains { get; init; } = "";
    public string AssistantText { get; init; } = "";
}

internal sealed class ScriptedProviderAdapter : IProviderAdapter
{
    private static readonly Regex NodeIdPattern =
        new(@"executing node ""(?<id>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ScriptedBackendPlan _plan;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _nodeCounters = new(StringComparer.OrdinalIgnoreCase);
    private int _helperCounter;

    public ScriptedProviderAdapter(ScriptedBackendPlan plan)
    {
        _plan = plan;
    }

    public string Name => "scripted";

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        var lastUser = request.Messages.LastOrDefault(message => message.Role == Role.User)?.Text ?? string.Empty;
        var responseText = IsHelperPrompt(lastUser)
            ? ResolveHelperResponse(lastUser)
            : ResolveNodeResponse(request.Messages);

        return Task.FromResult(new Response(
            Id: Guid.NewGuid().ToString("N"),
            Model: request.Model,
            Provider: Name,
            Message: Message.AssistantMsg(responseText),
            FinishReason: FinishReason.Stop,
            Usage: Usage.Empty));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Request request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await CompleteAsync(request, ct);
        yield return new StreamEvent { Type = StreamEventType.TextStart };
        yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = response.Text };
        yield return new StreamEvent { Type = StreamEventType.TextEnd };
        yield return new StreamEvent { Type = StreamEventType.Finish, FinishReason = response.FinishReason, Response = response };
    }

    private string ResolveNodeResponse(IReadOnlyList<Message> messages)
    {
        var transcript = string.Join(
            "\n",
            messages.Select(message => $"[{message.Role}] {message.Text}"));
        var nodeId = ExtractNodeId(messages);
        if (!_plan.Nodes.TryGetValue(nodeId, out var queue) || queue.Count == 0)
            return BuildResponseText(_plan.DefaultResponse, transcript, nodeId);

        ScriptedResponsePlan responsePlan;
        lock (_lock)
        {
            var index = _nodeCounters.TryGetValue(nodeId, out var current) ? current : 0;
            responsePlan = queue[Math.Min(index, queue.Count - 1)];
            _nodeCounters[nodeId] = index + 1;
        }

        return BuildResponseText(responsePlan, transcript, nodeId);
    }

    private string ResolveHelperResponse(string prompt)
    {
        ScriptedHelperPlan? helperPlan = null;
        lock (_lock)
        {
            if (_helperCounter < _plan.Helpers.Count)
            {
                helperPlan = _plan.Helpers[_helperCounter];
                _helperCounter++;
            }
        }

        if (helperPlan is not null &&
            (string.IsNullOrWhiteSpace(helperPlan.MatchContains) ||
             prompt.Contains(helperPlan.MatchContains, StringComparison.OrdinalIgnoreCase)))
        {
            return helperPlan.AssistantText;
        }

        return "Use the default scripted helper answer.";
    }

    private static bool IsHelperPrompt(string prompt)
    {
        return prompt.Contains("[SOULCASTER HELPER QUESTION]", StringComparison.Ordinal);
    }

    private static string ExtractNodeId(IReadOnlyList<Message> messages)
    {
        foreach (var message in messages.Reverse())
        {
            if (message.Role != Role.User)
                continue;

            var matches = NodeIdPattern.Matches(message.Text ?? string.Empty);
            if (matches.Count > 0)
                return matches[^1].Groups["id"].Value;
        }

        return "default";
    }

    private static string BuildResponseText(ScriptedResponsePlan? plan, string prompt, string nodeId)
    {
        if (plan is null)
            return BuildDefaultStageStatus($"scripted default for {nodeId}");

        if (plan.DelayMs > 0)
            Thread.Sleep(plan.DelayMs);

        foreach (var required in plan.MustContain ?? new List<string>())
        {
            if (!prompt.Contains(required, StringComparison.Ordinal))
                return BuildFailureStageStatus($"scripted assertion failed for {nodeId}: missing '{required}'");
        }

        foreach (var forbidden in plan.MustNotContain ?? new List<string>())
        {
            if (prompt.Contains(forbidden, StringComparison.Ordinal))
                return BuildFailureStageStatus($"scripted assertion failed for {nodeId}: found forbidden '{forbidden}'");
        }

        return string.IsNullOrWhiteSpace(plan.AssistantText)
            ? BuildDefaultStageStatus($"scripted default for {nodeId}")
            : plan.AssistantText!;
    }

    private static string BuildDefaultStageStatus(string notes)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "success",
            ["preferred_next_label"] = "",
            ["suggested_next_ids"] = Array.Empty<string>(),
            ["context_updates"] = new Dictionary<string, string>(),
            ["notes"] = notes
        });
    }

    private static string BuildFailureStageStatus(string notes)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "fail",
            ["preferred_next_label"] = "",
            ["suggested_next_ids"] = Array.Empty<string>(),
            ["context_updates"] = new Dictionary<string, string>(),
            ["notes"] = notes,
            ["failure_reason"] = notes
        });
    }
}
