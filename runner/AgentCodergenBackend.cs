using System.Text.Json;
using System.Text.RegularExpressions;
using JcAttractor.Attractor;
using JcAttractor.CodingAgent;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.Runner;

public static class RunnerBackendFactory
{
    public static AgentCodergenBackend Create(string workingDir, string projectRoot, RunOptions options)
    {
        Func<string?, string?, string?, Session>? sessionFactory = null;
        if (string.Equals(options.BackendMode, "scripted", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.BackendScriptPath))
                throw new InvalidOperationException("The scripted backend requires --backend-script <path>.");

            var plan = ScriptedBackendPlan.Load(options.BackendScriptPath!);
            sessionFactory = (provider, model, reasoningEffort) =>
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

    private readonly string _workingDir;
    private readonly string _projectRoot;
    private readonly string? _initialSteerText;
    private readonly Func<string?, string?, string?, Session>? _sessionFactory;
    private readonly SessionPool _sessionPool = new();
    private readonly string? _steerFilePath;
    private int _initialSteerUsed;

    public AgentCodergenBackend(
        string workingDir,
        string projectRoot,
        string? initialSteerText = null,
        Func<string?, string?, string?, Session>? sessionFactory = null,
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
        CancellationToken ct = default)
    {
        var hints = ParseRuntimeHints(prompt);
        var resolvedProvider = provider ?? InferProvider(model);
        var carryoverMode = hints.Fidelity;
        var shouldPool = ShouldPoolSession(hints);
        Session? session = null;
        var pooledSession = false;
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
                pooledSession: out pooledSession);

            ApplyInitialSteer(session);
            ApplySteerFile(session);

            var carryover = BuildCarryoverPreamble(hints.ThreadId, carryoverMode);
            var effectivePrompt = string.IsNullOrWhiteSpace(carryover)
                ? prompt
                : carryover + "\n\n" + prompt;
            var stageStartedAt = DateTimeOffset.UtcNow;
            var historyStartIndex = session.History.Count;

            Console.WriteLine(
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
                Console.WriteLine($"  [codergen] Session complete ({finalTurn.ToolCalls.Count} tool calls made)");

            var status = parsedStatus?.Status ?? InferStatusFromResponse(finalResponse);
            var telemetry = BuildStageTelemetry(session, historyStartIndex, stageStartedAt);
            if (helperTelemetry.Count > 0)
            {
                telemetry["helper_session_count"] = helperTelemetry.Count;
                telemetry["helper_sessions"] = helperTelemetry;
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
            return new CodergenResult(
                Response: $"Agent error: {ex.Message}",
                Status: OutcomeStatus.Retry);
        }
        finally
        {
            if (!pooledSession && session is not null)
                session.Close();
        }
    }

    private Session GetSession(
        bool shouldPool,
        RuntimeHints hints,
        string? resolvedProvider,
        string? requestedModel,
        string? reasoningEffort,
        out bool pooledSession)
    {
        pooledSession = false;

        if (!shouldPool)
            return CreateSession(resolvedProvider, requestedModel, reasoningEffort);

        if (_sessionPool.TryGet(hints.ThreadId, out var existing) &&
            existing is not null &&
            IsCompatible(existing, resolvedProvider, requestedModel, reasoningEffort))
        {
            pooledSession = true;
            return existing;
        }

        if (existing is not null)
            _sessionPool.Discard(hints.ThreadId);

        pooledSession = true;
        return _sessionPool.GetOrCreate(
            hints.ThreadId,
            () => CreateSession(resolvedProvider, requestedModel, reasoningEffort));
    }

    private Session CreateSession(string? resolvedProvider, string? model, string? reasoningEffort)
    {
        if (_sessionFactory is not null)
            return _sessionFactory(resolvedProvider, model, reasoningEffort);

        var (adapter, profile) = BuildProvider(resolvedProvider);

        if (!string.IsNullOrEmpty(model))
            SetProfileModel(profile, model);

        var env = new LocalExecutionEnvironment(_projectRoot);
        var sessionConfig = new SessionConfig(
            MaxTurns: 200,
            MaxToolRoundsPerInput: 300,
            DefaultCommandTimeoutMs: 30_000,
            MaxCommandTimeoutMs: 600_000,
            ReasoningEffort: reasoningEffort);

        var session = new Session(adapter, profile, env, sessionConfig);
        SubscribeSessionEvents(session);
        return session;
    }

    private static (IProviderAdapter Adapter, IProviderProfile Profile) BuildProvider(string? resolvedProvider)
    {
        switch (resolvedProvider?.ToLowerInvariant())
        {
            case "openai":
                var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
                return (new OpenAiAdapter(openAiKey), new OpenAiProfile());

            case "gemini":
                var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                    ?? throw new InvalidOperationException("GEMINI_API_KEY not set.");
                return (new GeminiAdapter(geminiKey), new GeminiProfile());

            default:
                var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
                return (new AnthropicAdapter(anthropicKey), new AnthropicProfile());
        }
    }

    private static void SubscribeSessionEvents(Session session)
    {
        session.EventEmitter.Subscribe(evt =>
        {
            switch (evt.Kind)
            {
                case EventKind.ToolCallStart:
                    var toolName = evt.Data.GetValueOrDefault("toolName");
                    Console.WriteLine($"  [tool] {toolName}");
                    break;
                case EventKind.AssistantTextDelta:
                    var text = evt.Data.GetValueOrDefault("text") as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var preview = text.Length > 200 ? text[..200] + "..." : text;
                        Console.Write(preview);
                    }
                    break;
            }

            return Task.CompletedTask;
        });
    }

