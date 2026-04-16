namespace Soulcaster.Attractor.Handlers;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public interface ICodergenBackend
{
    Task<CodergenResult> RunAsync(
        string prompt,
        string? model = null,
        string? provider = null,
        string? reasoningEffort = null,
        CancellationToken ct = default,
        CodergenExecutionOptions? options = null);
}

public record CodergenResult(
    string Response,
    OutcomeStatus Status,
    Dictionary<string, string>? ContextUpdates = null,
    string? PreferredLabel = null,
    List<string>? SuggestedNextIds = null,
    StageStatusContract? StageStatus = null,
    string? RawAssistantResponse = null,
    Dictionary<string, object?>? Telemetry = null,
    Dictionary<string, string>? Artifacts = null);

public class CodergenHandler : INodeHandler
{
    private const int MaxContractAttempts = 3;

    private readonly ICodergenBackend _backend;

    public CodergenHandler(ICodergenBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        var executionOptions = BuildExecutionOptions(node, graph);

        // Expand variables before adding runtime contract instructions.
        var expandedPrompt = ExpandVariables(node.Prompt, context, graph);
        var gatesRoot = Path.Combine(Path.GetDirectoryName(logsRoot) ?? logsRoot, "gates");
        expandedPrompt = RewriteArtifactPaths(expandedPrompt, logsRoot, gatesRoot);

        // Create stage directory and write prompt.
        var stageDir = RuntimeStageResolver.ResolveStageDir(logsRoot, context, node.Id);
        Directory.CreateDirectory(stageDir);
        var artifactPaths = BuildArtifactPaths(stageDir);

        var outgoingLabels = graph.OutgoingEdges(node.Id)
            .Where(e => !string.IsNullOrWhiteSpace(e.Label))
            .Select(e => e.Label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        expandedPrompt =
            BuildPreamble(node, graph, logsRoot, context, outgoingLabels, executionOptions) +
            BuildStageContractInstructions(outgoingLabels) +
            expandedPrompt;

        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), expandedPrompt, ct);
        await WriteJsonArtifactAsync(artifactPaths.EffectivePolicyPath, BuildEffectivePolicy(executionOptions), ct);

        var statusPath = Path.Combine(stageDir, "status.json");
        ArchivePreviousStatus(statusPath);

        var model = !string.IsNullOrEmpty(node.LlmModel) ? node.LlmModel : null;
        var provider = !string.IsNullOrEmpty(node.LlmProvider) ? node.LlmProvider : null;
        var reasoningEffort = !string.IsNullOrEmpty(node.ReasoningEffort) ? node.ReasoningEffort : null;

        CodergenResult result = new(
            Response: "No backend result produced.",
            Status: OutcomeStatus.Fail);

        StageStatusContract? stageStatus = null;
        var contractError = string.Empty;
        var attemptsUsed = 0;

        var attemptPrompt = expandedPrompt;
        for (var attempt = 1; attempt <= MaxContractAttempts; attempt++)
        {
            attemptsUsed = attempt;
            try
            {
                result = await _backend.RunAsync(
                    attemptPrompt,
                    model,
                    provider,
                    reasoningEffort,
                    ct: ct,
                    options: executionOptions);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new CodergenResult(
                    Response: $"Error: {ex.Message}",
                    Status: OutcomeStatus.Fail,
                    StageStatus: new StageStatusContract(
                        Status: OutcomeStatus.Fail,
                        PreferredNextLabel: string.Empty,
                        SuggestedNextIds: new List<string>(),
                        ContextUpdates: new Dictionary<string, string>(),
                        Notes: "Codergen backend threw an exception.",
                        FailureReason: ex.Message),
                    Telemetry: new Dictionary<string, object?>
                    {
                        ["provider_state"] = "error",
                        ["failure_kind"] = "provider_error"
                    });
            }

            await PersistAttemptArtifactsAsync(stageDir, attempt, result, ct);

            stageStatus = result.StageStatus;
            if (stageStatus is null)
            {
                stageStatus = await TryReadStageStatusArtifactAsync(statusPath, ct);
            }

            if (stageStatus is null)
            {
                var raw = result.RawAssistantResponse ?? result.Response;
                if (!StageStatusContract.TryParseAssistantResponse(raw, out stageStatus, out contractError))
                    stageStatus = null;
            }

            if (stageStatus is not null &&
                !TryValidatePreferredLabel(stageStatus.PreferredNextLabel, outgoingLabels, out contractError))
            {
                stageStatus = null;
            }

            if (stageStatus is not null)
                break;

            // Preserve legacy retry/fail semantics for backends that haven't
            // adopted the structured contract yet.
            if (result.Status != OutcomeStatus.Success)
                break;

            if (attempt < MaxContractAttempts)
            {
                var reminder = BuildReminder(contractError, outgoingLabels);
                await File.WriteAllTextAsync(Path.Combine(stageDir, $"reminder-{attempt}.md"), reminder, ct);
                attemptPrompt = expandedPrompt + "\n\n[STAGE CONTRACT REMINDER]\n" + reminder;
            }
        }

        var usedFallback = false;
        if (stageStatus is null)
        {
            usedFallback = true;
            var fallbackReason = ResolveFallbackReason(result, contractError);

            stageStatus = executionOptions.AllowContractFallback
                ? StageStatusContract.FromLegacy(result, fallbackReason: fallbackReason)
                : new StageStatusContract(
                    Status: OutcomeStatus.Fail,
                    PreferredNextLabel: string.Empty,
                    SuggestedNextIds: new List<string>(),
                    ContextUpdates: result.ContextUpdates ?? new Dictionary<string, string>(),
                    Notes: "Structured stage status is required and fallback is disabled.",
                    FailureReason: fallbackReason);
        }

        if (string.IsNullOrWhiteSpace(stageStatus.Notes))
        {
            stageStatus = stageStatus with
            {
                Notes = $"Codergen node '{node.Id}' completed with status {stageStatus.Status}."
            };
        }

        var telemetry = BuildStageTelemetry(result, attemptsUsed, usedFallback);
        var editState = ResolveEditState(telemetry);
        if (executionOptions.RequireEdits &&
            editState == "none" &&
            (stageStatus.Status == OutcomeStatus.Success || stageStatus.Status == OutcomeStatus.PartialSuccess))
        {
            stageStatus = stageStatus with
            {
                Status = OutcomeStatus.Fail,
                Notes = AppendNotes(stageStatus.Notes, "Required edit evidence was missing."),
                FailureReason = "required_edit_evidence_missing"
            };
        }

        var workSegmentStatus = StageStatusContract.ToStatusString(stageStatus.Status);
        var validationManifest = BuildValidationManifest(node.Id, executionOptions, telemetry);
        await WriteJsonArtifactAsync(artifactPaths.ValidationManifestPath, validationManifest, ct);

        var validationResults = await ExecuteValidationSegmentAsync(stageStatus, validationManifest, telemetry, stageDir, ct);
        await WriteJsonArtifactAsync(artifactPaths.ValidationResultsPath, validationResults, ct);
        await WriteJsonArtifactAsync(artifactPaths.ValidationSummaryPath, validationResults.ToSummary(), ct);

        stageStatus = ApplyValidationGate(stageStatus, executionOptions, validationResults);
        if (!telemetry.ContainsKey("failure_kind") &&
            !string.IsNullOrWhiteSpace(validationResults.FailureKind) &&
            stageStatus.Status is OutcomeStatus.Fail or OutcomeStatus.Retry)
        {
            telemetry["failure_kind"] = validationResults.FailureKind;
        }

        var providerState = ResolveProviderState(telemetry);
        var validationError = ResolveValidationError(contractError, providerState);
        var contractReason = string.IsNullOrWhiteSpace(validationError) ? stageStatus.FailureReason : validationError;
        var contractState = StageStatusContract.ResolveContractState(usedFallback, contractReason, providerState);
        var observedVerificationState = ResolveObservedVerificationState(executionOptions, telemetry);
        var authoritativeValidationState = validationResults.OverallState;
        var advanceAllowed = stageStatus.Status == OutcomeStatus.Success || stageStatus.Status == OutcomeStatus.PartialSuccess;
        telemetry["observed_verification_state"] = observedVerificationState;
        telemetry["authoritative_validation_state"] = authoritativeValidationState;
        telemetry["work_segment_status"] = workSegmentStatus;