    private bool IsCompatible(Session session, string? resolvedProvider, string? requestedModel, string? reasoningEffort)
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

        return session.State != SessionState.Closed;
    }

    private bool ShouldPoolSession(RuntimeHints hints)
    {
        return hints.Fidelity.Equals("full", StringComparison.OrdinalIgnoreCase);
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
            !string.IsNullOrWhiteSpace(hints.HelperReasoningEffort) ? hints.HelperReasoningEffort : reasoningEffort);

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

        var usage = assistantTurns
            .Select(t => t.Usage)
            .Aggregate(Usage.Empty, (acc, next) => acc + next);

        var byTool = toolCalls
            .GroupBy(tc => tc.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (object?)g.Count(), StringComparer.OrdinalIgnoreCase);

        var touchedFiles = ExtractTouchedFiles(toolCalls)
            .Take(50)
            .Cast<object?>()
            .ToList();

        return new Dictionary<string, object?>
        {
            ["duration_ms"] = (long)Math.Max(0, (DateTimeOffset.UtcNow - stageStartedAt).TotalMilliseconds),
            ["assistant_turns"] = assistantTurns.Count,
            ["tool_calls"] = toolCalls.Count,
            ["tool_errors"] = toolResults.Count(r => r.IsError),
            ["tool_by_name"] = byTool,
            ["touched_files"] = touchedFiles,
            ["touched_files_count"] = touchedFiles.Count,
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

    private static OutcomeStatus InferStatusFromResponse(string response)
    {
        if (response.StartsWith("[Error:", StringComparison.Ordinal) ||
            response.StartsWith("[Turn limit reached]", StringComparison.Ordinal) ||
            response.StartsWith("[Tool round limit reached]", StringComparison.Ordinal))
        {
            return OutcomeStatus.Retry;
        }

        return OutcomeStatus.Success;
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

    private static void SetProfileModel(IProviderProfile profile, string model)
    {
        var resolved = Client.ResolveModelAlias(model);

        switch (profile)
        {
            case AnthropicProfile ap:
                ap.Model = resolved;
                break;
            case OpenAiProfile op:
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

            var match = NodeIdPattern.Match(message.Text ?? string.Empty);
            if (match.Success)
                return match.Groups["id"].Value;
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