        var outcome = stageStatus.ToOutcome();
        if (telemetry.Count > 0)
            outcome = outcome with { Telemetry = telemetry };

        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), result.Response ?? string.Empty, ct);

        var rawAssistant = result.RawAssistantResponse;
        if (!string.IsNullOrWhiteSpace(rawAssistant) && !string.Equals(rawAssistant, result.Response, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(stageDir, "raw_assistant_response.md"), rawAssistant, ct);
        }

        var resolvedModel = ResolveModel(model, telemetry);
        var resolvedProvider = ResolveProvider(provider, telemetry);

        await WriteRuntimeArtifactsAsync(
            artifactPaths,
            node,
            stageStatus,
            executionOptions,
            telemetry,
            validationManifest,
            validationResults,
            resolvedModel,
            resolvedProvider,
            providerState,
            contractState,
            editState,
            workSegmentStatus,
            observedVerificationState,
            authoritativeValidationState,
            advanceAllowed,
            ct);

        var statusData = stageStatus.ToStatusJson(
            nodeId: node.Id,
            model: resolvedModel,
            provider: resolvedProvider,
            usedFallback: usedFallback,
            validationError: validationError);
        statusData["provider_state"] = providerState;
        statusData["contract_state"] = contractState;
        statusData["edit_state"] = editState;
        statusData["work_segment_status"] = workSegmentStatus;
        statusData["verification_state"] = observedVerificationState;
        statusData["observed_verification_state"] = observedVerificationState;
        statusData["verification_passed"] = observedVerificationState == "passed"
            ? true
            : observedVerificationState == "failed" ? false : null;
        statusData["authoritative_validation_state"] = authoritativeValidationState;
        statusData["authoritative_validation_passed"] = authoritativeValidationState == RuntimeValidationStates.Passed
            ? true
            : authoritativeValidationState is RuntimeValidationStates.Failed or RuntimeValidationStates.Timeout or RuntimeValidationStates.Missing or RuntimeValidationStates.Misconfigured
                ? false
                : null;
        statusData["advance_allowed"] = advanceAllowed;
        statusData["effective_policy"] = BuildEffectivePolicy(executionOptions);
        statusData["effective_policy_path"] = artifactPaths.EffectivePolicyPath;
        statusData["runtime_status_path"] = artifactPaths.RuntimeStatusPath;
        statusData["provider_events_path"] = artifactPaths.ProviderEventsPath;
        statusData["diff_summary_path"] = artifactPaths.DiffSummaryPath;
        statusData["verification_path"] = artifactPaths.VerificationPath;
        statusData["validation_manifest_path"] = artifactPaths.ValidationManifestPath;
        statusData["validation_results_path"] = artifactPaths.ValidationResultsPath;
        statusData["validation_summary_path"] = artifactPaths.ValidationSummaryPath;
        if (TryGetString(telemetry, "failure_kind", out var failureKind))
            statusData["failure_kind"] = failureKind;
        if (TryGetInt(telemetry, "provider_status_code", out var providerStatusCode))
            statusData["provider_status_code"] = providerStatusCode;
        if (TryGetBoolean(telemetry, "provider_retryable", out var providerRetryable))
            statusData["provider_retryable"] = providerRetryable;
        if (TryGetString(telemetry, "provider_error_message", out var providerErrorMessage))
            statusData["provider_error_message"] = providerErrorMessage;
        if (telemetry.Count > 0)
            statusData["telemetry"] = telemetry;

        await File.WriteAllTextAsync(
            statusPath,
            JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return outcome;
    }

    private static Dictionary<string, object?> BuildStageTelemetry(CodergenResult result, int attemptsUsed, bool usedFallback)
    {
        var telemetry = result.Telemetry is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(result.Telemetry, StringComparer.Ordinal);

        telemetry["contract_attempts"] = attemptsUsed;
        telemetry["contract_used_fallback"] = usedFallback;
        return telemetry;
    }

    private static CodergenExecutionOptions BuildExecutionOptions(GraphNode node, Graph graph)
    {
        var stageClass = ResolveStageClass(node);
        var requireVerification = ReadBooleanAttribute(node.RawAttributes, "require_verification") ||
                                  ReadGraphBooleanAttribute(graph.Attributes, "require_verification");
        var codergenVersion = ReadStringAttribute(node.RawAttributes, "codergen_version") ??
                              ReadGraphAttribute(graph.Attributes, "codergen_version");

        return new CodergenExecutionOptions(
            StageClass: stageClass,
            MaxProviderResponseMs: RuntimeDurationParser.TryParseMilliseconds(node.Timeout, out var timeoutMs) ? timeoutMs : null,
            RequireEdits: ResolveRequireEdits(node, stageClass),
            RequireVerification: requireVerification,
            AllowContractFallback: !node.RawAttributes.TryGetValue("allow_contract_fallback", out var allowFallbackRaw) ||
                                   ParseBoolean(allowFallbackRaw, defaultValue: true),
            CodergenVersion: codergenVersion,
            Validation: ResolveValidationPolicy(node, graph, stageClass, requireVerification, codergenVersion));
    }

    private static string? ResolveStageClass(GraphNode node)
    {
        var nodeKind = ReadStringAttribute(node.RawAttributes, "node_kind");
        if (!string.IsNullOrWhiteSpace(nodeKind))
            return nodeKind;

        return string.IsNullOrWhiteSpace(node.Class) ? null : node.Class;
    }

    private static bool ResolveRequireEdits(GraphNode node, string? stageClass)
    {
        if (ReadBooleanAttribute(node.RawAttributes, "require_edits"))
            return true;

        if (string.Equals(stageClass, "implementation", StringComparison.OrdinalIgnoreCase))
            return true;

        return LooksLikeImplementationNode(node);
    }

    private static bool LooksLikeImplementationNode(GraphNode node)
    {
        return ContainsImplementationCue(node.Id) ||
               ContainsImplementationCue(node.Label) ||
               ContainsImplementationCue(node.Class) ||
               ContainsImplementationCue(node.Prompt);
    }

    private static bool ContainsImplementationCue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("implement", StringComparison.Ordinal) ||
               normalized.Contains("write the code", StringComparison.Ordinal) ||
               normalized.Contains("modify the code", StringComparison.Ordinal) ||
               normalized.Contains("edit the code", StringComparison.Ordinal) ||
               normalized.Contains("patch", StringComparison.Ordinal) ||
               normalized.Contains("fix the code", StringComparison.Ordinal) ||
               normalized.Contains("refactor", StringComparison.Ordinal);
    }

    private static bool ReadBooleanAttribute(IReadOnlyDictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var raw) && ParseBoolean(raw, defaultValue: false);
    }

    private static bool ReadGraphBooleanAttribute(IReadOnlyDictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var raw) && ParseBoolean(raw, defaultValue: false) ||
               attributes.TryGetValue($"default_{key}", out var defaultRaw) && ParseBoolean(defaultRaw, defaultValue: false);
    }

    private static bool ParseBoolean(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadStringAttribute(IReadOnlyDictionary<string, string> attributes, string key)
    {
        return attributes.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;
    }

    private static string? ReadGraphAttribute(IReadOnlyDictionary<string, string> attributes, string key)
    {
        return ReadStringAttribute(attributes, key) ?? ReadStringAttribute(attributes, $"default_{key}");
    }

    private static RuntimeValidationPolicy ResolveValidationPolicy(
        GraphNode node,
        Graph graph,
        string? stageClass,
        bool requireVerification,
        string? codergenVersion)
    {
        var explicitMode = ReadStringAttribute(node.RawAttributes, "validation_mode");
        var graphMode = ReadGraphAttribute(graph.Attributes, "validation_mode");
        var resolvedMode = RuntimeValidationModes.Normalize(
            explicitMode ?? graphMode,
            ResolveStageDefaultValidationMode(stageClass, codergenVersion, requireVerification));

        var timeoutMs = ReadValidationTimeout(node.RawAttributes) ?? ReadValidationTimeout(graph.Attributes);
        var workdir = ReadStringAttribute(node.RawAttributes, "validation_workdir") ??
                      ReadGraphAttribute(graph.Attributes, "validation_workdir");
        var profile = ReadStringAttribute(node.RawAttributes, "validation_profile") ??
                      ReadGraphAttribute(graph.Attributes, "validation_profile");
        var failAction = RuntimeValidationFailActions.Normalize(
            ReadStringAttribute(node.RawAttributes, "validation_fail_action") ??
            ReadGraphAttribute(graph.Attributes, "validation_fail_action"));

        var declaredChecks = ResolveDeclaredValidationChecks(node, graph, timeoutMs, workdir, resolvedMode);

        return new RuntimeValidationPolicy(
            Mode: resolvedMode,
            Profile: profile,
            Checks: declaredChecks,
            TimeoutMs: timeoutMs,
            FailAction: failAction);
    }

    private static string ResolveStageDefaultValidationMode(string? stageClass, string? codergenVersion, bool requireVerification)
    {
        if (string.Equals(stageClass, "validation", StringComparison.OrdinalIgnoreCase))
            return RuntimeValidationModes.Required;

        if (string.Equals(stageClass, "implementation", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(codergenVersion, "v2", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeValidationModes.Required;
        }

        if (requireVerification)
            return RuntimeValidationModes.Advisory;

        return RuntimeValidationModes.None;
    }

    private static IReadOnlyList<RuntimeValidationCheckRegistration> ResolveDeclaredValidationChecks(
        GraphNode node,
        Graph graph,
        int? timeoutMs,
        string? workdir,
        string validationMode)
    {
        var defaultRequired = string.Equals(validationMode, RuntimeValidationModes.Required, StringComparison.Ordinal);

        if (TryReadStructuredValidationChecks(
                node.RawAttributes,
                includeGraphDefaults: false,
                timeoutMs,
                workdir,
                defaultRequired,
                RuntimeValidationCheckSources.NodePolicy,
                out var nodeStructuredChecks))
        {
            return nodeStructuredChecks;
        }

        if (TryReadValidationCommands(node.RawAttributes, includeGraphDefaults: false, out var nodeCommands))
        {
            return nodeCommands
                .Select((command, index) => BuildCommandRegistration(
                    command,
                    index,
                    timeoutMs,
                    workdir,
                    defaultRequired,
                    RuntimeValidationCheckSources.NodePolicy,
                    $"validation_commands[{index}]"))
                .ToList();
        }

        if (TryReadStructuredValidationChecks(
                graph.Attributes,
                includeGraphDefaults: true,
                timeoutMs,
                workdir,
                defaultRequired,
                RuntimeValidationCheckSources.GraphDefault,
                out var graphStructuredChecks))
        {
            return graphStructuredChecks;
        }

        if (TryReadValidationCommands(graph.Attributes, includeGraphDefaults: true, out var graphCommands))
        {
            return graphCommands
                .Select((command, index) => BuildCommandRegistration(
                    command,
                    index,
                    timeoutMs,
                    workdir,
                    defaultRequired,
                    RuntimeValidationCheckSources.GraphDefault,
                    $"default_validation_commands[{index}]"))
                .ToList();
        }

        return Array.Empty<RuntimeValidationCheckRegistration>();
    }

    private static RuntimeValidationCheckRegistration BuildCommandRegistration(
        string command,
        int index,
        int? timeoutMs,
        string? workdir,
        bool required,
        string source,
        string? sourceReference = null)
    {
        var trimmedCommand = command.Trim();
        return new RuntimeValidationCheckRegistration(
            Kind: RuntimeValidationCheckKinds.Command,
            Name: $"command-{index + 1}",
            Command: trimmedCommand,
            Workdir: workdir,
            TimeoutMs: timeoutMs,
            Required: required,
            Source: source,
            SourceReference: sourceReference);
    }

    private static int? ReadValidationTimeout(IReadOnlyDictionary<string, string> attributes)
    {
        var rawTimeout = ReadStringAttribute(attributes, "validation_timeout") ??
                         ReadStringAttribute(attributes, "default_validation_timeout");
        return RuntimeDurationParser.TryParseMilliseconds(rawTimeout, out var timeoutMs) ? timeoutMs : null;
    }

    private static bool TryReadValidationCommands(
        IReadOnlyDictionary<string, string> attributes,
        bool includeGraphDefaults,
        out IReadOnlyList<string> commands)
    {
        commands = Array.Empty<string>();

        if (!TryReadValidationAttribute(
                attributes,
                includeGraphDefaults,
                primaryPluralKey: "validation_commands",
                primarySingularKey: "validation_command",
                defaultPluralKey: "default_validation_commands",
                defaultSingularKey: "default_validation_command",
                out var raw))
        {
            return false;
        }

        commands = ParseValidationCommands(raw);
        return true;
    }

    private static IReadOnlyList<string> ParseValidationCommands(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Cast<string>()
                        .ToList();
                }
            }
            catch
            {
                // Fall back to line-based parsing below.
            }
        }

        return trimmed
            .Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .ToList();
    }

    private static bool TryReadStructuredValidationChecks(
        IReadOnlyDictionary<string, string> attributes,
        bool includeGraphDefaults,
        int? timeoutMs,
        string? workdir,
        bool defaultRequired,
        string source,
        out IReadOnlyList<RuntimeValidationCheckRegistration> checks)
    {
        checks = Array.Empty<RuntimeValidationCheckRegistration>();
        if (!TryReadValidationAttribute(
                attributes,
                includeGraphDefaults,
                primaryPluralKey: "validation_checks",
                primarySingularKey: "validation_check",
                defaultPluralKey: "default_validation_checks",
                defaultSingularKey: "default_validation_check",
                out var raw))
        {
            return false;
        }

        checks = ParseValidationChecks(raw, timeoutMs, workdir, defaultRequired, source);
        return true;
    }

    private static bool TryReadValidationAttribute(
        IReadOnlyDictionary<string, string> attributes,
        bool includeGraphDefaults,
        string primaryPluralKey,
        string primarySingularKey,
        string defaultPluralKey,
        string defaultSingularKey,
        out string? raw)
    {
        raw = null;

        if (attributes.TryGetValue(primaryPluralKey, out raw))
            return true;

        if (attributes.TryGetValue(primarySingularKey, out raw))
            return true;

        if (!includeGraphDefaults)
            return false;

        if (attributes.TryGetValue(defaultPluralKey, out raw))
            return true;

        return attributes.TryGetValue(defaultSingularKey, out raw);
    }

    private static IReadOnlyList<RuntimeValidationCheckRegistration> ParseValidationChecks(
        string? raw,
        int? timeoutMs,
        string? workdir,
        bool defaultRequired,
        string source)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<RuntimeValidationCheckRegistration>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var checks = new List<RuntimeValidationCheckRegistration>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in root.EnumerateArray())
                {
                    index++;
                    if (TryParseValidationCheck(item, index, timeoutMs, workdir, defaultRequired, source, out var check))
                        checks.Add(check!);
                }

                return checks;
            }

            if (TryParseValidationCheck(root, 1, timeoutMs, workdir, defaultRequired, source, out var singleCheck))
                return new[] { singleCheck! };
        }
        catch
        {
            // Invalid policy JSON is treated as an empty explicit override for now.
        }

        return Array.Empty<RuntimeValidationCheckRegistration>();
    }

    private static bool TryParseValidationCheck(
        JsonElement element,
        int ordinal,
        int? defaultTimeoutMs,
        string? defaultWorkdir,
        bool defaultRequired,
        string source,
        out RuntimeValidationCheckRegistration? check)
    {
        check = null;

        if (element.ValueKind == JsonValueKind.String)
        {
            var command = element.GetString();
            if (string.IsNullOrWhiteSpace(command))
                return false;

            check = BuildCommandRegistration(
                command,
                ordinal - 1,
                defaultTimeoutMs,
                defaultWorkdir,
                defaultRequired,
                source,
                $"{source}[{ordinal - 1}]");
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var kind = RuntimeValidationCheckKinds.Normalize(
            TryGetJsonString(element, "kind", "type"),
            RuntimeValidationCheckKinds.Command);
        var name = TryGetJsonString(element, "name")?.Trim();
        var commandValue = TryGetJsonString(element, "command");
        var path = TryGetJsonString(element, "path");
        var paths = ReadJsonStringList(element, "paths");
        var workdir = TryGetJsonString(element, "workdir", "working_directory") ?? defaultWorkdir;
        var containsText = TryGetJsonString(element, "contains_text", "containsText");
        var matchesRegex = TryGetJsonString(element, "matches_regex", "matchesRegex");
        var jsonPath = TryGetJsonString(element, "json_path", "jsonPath");
        var expectedValueJson = TryGetJsonString(element, "expected_value_json", "expectedValueJson");
        var expectedSchemaJson = TryGetJsonString(element, "expected_schema_json", "expectedSchemaJson");
        var requireAnyChange = TryGetJsonBoolean(element, "require_any_change", "requireAnyChange") ?? false;
        var timeoutMs = TryGetJsonInt(element, "timeout_ms", "timeoutMs") ?? defaultTimeoutMs;
        var required = TryGetJsonBoolean(element, "required") ?? defaultRequired;

        check = new RuntimeValidationCheckRegistration(
            Kind: kind,
            Name: string.IsNullOrWhiteSpace(name) ? $"{kind}-{ordinal}" : name,
            Command: commandValue,
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
            Source: source,
            SourceReference: $"{source}[{ordinal - 1}]");
        return true;
    }

    private static string? TryGetJsonString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadJsonStringList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var single = value.GetString();
                return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single.Trim() };
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .Select(item => item.Trim())
                    .ToList();
            }
        }

        return null;
    }

    private static bool? TryGetJsonBoolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();

            if (value.ValueKind == JsonValueKind.String &&
                bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static int? TryGetJsonInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string AppendNotes(string currentNotes, string note)
    {
        if (string.IsNullOrWhiteSpace(currentNotes))
            return note;

        if (currentNotes.Contains(note, StringComparison.Ordinal))
            return currentNotes;

        return currentNotes.TrimEnd() + " " + note;
    }

    private static string ResolveProviderState(Dictionary<string, object?> telemetry)
    {
        return TryGetString(telemetry, "provider_state", out var providerState)
            ? providerState
            : "completed";
    }

    private static string ResolveFallbackReason(CodergenResult result, string? contractError)
    {
        if (result.Telemetry is not null)
        {
            var providerState = ResolveProviderState(result.Telemetry);
            if (!string.Equals(providerState, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetString(result.Telemetry, "provider_error_message", out var providerErrorMessage) &&
                    !string.IsNullOrWhiteSpace(providerErrorMessage))
                {
                    return providerErrorMessage;
                }

                if (TryGetString(result.Telemetry, "failure_kind", out var failureKind) &&
                    !string.IsNullOrWhiteSpace(failureKind))
                {
                    return failureKind;
                }
            }
        }

        return string.IsNullOrWhiteSpace(contractError)
            ? "Missing structured stage status output."
            : contractError;
    }

    private static string? ResolveValidationError(string? contractError, string providerState)
    {
        if (string.IsNullOrWhiteSpace(contractError))
            return null;

        return string.Equals(providerState, "completed", StringComparison.OrdinalIgnoreCase)
            ? contractError
            : null;
    }

    private static string ResolveEditState(Dictionary<string, object?> telemetry)
    {
        return TryGetInt(telemetry, "touched_files_count", out var touchedFiles) && touchedFiles > 0
            ? "modified"
            : "none";
    }

    private static RuntimeValidationManifest BuildValidationManifest(
        string nodeId,
        CodergenExecutionOptions executionOptions,
        Dictionary<string, object?> telemetry)
    {
        var combinedChecks = new List<RuntimeValidationCheckRegistration>();
        combinedChecks.AddRange(executionOptions.EffectiveValidation.EffectiveChecks);
        combinedChecks.AddRange(ReadQueuedValidationChecks(telemetry));

        var manifestChecks = new List<RuntimeValidationManifestCheck>();
        var seenChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinal = 0;
        foreach (var check in combinedChecks)
        {
            var normalizedKind = RuntimeValidationCheckKinds.Normalize(check.Kind);
            var normalizedPaths = check.Paths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .ToList();
            var dedupeKey = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["kind"] = normalizedKind,
                ["command"] = check.Command?.Trim(),
                ["path"] = check.Path?.Trim(),
                ["paths"] = normalizedPaths,
                ["workdir"] = check.Workdir?.Trim(),
                ["contains_text"] = check.ContainsText,
                ["matches_regex"] = check.MatchesRegex,
                ["json_path"] = check.JsonPath,
                ["expected_value_json"] = check.ExpectedValueJson,
                ["expected_schema_json"] = check.ExpectedSchemaJson,
                ["require_any_change"] = check.RequireAnyChange,
                ["timeout_ms"] = check.TimeoutMs ?? executionOptions.EffectiveValidation.TimeoutMs,
                ["required"] = check.Required
            });
            if (!seenChecks.Add(dedupeKey))
                continue;

            ordinal++;
            var name = string.IsNullOrWhiteSpace(check.Name) ? $"{normalizedKind}-{ordinal}" : check.Name.Trim();
            manifestChecks.Add(new RuntimeValidationManifestCheck(
                Id: $"{normalizedKind}-{ordinal}",
                Kind: normalizedKind,
                Name: name,
                Command: string.IsNullOrWhiteSpace(check.Command) ? null : check.Command.Trim(),
                Path: string.IsNullOrWhiteSpace(check.Path) ? null : check.Path.Trim(),
                Paths: normalizedPaths is { Count: > 0 } ? normalizedPaths : null,
                Workdir: string.IsNullOrWhiteSpace(check.Workdir) ? null : check.Workdir.Trim(),
                ContainsText: string.IsNullOrWhiteSpace(check.ContainsText) ? null : check.ContainsText,
                MatchesRegex: string.IsNullOrWhiteSpace(check.MatchesRegex) ? null : check.MatchesRegex,
                JsonPath: string.IsNullOrWhiteSpace(check.JsonPath) ? null : check.JsonPath,
                ExpectedValueJson: string.IsNullOrWhiteSpace(check.ExpectedValueJson) ? null : check.ExpectedValueJson,
                ExpectedSchemaJson: string.IsNullOrWhiteSpace(check.ExpectedSchemaJson) ? null : check.ExpectedSchemaJson,
                RequireAnyChange: check.RequireAnyChange,
                TimeoutMs: check.TimeoutMs ?? executionOptions.EffectiveValidation.TimeoutMs,
                Required: check.Required,
                Source: check.Source,
                SourceReference: check.SourceReference));
        }

        return new RuntimeValidationManifest(
            NodeId: nodeId,
            Mode: executionOptions.EffectiveValidation.Mode,
            Profile: executionOptions.EffectiveValidation.Profile,
            Checks: manifestChecks,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<RuntimeValidationCheckRegistration> ReadQueuedValidationChecks(
        Dictionary<string, object?> telemetry)
    {
        if (!telemetry.TryGetValue("queued_validation_checks", out var raw) || raw is null)
            return Array.Empty<RuntimeValidationCheckRegistration>();

        if (raw is IEnumerable<RuntimeValidationCheckRegistration> typedChecks)
            return typedChecks.ToList();

        if (raw is IEnumerable<object?> objects)
        {
            return objects
                .Select(item => item as RuntimeValidationCheckRegistration)
                .Where(item => item is not null)
                .Cast<RuntimeValidationCheckRegistration>()
                .ToList();
        }

        return Array.Empty<RuntimeValidationCheckRegistration>();
    }

    private static async Task<RuntimeValidationResults> ExecuteValidationSegmentAsync(
        StageStatusContract stageStatus,
        RuntimeValidationManifest manifest,
        Dictionary<string, object?> telemetry,
        string stageDir,
        CancellationToken ct)
    {
        if (manifest.Checks.Count == 0)
            return RuntimeValidationResults.Empty(manifest.NodeId, manifest.Mode, manifest.Profile);

        if (stageStatus.Status is not OutcomeStatus.Success and not OutcomeStatus.PartialSuccess)
            return RuntimeValidationResults.Skipped(manifest);

        var executor = new RuntimeValidationExecutor();
        var evidence = new RuntimeValidationEvidence(ReadStringList(telemetry, "touched_files"));
        return await executor.ExecuteAsync(manifest, stageDir, evidence, ct);
    }

    private static StageStatusContract ApplyValidationGate(
        StageStatusContract stageStatus,
        CodergenExecutionOptions executionOptions,
        RuntimeValidationResults validationResults)
    {
        if (!executionOptions.EffectiveValidation.IsRequired ||
            stageStatus.Status is not OutcomeStatus.Success and not OutcomeStatus.PartialSuccess)
        {
            return stageStatus;
        }

        if (validationResults.OverallState == RuntimeValidationStates.Passed)
            return stageStatus;

        var nextStatus = string.Equals(executionOptions.EffectiveValidation.FailAction, RuntimeValidationFailActions.Retry, StringComparison.Ordinal)
            ? OutcomeStatus.Retry
            : OutcomeStatus.Fail;
        var note = validationResults.OverallState == RuntimeValidationStates.Missing
            ? "Required authoritative validation was missing."
            : $"Required validation ended in state '{validationResults.OverallState}'.";

        return stageStatus with
        {
            Status = nextStatus,
            Notes = AppendNotes(stageStatus.Notes, note),
            FailureReason = validationResults.FailureKind ?? stageStatus.FailureReason
        };
    }

    private static string ResolveObservedVerificationState(CodergenExecutionOptions options, Dictionary<string, object?> telemetry)
    {
        if (!options.RequireVerification)
            return RuntimeValidationStates.NotRequired;

        return TryGetString(telemetry, "verification_state", out var verificationState)
            ? verificationState
            : RuntimeValidationStates.NotRun;
    }

    private static Dictionary<string, object?> BuildEffectivePolicy(CodergenExecutionOptions options)
    {
        var validationPolicy = options.EffectiveValidation;
        return new Dictionary<string, object?>
        {
            ["stage_class"] = options.StageClass,
            ["max_provider_response_ms"] = options.MaxProviderResponseMs,
            ["require_edits"] = options.RequireEdits,
            ["require_verification"] = options.RequireVerification,
            ["allow_contract_fallback"] = options.AllowContractFallback,
            ["codergen_version"] = options.CodergenVersion,
            ["validation_mode"] = validationPolicy.Mode,
            ["validation_profile"] = validationPolicy.Profile,
            ["validation_timeout_ms"] = validationPolicy.TimeoutMs,
            ["validation_fail_action"] = validationPolicy.FailAction,
            ["validation_check_count"] = validationPolicy.EffectiveChecks.Count,
            ["validation_checks"] = validationPolicy.EffectiveChecks
                .Select(check => new Dictionary<string, object?>
                {
                    ["kind"] = check.Kind,
                    ["name"] = check.Name,
                    ["command"] = check.Command,
                    ["path"] = check.Path,
                    ["paths"] = check.Paths,
                    ["workdir"] = check.Workdir,
                    ["contains_text"] = check.ContainsText,
                    ["matches_regex"] = check.MatchesRegex,
                    ["json_path"] = check.JsonPath,
                    ["expected_value_json"] = check.ExpectedValueJson,
                    ["expected_schema_json"] = check.ExpectedSchemaJson,
                    ["require_any_change"] = check.RequireAnyChange,
                    ["timeout_ms"] = check.TimeoutMs,
                    ["required"] = check.Required,
                    ["source"] = check.Source,
                    ["source_reference"] = check.SourceReference
                })
                .ToList()
        };
    }

    private static async Task WriteRuntimeArtifactsAsync(
        StageArtifactPaths artifactPaths,
        GraphNode node,
        StageStatusContract stageStatus,
        CodergenExecutionOptions executionOptions,
        Dictionary<string, object?> telemetry,
        RuntimeValidationManifest validationManifest,
        RuntimeValidationResults validationResults,
        string? model,
        string? provider,
        string providerState,
        string contractState,
        string editState,
        string workSegmentStatus,
        string observedVerificationState,
        string authoritativeValidationState,
        bool advanceAllowed,
        CancellationToken ct)
    {
        var effectivePolicy = BuildEffectivePolicy(executionOptions);
        var touchedFiles = ReadStringList(telemetry, "touched_files");
        var verificationCommands = ReadStringList(telemetry, "verification_commands");

        var runtimeStatus = new Dictionary<string, object?>
        {
            ["node_id"] = node.Id,
            ["provider"] = provider,
            ["model"] = model,
            ["stage_class"] = executionOptions.StageClass,
            ["codergen_version"] = executionOptions.CodergenVersion ?? "v1",
            ["execution_status"] = StageStatusContract.ToStatusString(stageStatus.Status),
            ["failure_kind"] = TryGetString(telemetry, "failure_kind", out var failureKind) ? failureKind : stageStatus.FailureReason,
            ["provider_state"] = providerState,
            ["contract_state"] = contractState,
            ["edit_state"] = editState,
            ["work_segment_status"] = workSegmentStatus,
            ["verification_state"] = observedVerificationState,
            ["observed_verification_state"] = observedVerificationState,
            ["authoritative_validation_state"] = authoritativeValidationState,
            ["advance_allowed"] = advanceAllowed,
            ["touched_paths"] = touchedFiles,
            ["validation_check_count"] = validationManifest.Checks.Count,
            ["required_validation_check_count"] = validationManifest.Checks.Count(check => check.Required),
            ["effective_policy"] = effectivePolicy
        };

        var providerEvents = new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["model"] = model,
            ["provider_state"] = providerState,
            ["failure_kind"] = TryGetString(telemetry, "failure_kind", out var providerFailureKind) ? providerFailureKind : null,
            ["provider_timeout_ms"] = TryGetInt(telemetry, "provider_timeout_ms", out var providerTimeoutMs) ? providerTimeoutMs : null,
            ["provider_status_code"] = TryGetInt(telemetry, "provider_status_code", out var providerStatusCode) ? providerStatusCode : null,
            ["provider_retryable"] = TryGetBoolean(telemetry, "provider_retryable", out var providerRetryable) ? providerRetryable : null,
            ["provider_error_message"] = TryGetString(telemetry, "provider_error_message", out var providerErrorMessage) ? providerErrorMessage : null,
            ["helper_session_count"] = TryGetInt(telemetry, "helper_session_count", out var helperSessionCount) ? helperSessionCount : 0
        };

        var diffSummary = new Dictionary<string, object?>
        {
            ["edit_state"] = editState,
            ["touched_files_count"] = TryGetInt(telemetry, "touched_files_count", out var touchedFilesCount) ? touchedFilesCount : 0,
            ["touched_files"] = touchedFiles,
            ["touched_paths"] = touchedFiles
        };

        var verificationSummary = new Dictionary<string, object?>
        {
            ["required"] = executionOptions.RequireVerification,
            ["verification_state"] = observedVerificationState,
            ["observed_verification_state"] = observedVerificationState,
            ["passed"] = observedVerificationState == "passed"
                ? true
                : observedVerificationState == "failed" ? false : null,
            ["commands"] = verificationCommands,
            ["command_count"] = verificationCommands.Count,
            ["errors"] = TryGetInt(telemetry, "verification_errors", out var verificationErrors) ? verificationErrors : 0,
            ["verification_commands"] = verificationCommands,
            ["verification_errors"] = TryGetInt(telemetry, "verification_errors", out var verificationErrorCount) ? verificationErrorCount : 0,
            ["authoritative_validation_state"] = authoritativeValidationState,
            ["authoritative_validation_failure_kind"] = validationResults.FailureKind,
            ["validation_manifest_path"] = artifactPaths.ValidationManifestPath,
            ["validation_results_path"] = artifactPaths.ValidationResultsPath
        };

        await WriteJsonArtifactAsync(artifactPaths.RuntimeStatusPath, runtimeStatus, ct);
        await WriteJsonArtifactAsync(artifactPaths.ProviderEventsPath, providerEvents, ct);
        await WriteJsonArtifactAsync(artifactPaths.DiffSummaryPath, diffSummary, ct);
        await WriteJsonArtifactAsync(artifactPaths.VerificationPath, verificationSummary, ct);
    }

    private static StageArtifactPaths BuildArtifactPaths(string stageDir) =>
        new(
            EffectivePolicyPath: Path.Combine(stageDir, "effective-policy.json"),
            RuntimeStatusPath: Path.Combine(stageDir, "runtime-status.json"),
            ProviderEventsPath: Path.Combine(stageDir, "provider-events.json"),
            DiffSummaryPath: Path.Combine(stageDir, "diff-summary.json"),
            VerificationPath: Path.Combine(stageDir, "verification.json"),
            ValidationManifestPath: Path.Combine(stageDir, "validation-manifest.json"),
            ValidationResultsPath: Path.Combine(stageDir, "validation-results.json"),
            ValidationSummaryPath: Path.Combine(stageDir, "validation-summary.json"));

    private static string? ResolveModel(string? model, Dictionary<string, object?> telemetry)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return model;

        return TryGetString(telemetry, "model", out var resolvedModel) ? resolvedModel : null;
    }

    private static string? ResolveProvider(string? provider, Dictionary<string, object?> telemetry)
    {
        if (!string.IsNullOrWhiteSpace(provider))
            return provider;

        return TryGetString(telemetry, "provider", out var resolvedProvider) ? resolvedProvider : null;
    }

    private static IReadOnlyList<string> ReadStringList(Dictionary<string, object?> telemetry, string key)
    {
        if (!telemetry.TryGetValue(key, out var raw) || raw is null)
            return Array.Empty<string>();

        return raw switch
        {
            IEnumerable<string> strings => strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
            IEnumerable<object?> objects => objects
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList(),
            _ => Array.Empty<string>()
        };
    }

    private static bool TryGetString(Dictionary<string, object?> telemetry, string key, out string value)
    {
        value = string.Empty;
        if (!telemetry.TryGetValue(key, out var raw) || raw is not string text || string.IsNullOrWhiteSpace(text))
            return false;

        value = text;
        return true;
    }

    private static bool TryGetInt(Dictionary<string, object?> telemetry, string key, out long value)
    {
        value = 0;
        if (!telemetry.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case float f:
                value = (long)f;
                return true;
            case double d:
                value = (long)d;
                return true;
            case decimal m:
                value = (long)m;
                return true;
            case string s when long.TryParse(s, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetBoolean(Dictionary<string, object?> telemetry, string key, out bool value)
    {
        value = false;
        if (!telemetry.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case bool boolean:
                value = boolean;
                return true;
            case string text when bool.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static void ArchivePreviousStatus(string statusPath)
    {
        if (!File.Exists(statusPath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(statusPath)!;
            var archived = Path.Combine(dir, $"status.previous.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(statusPath, archived, overwrite: true);
        }
        catch
        {
            // Non-critical. If archive fails, status.json will be overwritten later.
        }
    }

    private static async Task PersistAttemptArtifactsAsync(string stageDir, int attempt, CodergenResult result, CancellationToken ct)
    {
        var suffix = attempt == 1 ? string.Empty : $"-attempt-{attempt}";
        await File.WriteAllTextAsync(
            Path.Combine(stageDir, $"response{suffix}.md"),
            result.Response ?? string.Empty,
            ct);

        if (!string.IsNullOrWhiteSpace(result.RawAssistantResponse) &&
            !string.Equals(result.RawAssistantResponse, result.Response, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(
                Path.Combine(stageDir, $"raw-response{suffix}.md"),
                result.RawAssistantResponse,
                ct);
        }

        await PersistSupplementalArtifactsAsync(stageDir, result.Artifacts, ct);
    }

    private sealed record StageArtifactPaths(
        string EffectivePolicyPath,
        string RuntimeStatusPath,
        string ProviderEventsPath,
        string DiffSummaryPath,
        string VerificationPath,
        string ValidationManifestPath,
        string ValidationResultsPath,
        string ValidationSummaryPath);

    private static Task WriteJsonArtifactAsync(string path, object payload, CancellationToken ct)
    {
        return File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }),
            ct);
    }

    private static async Task<StageStatusContract?> TryReadStageStatusArtifactAsync(string statusPath, CancellationToken ct)
    {
        if (!File.Exists(statusPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(statusPath, ct);
            if (StageStatusContract.TryParseJson(json, out var parsed, out _))
                return parsed;
        }
        catch
        {
            // Treat as absent; caller will retry/remind.
        }

        return null;
    }

    private static bool TryValidatePreferredLabel(
        string preferredLabel,
        IReadOnlyCollection<string> outgoingLabels,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(preferredLabel) || outgoingLabels.Count == 0)
            return true;

        var normalizedPreferred = EdgeSelector.NormalizeLabel(preferredLabel);
        var hasMatch = outgoingLabels
            .Select(EdgeSelector.NormalizeLabel)
            .Any(label => label == normalizedPreferred);

        if (hasMatch)
            return true;

        error = $"preferred_next_label '{preferredLabel}' does not match outgoing labels: {string.Join(", ", outgoingLabels)}";
        return false;
    }

    private static string RewriteArtifactPaths(string prompt, string logsRoot, string gatesRoot)
    {
        var rewritten = RewriteArtifactPrefix(prompt, "logs/", logsRoot);
        rewritten = RewriteArtifactPrefix(rewritten, "gates/", gatesRoot);
        return rewritten;
    }

    private static string RewriteArtifactPrefix(string prompt, string prefix, string absoluteRoot)
    {
        var normalizedRoot = absoluteRoot.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/') + "/";
        var pattern = $@"(?<![A-Za-z0-9_/\.-]){Regex.Escape(prefix)}(?<rest>[\w./-]+)";
        return Regex.Replace(
            prompt,
            pattern,
            match => normalizedRoot + match.Groups["rest"].Value,
            RegexOptions.CultureInvariant);
    }

    private static string BuildStageContractInstructions(IReadOnlyList<string> outgoingLabels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[STAGE STATUS CONTRACT]");
        sb.AppendLine("Before finishing this stage, output a valid JSON object (optionally in ```json fences) with these fields:");
        sb.AppendLine("  status: success | retry | fail | partial_success");
        sb.AppendLine("  preferred_next_label: string (optional)");
        sb.AppendLine("  suggested_next_ids: string[] (optional)");
        sb.AppendLine("  context_updates: object<string,string> (optional)");
        sb.AppendLine("  notes: string (optional)");
        sb.AppendLine("  failure_reason: string (optional)");
        sb.AppendLine("  blocking_question: string | object (optional when you need Soulcaster to auto-answer a blocking question)");
        if (outgoingLabels.Count > 0)
            sb.AppendLine($"If preferred_next_label is set, it MUST be one of: {string.Join(", ", outgoingLabels)}");
        sb.AppendLine("[/STAGE STATUS CONTRACT]");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildReminder(string reason, IReadOnlyList<string> outgoingLabels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Your previous output did not satisfy the stage status contract.");
        if (!string.IsNullOrWhiteSpace(reason))
            sb.AppendLine($"Reason: {reason}");
        sb.AppendLine("Return ONLY a valid JSON object with keys: status, preferred_next_label, suggested_next_ids, context_updates, notes, failure_reason.");
        if (outgoingLabels.Count > 0)
            sb.AppendLine($"Allowed preferred_next_label values: {string.Join(", ", outgoingLabels)}");
        return sb.ToString();
    }

    private static string BuildPreamble(
        GraphNode node,
        Graph graph,
        string logsRoot,
        PipelineContext context,
        IReadOnlyList<string> outgoingLabels,
        CodergenExecutionOptions executionOptions)
    {
        // logsRoot is the absolute path to the logs/ directory.
        // outputRoot = logs parent = the run output directory (e.g. dotfiles/output/run-name).
        var outputRoot = Path.GetFullPath(Path.Combine(logsRoot, ".."));

        // Project root: dotfile lives at {project}/dotfiles/{name}.dot,
        // so output is at {project}/dotfiles/output/{run}. Going up three
        // levels from logsRoot (logs → output/run → output → dotfiles → project).
        var projectRoot = Path.GetFullPath(Path.Combine(logsRoot, "..", "..", "..", ".."));

        var fidelity = string.IsNullOrWhiteSpace(node.Fidelity) ? "full" : node.Fidelity;
        var thread = string.IsNullOrWhiteSpace(node.ThreadId) ? node.Id : node.ThreadId;
        var stageId = RuntimeStageResolver.ResolveStageId(context, node.Id);
        var resumeMode = context.Get("pipeline.resume_mode");
        var helperProvider = ResolveHelperAttribute(node, graph, "provider");
        var helperModel = ResolveHelperAttribute(node, graph, "model");
        var helperReasoning = ResolveHelperAttribute(node, graph, "reasoning_effort");
        if (string.IsNullOrWhiteSpace(resumeMode))
            resumeMode = "fresh";

        var sb = new StringBuilder();
        sb.AppendLine("[PIPELINE CONTEXT]");
        sb.AppendLine($"You are executing node \"{node.Id}\" in pipeline \"{graph.Name}\".");
        sb.AppendLine();
        sb.AppendLine("Your working directory (CWD) is the project root:");
        sb.AppendLine($"  {projectRoot}");
        sb.AppendLine();
        sb.AppendLine("Pipeline artifacts are stored at:");
        sb.AppendLine($"  {logsRoot}/");
        sb.AppendLine();
        sb.AppendLine("Runtime settings:");
        sb.AppendLine($"  Runtime fidelity: {fidelity}");
        sb.AppendLine($"  Runtime thread: {thread}");
        if (!string.Equals(stageId, node.Id, StringComparison.Ordinal))
            sb.AppendLine($"  Runtime stage: {stageId}");
        sb.AppendLine($"  Resume mode: {resumeMode}");
        if (!string.IsNullOrWhiteSpace(helperProvider))
            sb.AppendLine($"  Helper provider: {helperProvider}");
        if (!string.IsNullOrWhiteSpace(helperModel))
            sb.AppendLine($"  Helper model: {helperModel}");
        if (!string.IsNullOrWhiteSpace(helperReasoning))
            sb.AppendLine($"  Helper reasoning effort: {helperReasoning}");
        if (outgoingLabels.Count > 0)
            sb.AppendLine($"  Outgoing labels: {string.Join(", ", outgoingLabels)}");
        if (!string.IsNullOrWhiteSpace(executionOptions.StageClass))
            sb.AppendLine($"  Stage class: {executionOptions.StageClass}");
        if (executionOptions.MaxProviderResponseMs is int timeoutMs)
            sb.AppendLine($"  Provider timeout ms: {timeoutMs}");
        sb.AppendLine($"  Require edits: {executionOptions.RequireEdits}");
        sb.AppendLine($"  Require verification: {executionOptions.RequireVerification}");
        sb.AppendLine($"  Validation mode: {executionOptions.EffectiveValidation.Mode}");
        if (!string.IsNullOrWhiteSpace(executionOptions.EffectiveValidation.Profile))
            sb.AppendLine($"  Validation profile: {executionOptions.EffectiveValidation.Profile}");
        if (executionOptions.EffectiveValidation.TimeoutMs is int validationTimeoutMs)
            sb.AppendLine($"  Validation timeout ms: {validationTimeoutMs}");
        sb.AppendLine($"  Validation fail action: {executionOptions.EffectiveValidation.FailAction}");
        sb.AppendLine($"  Declared validation checks: {executionOptions.EffectiveValidation.EffectiveChecks.Count}");
        sb.AppendLine($"  Allow contract fallback: {executionOptions.AllowContractFallback}");
        if (!string.IsNullOrWhiteSpace(executionOptions.CodergenVersion))
            sb.AppendLine($"  Codergen version: {executionOptions.CodergenVersion}");
        sb.AppendLine();
        if (executionOptions.EffectiveValidation.Mode != RuntimeValidationModes.None)
        {
            sb.AppendLine("Runtime validation is authoritative for this stage.");
            sb.AppendLine("Use queue_validation_check to register final validation checks; shell commands alone only count as observed telemetry.");
            sb.AppendLine();
        }
        sb.AppendLine("Artifact directory layout:");

        // List each node that has a logs subdirectory, marking which exist.
        foreach (var n in graph.Nodes.Values.OrderBy(n => n.Id))
        {
            if (n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase) ||
                n.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase) ||
                n.Shape.Equals("hexagon", StringComparison.OrdinalIgnoreCase))
                continue;

            var nodeDir = Path.Combine(logsRoot, n.Id);
            var exists = Directory.Exists(nodeDir);
            var marker = exists ? "(exists)" : "(not yet created)";
            sb.AppendLine($"  {logsRoot}/{n.Id,-25} {marker}");
        }

        sb.AppendLine();
        sb.AppendLine($"Current stage directory: {Path.Combine(logsRoot, stageId)}");
        sb.AppendLine($"Run output root: {outputRoot}");
        sb.AppendLine("Use ABSOLUTE paths when reading/writing pipeline artifacts.");
        sb.AppendLine("Source code files can use paths relative to the project root (your CWD).");
        sb.AppendLine("[/PIPELINE CONTEXT]");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string ExpandVariables(string prompt, PipelineContext context, Graph graph)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        var expanded = prompt.Replace("$goal", graph.Goal);

        foreach (var (key, value) in context.All)
        {
            expanded = expanded.Replace($"${{context.{key}}}", value);
        }

        return expanded;
    }

    private static string ResolveHelperAttribute(GraphNode node, Graph graph, string suffix)
    {
        var nodeKey = $"helper_{suffix}";
        if (node.RawAttributes.TryGetValue(nodeKey, out var nodeValue) && !string.IsNullOrWhiteSpace(nodeValue))
            return nodeValue;

        return graph.Attributes.TryGetValue(nodeKey, out var graphValue) ? graphValue : string.Empty;
    }

    private static async Task PersistSupplementalArtifactsAsync(
        string stageDir,
        IReadOnlyDictionary<string, string>? artifacts,
        CancellationToken ct)
    {
        if (artifacts is null || artifacts.Count == 0)
            return;

        foreach (var (relativePath, content) in artifacts)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var safePath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(stageDir, safePath));
            if (!fullPath.StartsWith(stageDir, StringComparison.Ordinal))
                continue;

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(fullPath, content ?? string.Empty, ct);
        }
    }
}
