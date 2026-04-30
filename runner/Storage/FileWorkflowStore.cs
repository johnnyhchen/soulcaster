namespace Soulcaster.Runner.Storage;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soulcaster.UnifiedLlm;

internal sealed class FileWorkflowStore : IWorkflowStore
{
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions StoreJsonLines = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly Regex ContractAttemptSuffixPattern = new(
        @"^(?<stem>.+)-attempt-(?<attempt>\d+)(?<ext>\.[^.]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ArchivedStageAttemptPattern = new(
        @"^(?<stem>.+)\.previous\.attempt-(?<attempt>\d+)(?<ext>\.[^.]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public FileWorkflowStore(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        StoreDirectory = Path.Combine(WorkingDirectory, "store");
    }

    public string WorkingDirectory { get; }

    public string StoreDirectory { get; }

    public string BackendId => "file_workflow_store";

    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(StoreDirectory);

            var logsDir = Path.Combine(WorkingDirectory, "logs");
            var gatesDir = Path.Combine(WorkingDirectory, "gates");
            var manifestPath = Path.Combine(WorkingDirectory, "run-manifest.json");
            var lockPath = Path.Combine(WorkingDirectory, "run.lock");
            var resultPath = Path.Combine(logsDir, "result.json");
            var checkpointPath = Path.Combine(logsDir, "checkpoint.json");
            var eventsPath = Path.Combine(logsDir, "events.jsonl");

            var manifest = await LoadPreferredManifestAsync(manifestPath, ct);
            var runId = !string.IsNullOrWhiteSpace(manifest?.run_id)
                ? manifest!.run_id
                : Path.GetFileName(WorkingDirectory);

            var runEvents = BuildRunEvents(runId, ReadJsonLines(eventsPath));
            var stageAttempts = BuildStageAttempts(runId, logsDir, runEvents);
            var gates = BuildGates(runId, gatesDir);
            var gateAnswers = BuildGateAnswers(runId, gates);
            var artifactProjection = BuildArtifactProjection(runId, logsDir, stageAttempts);
            var artifactVersions = artifactProjection.Versions;
            var artifactLineage = artifactProjection.Lineage;
            var artifacts = BuildArtifacts(runId, artifactVersions);
            var agentSessions = BuildAgentSessions(runId, stageAttempts);
            var providerInvocations = BuildProviderInvocations(runId, stageAttempts);
            var modelScorecards = BuildModelScorecards(runId, providerInvocations);
            var leases = BuildLeases(runId, lockPath, runEvents);
            var runs = BuildRuns(
                runId,
                manifest,
                WorkingDirectory,
                resultPath,
                checkpointPath,
                runEvents.Count,
                stageAttempts.Count,
                gates.Count,
                artifactVersions.Count);
            var replay = BuildReplay(runId, runEvents);

            await WriteJsonAsync(Path.Combine(StoreDirectory, "runs.json"), runs, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "stage_attempts.json"), stageAttempts, ct);
            await WriteJsonLinesAsync(Path.Combine(StoreDirectory, "run_events.jsonl"), runEvents, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "gate_instances.json"), gates, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "gate_answers.json"), gateAnswers, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "artifacts.json"), artifacts, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "artifact_versions.json"), artifactVersions, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "artifact_lineage.json"), artifactLineage, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "agent_sessions.json"), agentSessions, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "provider_invocations.json"), providerInvocations, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "model_scorecards.json"), modelScorecards, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "leases.json"), leases, ct);
            await WriteJsonAsync(Path.Combine(StoreDirectory, "replay.json"), replay, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static List<Dictionary<string, object?>> BuildRunEvents(
        string runId,
        IReadOnlyList<Dictionary<string, object?>> rawEvents)
    {
        var events = new List<Dictionary<string, object?>>(rawEvents.Count);
        var stageOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var activeStageAttempts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < rawEvents.Count; index++)
        {
            var raw = new Dictionary<string, object?>(rawEvents[index], StringComparer.Ordinal);
            var eventType = GetString(raw, "event_type") ?? "unknown";
            var nodeId = GetString(raw, "node_id");
            string? stageAttemptId = null;

            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                if (string.Equals(eventType, "stage_start", StringComparison.OrdinalIgnoreCase))
                {
                    var ordinal = stageOrdinals.TryGetValue(nodeId, out var current) ? current + 1 : 1;
                    stageOrdinals[nodeId] = ordinal;
                    stageAttemptId = BuildStageAttemptId(runId, nodeId, ordinal);
                    activeStageAttempts[nodeId] = stageAttemptId;
                }
                else if (activeStageAttempts.TryGetValue(nodeId, out var activeStageAttemptId))
                {
                    stageAttemptId = activeStageAttemptId;
                }
            }

            raw["run_id"] = runId;
            raw["event_id"] = $"{runId}:event:{index + 1}";
            raw["sequence"] = index + 1;

            if (!string.IsNullOrWhiteSpace(stageAttemptId))
                raw["stage_attempt_id"] = stageAttemptId;

            if (!raw.ContainsKey("gate_id") && TryGetNestedString(raw, "data", "gate_id", out var gateId))
                raw["gate_id"] = gateId;

            if (!raw.ContainsKey("lease_id") && eventType.StartsWith("lease_", StringComparison.OrdinalIgnoreCase))
                raw["lease_id"] = $"{runId}:lease:primary";

            events.Add(raw);
        }

        return events;
    }

    private List<StageAttemptSnapshot> BuildStageAttempts(
        string runId,
        string logsDir,
        IReadOnlyList<Dictionary<string, object?>> runEvents)
    {
        var attempts = new List<StageAttemptSnapshot>();
        if (!Directory.Exists(logsDir))
            return attempts;

        var eventsByStageAttemptId = runEvents
            .Where(evt => !string.IsNullOrWhiteSpace(GetString(evt, "stage_attempt_id")))
            .GroupBy(evt => GetString(evt, "stage_attempt_id")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var stageDir in Directory.GetDirectories(logsDir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var nodeId = Path.GetFileName(stageDir);
            var statusFiles = Directory
                .GetFiles(stageDir, "status*.json")
                .Where(path =>
                {
                    var fileName = Path.GetFileName(path);
                    return fileName.Equals("status.json", StringComparison.OrdinalIgnoreCase) ||
                           fileName.StartsWith("status.previous.", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path =>
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.Equals("status.json", StringComparison.OrdinalIgnoreCase))
                        return int.MaxValue;

                    return TryGetArchivedStageAttemptNumber(fileName, out var archivedAttempt)
                        ? archivedAttempt
                        : 0;
                })
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var attemptNumber = 0;
            foreach (var statusPath in statusFiles)
            {
                attemptNumber++;
                var status = ReadJsonObject(statusPath);
                var telemetry = GetObject(status, "telemetry");
                var stageAttemptId = BuildStageAttemptId(runId, nodeId, attemptNumber);
                eventsByStageAttemptId.TryGetValue(stageAttemptId, out var stageEvents);

                var startedAt = stageEvents?
                    .FirstOrDefault(evt => string.Equals(GetString(evt, "event_type"), "stage_start", StringComparison.OrdinalIgnoreCase))
                    ?.GetValueOrDefault("timestamp_utc")?.ToString();
                var endedAt = stageEvents?
                    .LastOrDefault(evt => string.Equals(GetString(evt, "event_type"), "stage_end", StringComparison.OrdinalIgnoreCase))
                    ?.GetValueOrDefault("timestamp_utc")?.ToString();

                var durationMs = GetInt(status, "duration_ms")
                    ?? GetInt(telemetry, "duration_ms")
                    ?? stageEvents?
                        .LastOrDefault(evt => string.Equals(GetString(evt, "event_type"), "stage_end", StringComparison.OrdinalIgnoreCase))
                        .Let(evt => GetInt(evt, "duration_ms"));

                attempts.Add(new StageAttemptSnapshot(
                    StageAttemptId: stageAttemptId,
                    RunId: runId,
                    NodeId: nodeId,
                    AttemptNumber: attemptNumber,
                    IsCurrent: Path.GetFileName(statusPath).Equals("status.json", StringComparison.OrdinalIgnoreCase),
                    Status: GetString(status, "status") ?? "unknown",
                    Notes: GetString(status, "notes"),
                    FailureKind: GetString(status, "failure_kind"),
                    Provider: GetString(status, "provider"),
                    Model: GetString(status, "model"),
                    ProviderState: GetString(status, "provider_state"),
                    ContractState: GetString(status, "contract_state"),
                    EditState: GetString(status, "edit_state"),
                    VerificationState: GetString(status, "verification_state"),
                    AuthoritativeValidationState: GetString(status, "authoritative_validation_state"),
                    AdvanceAllowed: GetBoolean(status, "advance_allowed"),
                    StartedAt: startedAt,
                    EndedAt: endedAt,
                    DurationMs: durationMs,
                    StatusPath: Path.GetRelativePath(WorkingDirectory, statusPath),
                    Telemetry: telemetry));
            }
        }

        return attempts;
    }

    private List<GateSnapshot> BuildGates(string runId, string gatesDir)
    {
        var gates = new List<GateSnapshot>();
        if (!Directory.Exists(gatesDir))
            return gates;

        var pendingPath = Path.Combine(gatesDir, "pending");
        var pendingGateId = File.Exists(pendingPath) ? File.ReadAllText(pendingPath).Trim() : null;

        foreach (var gateDir in Directory.GetDirectories(gatesDir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var gateId = Path.GetFileName(gateDir);
            var questionPath = Path.Combine(gateDir, "question.json");
            if (!File.Exists(questionPath))
                continue;

            var question = ReadJsonObject(questionPath);
            var metadata = GetObject(question, "metadata");
            var answerPath = Path.Combine(gateDir, "answer.json");
            var answer = File.Exists(answerPath) ? ReadJsonObject(answerPath) : null;

            gates.Add(new GateSnapshot(
                GateId: gateId,
                RunId: runId,
                NodeId: GetString(metadata, "node_id"),
                Question: GetString(question, "text") ?? gateId,
                QuestionType: GetString(question, "type") ?? "Unknown",
                Options: GetStringList(question, "options"),
                DefaultChoice: GetString(metadata, "default_choice"),
                CreatedAt: GetString(question, "created_at") ?? GetString(question, "timestamp"),
                IsPending: string.Equals(gateId, pendingGateId, StringComparison.Ordinal),
                Status: GetString(answer, "status") ?? (File.Exists(answerPath) ? "answered" : "pending"),
                AnswerText: GetString(answer, "text"),
                SelectedOptions: GetStringList(answer, "selected_options"),
                AnsweredAt: GetString(answer, "answered_at") ?? GetString(answer, "timestamp"),
                Actor: GetString(answer, "actor"),
                Rationale: GetString(answer, "rationale"),
                Source: GetString(answer, "source"),
                QuestionPath: Path.GetRelativePath(WorkingDirectory, questionPath),
                AnswerPath: File.Exists(answerPath) ? Path.GetRelativePath(WorkingDirectory, answerPath) : null));
        }

        return gates;
    }

    private static List<GateAnswerSnapshot> BuildGateAnswers(string runId, IReadOnlyList<GateSnapshot> gates)
    {
        return gates
            .Where(gate => !string.IsNullOrWhiteSpace(gate.AnswerText) || gate.SelectedOptions.Count > 0)
            .Select(gate => new GateAnswerSnapshot(
                GateAnswerId: $"{runId}:{gate.GateId}:answer:1",
                RunId: runId,
                GateId: gate.GateId,
                NodeId: gate.NodeId,
                Status: gate.Status,
                Text: gate.AnswerText,
                SelectedOptions: gate.SelectedOptions,
                Actor: gate.Actor,
                Rationale: gate.Rationale,
                Source: gate.Source,
                AnsweredAt: gate.AnsweredAt,
                AnswerPath: gate.AnswerPath))
            .ToList();
    }

    private ArtifactProjectionSnapshot BuildArtifactProjection(
        string runId,
        string logsDir,
        IReadOnlyList<StageAttemptSnapshot> stageAttempts)
    {
        var artifactRegistryState = ArtifactRegistryStateStore.Load(StoreDirectory);
        if (!Directory.Exists(logsDir))
            return new ArtifactProjectionSnapshot([], []);

        var rawVersions = new List<ArtifactVersionSnapshot>();
        var rawLineage = new List<ArtifactLineageSnapshot>();
        var priorVersions = new List<ArtifactVersionSnapshot>();
        var currentVersionsByArtifactId = new Dictionary<string, ArtifactVersionSnapshot>(StringComparer.Ordinal);
        var currentAttemptByNode = stageAttempts
            .GroupBy(attempt => attempt.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(attempt => attempt.AttemptNumber).First(),
                StringComparer.OrdinalIgnoreCase);
        var candidates = CollectArtifactCandidates(runId, logsDir, stageAttempts, currentAttemptByNode);
        var candidatesByAttempt = candidates
            .GroupBy(candidate => candidate.StageAttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var orderedStageAttempts = stageAttempts
            .Where(attempt => candidatesByAttempt.ContainsKey(attempt.StageAttemptId))
            .OrderBy(GetStageAttemptProjectionSortKey)
            .ThenBy(attempt => attempt.NodeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(attempt => attempt.AttemptNumber)
            .ToList();

        foreach (var stageAttempt in orderedStageAttempts)
        {
            if (!candidatesByAttempt.TryGetValue(stageAttempt.StageAttemptId, out var stageCandidates))
                continue;

            var stageDir = Path.GetDirectoryName(Path.Combine(WorkingDirectory, stageAttempt.StatusPath))
                ?? Path.Combine(logsDir, stageAttempt.NodeId);
            var promptPath = Path.Combine(stageDir, "prompt.md");
            var promptRelativePath = File.Exists(promptPath)
                ? Path.GetRelativePath(WorkingDirectory, promptPath).Replace('\\', '/')
                : null;
            var promptSha256 = File.Exists(promptPath) ? ComputeSha256(promptPath) : null;
            var promptContent = File.Exists(promptPath) ? File.ReadAllText(promptPath) : null;
            var inputRefs = ResolvePromptArtifactRefs(promptContent, priorVersions, currentVersionsByArtifactId);

            foreach (var candidate in stageCandidates
                         .OrderBy(candidate => candidate.ProducedAtUtc)
                         .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var artifactId = $"{runId}:{candidate.LogicalPath}";
                currentVersionsByArtifactId.TryGetValue(artifactId, out var supersededVersion);
                var identityToken = ComputeSha256Text($"{stageAttempt.StageAttemptId}|{candidate.RelativePath}|{candidate.Sha256}");
                var versionId = $"{runId}:artifact-version:{identityToken[..Math.Min(20, identityToken.Length)]}";
                var snapshot = new ArtifactVersionSnapshot(
                    ArtifactVersionId: versionId,
                    ArtifactId: artifactId,
                    RunId: runId,
                    NodeId: stageAttempt.NodeId,
                    StageAttemptId: stageAttempt.StageAttemptId,
                    RelativePath: candidate.RelativePath,
                    LogicalPath: candidate.LogicalPath,
                    ProducedAt: candidate.ProducedAtUtc.ToString("o"),
                    SizeBytes: candidate.SizeBytes,
                    Sha256: candidate.Sha256,
                    MediaType: candidate.MediaType,
                    ApprovalState: "generated",
                    IsDefault: false,
                    Actor: null,
                    Rationale: null,
                    Source: null,
                    ApprovedAt: null,
                    ProducerProvider: stageAttempt.Provider,
                    ProducerModel: stageAttempt.Model,
                    PromptPath: promptRelativePath,
                    PromptSha256: promptSha256,
                    InputArtifactVersionIds: inputRefs.Select(item => item.ArtifactVersionId).ToList(),
                    SupersedesArtifactVersionId: supersededVersion?.ArtifactVersionId);

                rawVersions.Add(snapshot);
                priorVersions.Add(snapshot);
                currentVersionsByArtifactId[artifactId] = snapshot;

                foreach (var inputRef in inputRefs)
                {
                    rawLineage.Add(new ArtifactLineageSnapshot(
                        ArtifactLineageId: BuildArtifactLineageId(snapshot.ArtifactVersionId, "input", inputRef.ArtifactVersionId),
                        RunId: runId,
                        ArtifactId: snapshot.ArtifactId,
                        ArtifactVersionId: snapshot.ArtifactVersionId,
                        RelatedArtifactId: inputRef.ArtifactId,
                        RelatedArtifactVersionId: inputRef.ArtifactVersionId,
                        StageAttemptId: stageAttempt.StageAttemptId,
                        RelationType: "input",
                        LogicalPath: snapshot.LogicalPath,
                        RelatedLogicalPath: inputRef.LogicalPath,
                        SourcePath: promptRelativePath,
                        CreatedAt: snapshot.ProducedAt));
                }

                if (supersededVersion is not null)
                {
                    rawLineage.Add(new ArtifactLineageSnapshot(
                        ArtifactLineageId: BuildArtifactLineageId(snapshot.ArtifactVersionId, "supersedes", supersededVersion.ArtifactVersionId),
                        RunId: runId,
                        ArtifactId: snapshot.ArtifactId,
                        ArtifactVersionId: snapshot.ArtifactVersionId,
                        RelatedArtifactId: supersededVersion.ArtifactId,
                        RelatedArtifactVersionId: supersededVersion.ArtifactVersionId,
                        StageAttemptId: stageAttempt.StageAttemptId,
                        RelationType: "supersedes",
                        LogicalPath: snapshot.LogicalPath,
                        RelatedLogicalPath: supersededVersion.LogicalPath,
                        SourcePath: null,
                        CreatedAt: snapshot.ProducedAt));
                }
            }
        }

        var versions = new List<ArtifactVersionSnapshot>(rawVersions.Count);
        foreach (var group in rawVersions.GroupBy(version => version.ArtifactId, StringComparer.Ordinal))
        {
            artifactRegistryState.Selections.TryGetValue(group.Key, out var explicitSelection);
            var selectedVersion = explicitSelection is null
                ? null
                : group.FirstOrDefault(version =>
                    string.Equals(version.ArtifactVersionId, explicitSelection.CurrentVersionId, StringComparison.Ordinal));

            var defaultVersion = selectedVersion ?? group
                .OrderByDescending(version => version.ProducedAt, StringComparer.Ordinal)
                .ThenByDescending(version => version.RelativePath, StringComparer.OrdinalIgnoreCase)
                .First();

            versions.AddRange(group.Select(version => version with
            {
                IsDefault = version.ArtifactVersionId == defaultVersion.ArtifactVersionId,
                ApprovalState = version.ArtifactVersionId == defaultVersion.ArtifactVersionId
                    ? explicitSelection?.ApprovalState ?? "approved"
                    : "superseded",
                Actor = version.ArtifactVersionId == defaultVersion.ArtifactVersionId ? explicitSelection?.Actor : null,
                Rationale = version.ArtifactVersionId == defaultVersion.ArtifactVersionId ? explicitSelection?.Rationale : null,
                Source = version.ArtifactVersionId == defaultVersion.ArtifactVersionId ? explicitSelection?.Source : null,
                ApprovedAt = version.ArtifactVersionId == defaultVersion.ArtifactVersionId ? explicitSelection?.UpdatedAtUtc : null
            }));
        }

        return new ArtifactProjectionSnapshot(
            versions,
            rawLineage
                .OrderBy(lineage => lineage.CreatedAt, StringComparer.Ordinal)
                .ThenBy(lineage => lineage.ArtifactVersionId, StringComparer.Ordinal)
                .ThenBy(lineage => lineage.RelationType, StringComparer.Ordinal)
                .ToList());
    }

    private static List<ArtifactSnapshot> BuildArtifacts(
        string runId,
        IReadOnlyList<ArtifactVersionSnapshot> artifactVersions)
    {
        return artifactVersions
            .GroupBy(version => version.ArtifactId, StringComparer.Ordinal)
            .Select(group =>
            {
                var currentVersion = group.First(version => version.IsDefault);
                return new ArtifactSnapshot(
                    ArtifactId: group.Key,
                    RunId: runId,
                    NodeId: currentVersion.NodeId,
                    LogicalPath: currentVersion.LogicalPath,
                    CurrentVersionId: currentVersion.ArtifactVersionId,
                    VersionCount: group.Count(),
                    ApprovalState: currentVersion.ApprovalState,
                    Actor: currentVersion.Actor,
                    Rationale: currentVersion.Rationale,
                    Source: currentVersion.Source,
                    ApprovedAt: currentVersion.ApprovedAt);
            })
            .OrderBy(artifact => artifact.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<AgentSessionSnapshot> BuildAgentSessions(
        string runId,
        IReadOnlyList<StageAttemptSnapshot> stageAttempts)
    {
        var sessions = new List<AgentSessionSnapshot>();

        foreach (var stageAttempt in stageAttempts)
        {
            if (stageAttempt.Telemetry is null)
                continue;

            sessions.Add(new AgentSessionSnapshot(
                AgentSessionId: $"{stageAttempt.StageAttemptId}:primary",
                RunId: runId,
                StageAttemptId: stageAttempt.StageAttemptId,
                NodeId: stageAttempt.NodeId,
                ParentAgentSessionId: null,
                Role: "primary",
                Provider: stageAttempt.Provider,
                Model: stageAttempt.Model,
                LifecycleState: ResolveAgentLifecycle(stageAttempt.Status),
                AssistantTurns: GetInt(stageAttempt.Telemetry, "assistant_turns"),
                ToolCalls: GetInt(stageAttempt.Telemetry, "tool_calls"),
                ToolErrors: GetInt(stageAttempt.Telemetry, "tool_errors"),
                DurationMs: stageAttempt.DurationMs,
                Metadata: stageAttempt.Telemetry));

            var helperSessions = GetObjectList(stageAttempt.Telemetry, "helper_sessions");
            for (var index = 0; index < helperSessions.Count; index++)
            {
                var helper = helperSessions[index];
                sessions.Add(new AgentSessionSnapshot(
                    AgentSessionId: $"{stageAttempt.StageAttemptId}:helper:{index + 1}",
                    RunId: runId,
                    StageAttemptId: stageAttempt.StageAttemptId,
                    NodeId: stageAttempt.NodeId,
                    ParentAgentSessionId: $"{stageAttempt.StageAttemptId}:primary",
                    Role: "helper",
                    Provider: GetString(helper, "provider"),
                    Model: GetString(helper, "model"),
                    LifecycleState: "completed",
                    AssistantTurns: GetInt(helper, "assistant_turns"),
                    ToolCalls: GetInt(helper, "tool_calls"),
                    ToolErrors: GetInt(helper, "tool_errors"),
                    DurationMs: GetInt(helper, "duration_ms"),
                    Metadata: helper));
            }
        }

        return sessions;
    }

    private static List<ProviderInvocationSnapshot> BuildProviderInvocations(
        string runId,
        IReadOnlyList<StageAttemptSnapshot> stageAttempts)
    {
        return stageAttempts
            .Where(stageAttempt => !string.IsNullOrWhiteSpace(stageAttempt.Provider) || !string.IsNullOrWhiteSpace(stageAttempt.Model))
            .Select(stageAttempt => new ProviderInvocationSnapshot(
                ProviderInvocationId: $"{stageAttempt.StageAttemptId}:provider:1",
                RunId: runId,
                StageAttemptId: stageAttempt.StageAttemptId,
                NodeId: stageAttempt.NodeId,
                Provider: stageAttempt.Provider,
                Model: stageAttempt.Model,
                StageStatus: stageAttempt.Status,
                ProviderState: stageAttempt.ProviderState,
                FailureKind: stageAttempt.FailureKind,
                ProviderStatusCode: GetInt(stageAttempt.Telemetry, "provider_status_code"),
                ProviderRetryable: GetBoolean(stageAttempt.Telemetry, "provider_retryable"),
                ProviderTimeoutMs: GetInt(stageAttempt.Telemetry, "provider_timeout_ms"),
                DurationMs: stageAttempt.DurationMs,
                TokenUsage: GetObject(stageAttempt.Telemetry, "token_usage"),
                VerificationState: stageAttempt.VerificationState,
                ErrorMessage: GetString(stageAttempt.Telemetry, "provider_error_message")))
            .ToList();
    }

    private static List<ModelScorecardSnapshot> BuildModelScorecards(
        string runId,
        IReadOnlyList<ProviderInvocationSnapshot> providerInvocations)
    {
        return providerInvocations
            .Where(invocation => !string.IsNullOrWhiteSpace(invocation.Provider) && !string.IsNullOrWhiteSpace(invocation.Model))
            .GroupBy(
                invocation => $"{invocation.Provider}\u001f{invocation.Model}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var invocations = group.ToList();
                var first = invocations[0];
                var provider = first.Provider!;
                var model = first.Model!;
                var invocationCount = invocations.Count;
                var successCount = invocations.Count(invocation => IsSuccessfulStageStatus(invocation.StageStatus));
                var failureCount = invocationCount - successCount;
                var durations = invocations
                    .Where(invocation => invocation.DurationMs is not null)
                    .Select(invocation => invocation.DurationMs!.Value)
                    .OrderBy(value => value)
                    .ToList();
                var avgDurationMs = durations.Count == 0
                    ? (long?)null
                    : (long)Math.Round(durations.Average(), MidpointRounding.AwayFromZero);
                var p95DurationMs = durations.Count == 0
                    ? (long?)null
                    : durations[(int)Math.Clamp(Math.Ceiling(durations.Count * 0.95d) - 1, 0, durations.Count - 1)];
                var inputTokens = invocations.Sum(invocation => (int)(GetInt(invocation.TokenUsage, "input_tokens") ?? 0));
                var outputTokens = invocations.Sum(invocation => (int)(GetInt(invocation.TokenUsage, "output_tokens") ?? 0));
                var totalTokens = invocations.Sum(invocation => (int)(GetInt(invocation.TokenUsage, "total_tokens") ?? 0));
                var modelInfo = ModelCatalog.GetModelInfo(model);
                decimal? estimatedInputCost = modelInfo?.InputCostPerMillion is decimal inputCostPerMillion
                    ? Math.Round(inputTokens / 1_000_000m * inputCostPerMillion, 6, MidpointRounding.AwayFromZero)
                    : null;
                decimal? estimatedOutputCost = modelInfo?.OutputCostPerMillion is decimal outputCostPerMillion
                    ? Math.Round(outputTokens / 1_000_000m * outputCostPerMillion, 6, MidpointRounding.AwayFromZero)
                    : null;
                var hasInputComponent = inputTokens > 0;
                var hasOutputComponent = outputTokens > 0;
                var inputPriced = !hasInputComponent || estimatedInputCost is not null;
                var outputPriced = !hasOutputComponent || estimatedOutputCost is not null;
                var pricingCoverage = !hasInputComponent && !hasOutputComponent
                    ? "none"
                    : inputPriced && outputPriced
                        ? "full"
                        : estimatedInputCost is not null || estimatedOutputCost is not null
                            ? "partial"
                            : "none";
                decimal? estimatedKnownCost = estimatedInputCost is null && estimatedOutputCost is null
                    ? null
                    : (estimatedInputCost ?? 0m) + (estimatedOutputCost ?? 0m);
                decimal? estimatedTotalCost = pricingCoverage == "full" && estimatedKnownCost is decimal knownCost
                    ? knownCost
                    : null;

                return new ModelScorecardSnapshot(
                    ModelScorecardId: $"{runId}:{provider}:{model}",
                    RunId: runId,
                    Provider: provider,
                    Model: model,
                    InvocationCount: invocationCount,
                    SuccessCount: successCount,
                    FailureCount: failureCount,
                    SuccessRate: invocationCount == 0
                        ? 0m
                        : Math.Round(successCount / (decimal)invocationCount, 4, MidpointRounding.AwayFromZero),
                    AvgDurationMs: avgDurationMs,
                    P95DurationMs: p95DurationMs,
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    TotalTokens: totalTokens,
                    EstimatedInputCostUsd: estimatedInputCost,
                    EstimatedOutputCostUsd: estimatedOutputCost,
                    EstimatedKnownCostUsd: estimatedKnownCost,
                    EstimatedTotalCostUsd: estimatedTotalCost,
                    PricingCoverage: pricingCoverage,
                    FailureKinds: CountBy(invocations.Select(invocation => invocation.FailureKind)),
                    ProviderStates: CountBy(invocations.Select(invocation => invocation.ProviderState)),
                    VerificationStates: CountBy(invocations.Select(invocation => invocation.VerificationState)));
            })
            .OrderBy(scorecard => scorecard.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(scorecard => scorecard.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LeaseSnapshot> BuildLeases(
        string runId,
        string lockPath,
        IReadOnlyList<Dictionary<string, object?>> runEvents)
    {
        var leases = new Dictionary<string, LeaseSnapshot>(StringComparer.Ordinal);

        foreach (var evt in runEvents)
        {
            var eventType = GetString(evt, "event_type");
            if (!string.Equals(eventType, "lease_acquired", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(eventType, "lease_released", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var leaseId = GetString(evt, "lease_id") ?? $"{runId}:lease:primary";
            leases.TryGetValue(leaseId, out var current);

            if (string.Equals(eventType, "lease_acquired", StringComparison.OrdinalIgnoreCase))
            {
                leases[leaseId] = new LeaseSnapshot(
                    LeaseId: leaseId,
                    RunId: runId,
                    OwnerPid: GetInt(evt, "pid"),
                    AcquiredAt: GetString(evt, "timestamp_utc"),
                    ReleasedAt: current?.ReleasedAt,
                    State: File.Exists(lockPath) ? "active" : "released");
            }
            else
            {
                leases[leaseId] = new LeaseSnapshot(
                    LeaseId: leaseId,
                    RunId: runId,
                    OwnerPid: current?.OwnerPid ?? GetInt(evt, "pid"),
                    AcquiredAt: current?.AcquiredAt,
                    ReleasedAt: GetString(evt, "timestamp_utc"),
                    State: "released");
            }
        }

        if (File.Exists(lockPath))
        {
            var lockRecord = ReadJsonObject(lockPath);
            var leaseId = $"{runId}:lease:primary";
            leases[leaseId] = new LeaseSnapshot(
                LeaseId: leaseId,
                RunId: runId,
                OwnerPid: GetInt(lockRecord, "pid"),
                AcquiredAt: GetString(lockRecord, "started_at"),
                ReleasedAt: null,
                State: "active");
        }

        return leases.Values
            .OrderBy(lease => lease.AcquiredAt, StringComparer.Ordinal)
            .ToList();
    }

    private static List<RunSnapshot> BuildRuns(
        string runId,
        RunManifest? manifest,
        string workingDirectory,
        string resultPath,
        string checkpointPath,
        int eventCount,
        int stageCount,
        int gateCount,
        int artifactVersionCount)
    {
        return
        [
            new RunSnapshot(
                RunId: runId,
                StateVersion: manifest?.state_version ?? 0,
                WorkingDirectory: workingDirectory,
                OutputDirectory: workingDirectory,
                GraphPath: manifest?.graph_path,
                Status: manifest?.status ?? (File.Exists(resultPath) ? "completed" : "unknown"),
                ActiveStage: manifest?.active_stage,
                StartedAt: manifest?.started_at,
                UpdatedAt: manifest?.updated_at,
                CheckpointPath: File.Exists(checkpointPath) ? checkpointPath : manifest?.checkpoint_path,
                ResultPath: File.Exists(resultPath) ? resultPath : manifest?.result_path,
                Crash: manifest?.crash,
                CancelRequestedAt: manifest?.cancel_requested_at,
                CancelRequestedActor: manifest?.cancel_requested_actor,
                CancelRequestedRationale: manifest?.cancel_requested_rationale,
                CancelRequestedSource: manifest?.cancel_requested_source,
                AutoResumePolicy: manifest?.auto_resume_policy,
                ResumeSource: manifest?.resume_source,
                RespawnCount: manifest?.respawn_count ?? 0,
                EventCount: eventCount,
                StageAttemptCount: stageCount,
                GateCount: gateCount,
                ArtifactVersionCount: artifactVersionCount,
                StoreBackend: "file_workflow_store")
        ];
    }

    private static ReplaySnapshot BuildReplay(
        string runId,
        IReadOnlyList<Dictionary<string, object?>> runEvents)
    {
        var items = runEvents
            .Select(evt => new ReplayItemSnapshot(
                Sequence: (int)(GetInt(evt, "sequence") ?? 0),
                TimestampUtc: GetString(evt, "timestamp_utc") ?? string.Empty,
                EventType: GetString(evt, "event_type") ?? "unknown",
                NodeId: GetString(evt, "node_id"),
                StageAttemptId: GetString(evt, "stage_attempt_id"),
                GateId: GetString(evt, "gate_id"),
                LeaseId: GetString(evt, "lease_id"),
                Summary: SummarizeEvent(evt),
                Data: evt))
            .ToList();

        return new ReplaySnapshot(
            RunId: runId,
            GeneratedAt: DateTime.UtcNow.ToString("o"),
            Events: items);
    }

    private static string SummarizeEvent(IReadOnlyDictionary<string, object?> evt)
    {
        var eventType = GetString(evt, "event_type") ?? "unknown";
        var nodeId = GetString(evt, "node_id");
        var gateId = GetString(evt, "gate_id");

        return eventType switch
        {
            "run_started" => "run started",
            "run_finished" => $"run finished with status {GetString(evt, "status") ?? "unknown"}",
            "run_crashed" => $"run crashed: {GetString(evt, "error") ?? "unknown error"}",
            "run_respawning" => "run scheduled for autoresume respawn",
            "run_resume_requested" => "operator requested run resume",
            "run_cancel_requested" => "operator requested run cancellation",
            "run_cancelled" => "run cancelled by operator",
            "lease_acquired" => $"lease acquired by pid {GetInt(evt, "pid")?.ToString() ?? "unknown"}",
            "lease_released" => $"lease released by pid {GetInt(evt, "pid")?.ToString() ?? "unknown"}",
            "stage_start" => $"stage {nodeId} started",
            "stage_end" => $"stage {nodeId} ended with {GetString(evt, "status") ?? "unknown"}",
            "stage_retry" => $"stage {nodeId} retry #{GetInt(evt, "retry_count")?.ToString() ?? "?"}",
            "operator_retry_stage_requested" => $"operator requested retry for {nodeId ?? "unknown stage"}",
            "operator_force_advanced" => $"operator forced run advance to {GetString(evt, "target_node") ?? "unknown"}",
            "gate_created" => $"gate {gateId} created for {nodeId ?? "operator"}",
            "gate_answered" => $"gate {gateId} answered with {GetString(evt, "text") ?? GetFirstListString(evt, "selected_options") ?? "no answer"}",
            "artifact_promoted" => $"artifact promoted: {GetString(evt, "artifact_id") ?? "unknown"}",
            "artifact_rolled_back" => $"artifact rolled back: {GetString(evt, "artifact_id") ?? "unknown"}",
            _ => eventType.Replace('_', ' ')
        };
    }

    private static string ResolveAgentLifecycle(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" or "partialsuccess" or "partial_success" => "completed",
            "retry" => "retrying",
            "fail" => "failed",
            _ => "completed"
        };
    }

    private static bool IsSuccessfulStageStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "success" or "partialsuccess" or "partial_success" => true,
            _ => false
        };
    }

    private static Dictionary<string, int> CountBy(IEnumerable<string?> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private List<ArtifactFileCandidate> CollectArtifactCandidates(
        string runId,
        string logsDir,
        IReadOnlyList<StageAttemptSnapshot> stageAttempts,
        IReadOnlyDictionary<string, StageAttemptSnapshot> currentAttemptByNode)
    {
        var candidates = new List<ArtifactFileCandidate>();

        foreach (var stageDir in Directory.GetDirectories(logsDir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var nodeId = Path.GetFileName(stageDir);
            foreach (var filePath in Directory.GetFiles(stageDir, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(filePath);
                var relativePath = Path.GetRelativePath(WorkingDirectory, filePath).Replace('\\', '/');
                var logicalFileName = NormalizeLogicalArtifactFileName(fileName);
                var logicalPath = Path.Combine(Path.GetDirectoryName(relativePath) ?? string.Empty, logicalFileName)
                    .Replace('\\', '/');
                var stageAttemptId = ResolveArtifactStageAttemptId(runId, nodeId, fileName, stageAttempts, currentAttemptByNode);
                var info = new FileInfo(filePath);

                candidates.Add(new ArtifactFileCandidate(
                    StageAttemptId: stageAttemptId,
                    NodeId: nodeId,
                    RelativePath: relativePath,
                    LogicalPath: logicalPath,
                    ProducedAtUtc: info.LastWriteTimeUtc,
                    SizeBytes: info.Exists ? info.Length : 0,
                    Sha256: ComputeSha256(filePath),
                    MediaType: GuessMediaType(filePath)));
            }
        }

        return candidates;
    }

    private DateTimeOffset GetStageAttemptProjectionSortKey(StageAttemptSnapshot stageAttempt)
    {
        if (TryParseTimestamp(stageAttempt.StartedAt, out var startedAt))
            return startedAt;
        if (TryParseTimestamp(stageAttempt.EndedAt, out var endedAt))
            return endedAt;

        var statusPath = Path.Combine(WorkingDirectory, stageAttempt.StatusPath);
        return File.Exists(statusPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(statusPath))
            : DateTimeOffset.MinValue;
    }

    private List<ArtifactVersionSnapshot> ResolvePromptArtifactRefs(
        string? promptContent,
        IReadOnlyList<ArtifactVersionSnapshot> priorVersions,
        IReadOnlyDictionary<string, ArtifactVersionSnapshot> currentVersionsByArtifactId)
    {
        if (string.IsNullOrWhiteSpace(promptContent))
            return [];

        var normalizedPrompt = NormalizePathForMatching(promptContent);
        var matches = new List<ArtifactVersionSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var version in priorVersions)
        {
            var absolutePath = NormalizePathForMatching(Path.Combine(WorkingDirectory, version.RelativePath));
            if (!normalizedPrompt.Contains(absolutePath, StringComparison.Ordinal))
                continue;

            if (seen.Add(version.ArtifactVersionId))
                matches.Add(version);
        }

        foreach (var currentVersion in currentVersionsByArtifactId.Values.OrderBy(version => version.LogicalPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!normalizedPrompt.Contains(currentVersion.LogicalPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen.Add(currentVersion.ArtifactVersionId))
                matches.Add(currentVersion);
        }

        return matches;
    }

    private static string ResolveArtifactStageAttemptId(
        string runId,
        string nodeId,
        string fileName,
        IReadOnlyList<StageAttemptSnapshot> stageAttempts,
        IReadOnlyDictionary<string, StageAttemptSnapshot> currentAttemptByNode)
    {
        if (TryGetArchivedStageAttemptNumber(fileName, out var archivedStageAttempt))
            return BuildStageAttemptId(runId, nodeId, archivedStageAttempt);

        if (fileName.Equals("status.json", StringComparison.OrdinalIgnoreCase))
        {
            return currentAttemptByNode.TryGetValue(nodeId, out var currentAttempt)
                ? currentAttempt.StageAttemptId
                : BuildStageAttemptId(runId, nodeId, 1);
        }

        var previousStatuses = stageAttempts
            .Where(stageAttempt => stageAttempt.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(stageAttempt => stageAttempt.AttemptNumber)
            .ToList();

        if (fileName.StartsWith("status.previous.", StringComparison.OrdinalIgnoreCase))
        {
            var previousAttempt = previousStatuses.FirstOrDefault(stageAttempt =>
                string.Equals(
                    Path.GetFileName(stageAttempt.StatusPath),
                    fileName,
                    StringComparison.OrdinalIgnoreCase));

            return previousAttempt?.StageAttemptId
                   ?? BuildStageAttemptId(runId, nodeId, Math.Max(1, previousStatuses.Count - 1));
        }

        return currentAttemptByNode.TryGetValue(nodeId, out var latestAttempt)
            ? latestAttempt.StageAttemptId
            : BuildStageAttemptId(runId, nodeId, 1);
    }

    private static string NormalizeLogicalArtifactFileName(string fileName)
    {
        if (TryGetArchivedStageAttemptBaseName(fileName, out var archivedBaseName))
            fileName = archivedBaseName;

        if (fileName.StartsWith("status.previous.", StringComparison.OrdinalIgnoreCase))
            return "status.json";

        var match = ContractAttemptSuffixPattern.Match(fileName);
        if (!match.Success)
            return fileName;

        var extension = match.Groups["ext"].Success ? match.Groups["ext"].Value : string.Empty;
        return $"{match.Groups["stem"].Value}{extension}";
    }

    private static bool TryGetArchivedStageAttemptNumber(string fileName, out int attemptNumber)
    {
        attemptNumber = 0;
        var match = ArchivedStageAttemptPattern.Match(fileName);
        return match.Success &&
               int.TryParse(match.Groups["attempt"].Value, out attemptNumber) &&
               attemptNumber > 0;
    }

    private static bool TryGetArchivedStageAttemptBaseName(string fileName, out string baseName)
    {
        baseName = string.Empty;
        var match = ArchivedStageAttemptPattern.Match(fileName);
        if (!match.Success)
            return false;

        var extension = match.Groups["ext"].Success ? match.Groups["ext"].Value : string.Empty;
        baseName = $"{match.Groups["stem"].Value}{extension}";
        return true;
    }

    private static bool TryParseTimestamp(string? raw, out DateTimeOffset timestamp)
    {
        if (DateTimeOffset.TryParse(raw, out timestamp))
            return true;

        timestamp = default;
        return false;
    }

    private static string NormalizePathForMatching(string value) =>
        value.Replace('\\', '/');

    private static string BuildArtifactLineageId(
        string artifactVersionId,
        string relationType,
        string relatedArtifactVersionId)
    {
        var token = ComputeSha256Text($"{artifactVersionId}|{relationType}|{relatedArtifactVersionId}");
        return $"artifact-lineage:{token[..Math.Min(20, token.Length)]}";
    }

    private static string GuessMediaType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".log" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private static async Task WriteJsonAsync<T>(string path, T payload, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, StoreJson), ct);
    }

    private async Task<RunManifest?> LoadPreferredManifestAsync(string manifestPath, CancellationToken ct)
    {
        var fileManifest = RunManifest.Load(manifestPath);
        if (string.IsNullOrWhiteSpace(fileManifest?.run_id))
            return fileManifest;

        try
        {
            var durableStore = new SqliteRunStateStore(WorkingDirectory);
            var snapshot = await durableStore.LoadAsync(fileManifest.run_id, ct);
            if (snapshot?.Manifest is not null)
                return snapshot.Manifest;
        }
        catch
        {
            // Mirrors should stay best-effort; fall back to the file manifest below.
        }

        return fileManifest;
    }

    private static async Task WriteJsonLinesAsync(string path, IEnumerable<Dictionary<string, object?>> lines, CancellationToken ct)
    {
        var serialized = string.Join(
            Environment.NewLine,
            lines.Select(line => JsonSerializer.Serialize(line, StoreJsonLines)));

        await File.WriteAllTextAsync(
            path,
            serialized.Length == 0 ? string.Empty : serialized + Environment.NewLine,
            ct);
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReadJsonLines(string path)
    {
        var results = new List<Dictionary<string, object?>>();
        if (!File.Exists(path))
            return results;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                results.Add(ConvertObject(document.RootElement));
            }
            catch
            {
                // Ignore malformed telemetry lines during projection.
            }
        }

        return results;
    }

    private static Dictionary<string, object?> ReadJsonObject(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return ConvertObject(document.RootElement);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            dict[property.Name] = ConvertValue(property.Value);

        return dict;
    }

    private static object? ConvertValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(value),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToList(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var dbl) => dbl,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static string BuildStageAttemptId(string runId, string nodeId, int attemptNumber)
    {
        return $"{runId}:{nodeId}:attempt:{attemptNumber}";
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256Text(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, object?>? GetObject(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var raw) || raw is not Dictionary<string, object?> nested)
            return null;

        return nested;
    }

    private static List<Dictionary<string, object?>> GetObjectList(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var raw) || raw is not IEnumerable<object?> items)
            return [];

        return items
            .OfType<Dictionary<string, object?>>()
            .ToList();
    }

    private static string? GetString(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => raw.ToString()
        };
    }

    private static bool TryGetNestedString(
        IReadOnlyDictionary<string, object?> data,
        string objectKey,
        string nestedKey,
        out string? value)
    {
        value = null;
        if (GetObject(data, objectKey) is not { } nested)
            return false;

        value = GetString(nested, nestedKey);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static List<string> GetStringList(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var raw) || raw is null)
            return [];

        return raw switch
        {
            IEnumerable<string> strings => strings.Where(item => !string.IsNullOrWhiteSpace(item)).ToList(),
            IEnumerable<object?> objects => objects
                .Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList(),
            _ => []
        };
    }

    private static string? GetFirstListString(IReadOnlyDictionary<string, object?> data, string key)
    {
        return GetStringList(data, key).FirstOrDefault();
    }

    private static long? GetInt(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            int number => number,
            long number => number,
            double number => (long)number,
            float number => (long)number,
            decimal number => (long)number,
            string text when long.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? GetBoolean(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private sealed record StageAttemptSnapshot(
        string StageAttemptId,
        string RunId,
        string NodeId,
        int AttemptNumber,
        bool IsCurrent,
        string Status,
        string? Notes,
        string? FailureKind,
        string? Provider,
        string? Model,
        string? ProviderState,
        string? ContractState,
        string? EditState,
        string? VerificationState,
        string? AuthoritativeValidationState,
        bool? AdvanceAllowed,
        string? StartedAt,
        string? EndedAt,
        long? DurationMs,
        string StatusPath,
        Dictionary<string, object?>? Telemetry);

    private sealed record GateSnapshot(
        string GateId,
        string RunId,
        string? NodeId,
        string Question,
        string QuestionType,
        IReadOnlyList<string> Options,
        string? DefaultChoice,
        string? CreatedAt,
        bool IsPending,
        string Status,
        string? AnswerText,
        IReadOnlyList<string> SelectedOptions,
        string? AnsweredAt,
        string? Actor,
        string? Rationale,
        string? Source,
        string QuestionPath,
        string? AnswerPath);

    private sealed record GateAnswerSnapshot(
        string GateAnswerId,
        string RunId,
        string GateId,
        string? NodeId,
        string Status,
        string? Text,
        IReadOnlyList<string> SelectedOptions,
        string? Actor,
        string? Rationale,
        string? Source,
        string? AnsweredAt,
        string? AnswerPath);

    private sealed record ArtifactSnapshot(
        string ArtifactId,
        string RunId,
        string NodeId,
        string LogicalPath,
        string CurrentVersionId,
        int VersionCount,
        string ApprovalState,
        string? Actor,
        string? Rationale,
        string? Source,
        string? ApprovedAt);

    private sealed record ArtifactProjectionSnapshot(
        IReadOnlyList<ArtifactVersionSnapshot> Versions,
        IReadOnlyList<ArtifactLineageSnapshot> Lineage);

    private sealed record ArtifactFileCandidate(
        string StageAttemptId,
        string NodeId,
        string RelativePath,
        string LogicalPath,
        DateTimeOffset ProducedAtUtc,
        long SizeBytes,
        string Sha256,
        string MediaType);

    private sealed record ArtifactVersionSnapshot(
        string ArtifactVersionId,
        string ArtifactId,
        string RunId,
        string NodeId,
        string? StageAttemptId,
        string RelativePath,
        string LogicalPath,
        string ProducedAt,
        long SizeBytes,
        string Sha256,
        string MediaType,
        string ApprovalState,
        bool IsDefault,
        string? Actor,
        string? Rationale,
        string? Source,
        string? ApprovedAt,
        string? ProducerProvider,
        string? ProducerModel,
        string? PromptPath,
        string? PromptSha256,
        IReadOnlyList<string> InputArtifactVersionIds,
        string? SupersedesArtifactVersionId);

    private sealed record ArtifactLineageSnapshot(
        string ArtifactLineageId,
        string RunId,
        string ArtifactId,
        string ArtifactVersionId,
        string RelatedArtifactId,
        string RelatedArtifactVersionId,
        string StageAttemptId,
        string RelationType,
        string LogicalPath,
        string RelatedLogicalPath,
        string? SourcePath,
        string CreatedAt);

    private sealed record AgentSessionSnapshot(
        string AgentSessionId,
        string RunId,
        string StageAttemptId,
        string NodeId,
        string? ParentAgentSessionId,
        string Role,
        string? Provider,
        string? Model,
        string LifecycleState,
        long? AssistantTurns,
        long? ToolCalls,
        long? ToolErrors,
        long? DurationMs,
        Dictionary<string, object?>? Metadata);

    private sealed record ProviderInvocationSnapshot(
        string ProviderInvocationId,
        string RunId,
        string StageAttemptId,
        string NodeId,
        string? Provider,
        string? Model,
        string StageStatus,
        string? ProviderState,
        string? FailureKind,
        long? ProviderStatusCode,
        bool? ProviderRetryable,
        long? ProviderTimeoutMs,
        long? DurationMs,
        Dictionary<string, object?>? TokenUsage,
        string? VerificationState,
        string? ErrorMessage);

    private sealed record ModelScorecardSnapshot(
        string ModelScorecardId,
        string RunId,
        string Provider,
        string Model,
        int InvocationCount,
        int SuccessCount,
        int FailureCount,
        decimal SuccessRate,
        long? AvgDurationMs,
        long? P95DurationMs,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        decimal? EstimatedInputCostUsd,
        decimal? EstimatedOutputCostUsd,
        decimal? EstimatedKnownCostUsd,
        decimal? EstimatedTotalCostUsd,
        string PricingCoverage,
        IReadOnlyDictionary<string, int> FailureKinds,
        IReadOnlyDictionary<string, int> ProviderStates,
        IReadOnlyDictionary<string, int> VerificationStates);

    private sealed record LeaseSnapshot(
        string LeaseId,
        string RunId,
        long? OwnerPid,
        string? AcquiredAt,
        string? ReleasedAt,
        string State);

    private sealed record RunSnapshot(
        string RunId,
        long StateVersion,
        string? WorkingDirectory,
        string? OutputDirectory,
        string? GraphPath,
        string Status,
        string? ActiveStage,
        string? StartedAt,
        string? UpdatedAt,
        string? CheckpointPath,
        string? ResultPath,
        string? Crash,
        string? CancelRequestedAt,
        string? CancelRequestedActor,
        string? CancelRequestedRationale,
        string? CancelRequestedSource,
        string? AutoResumePolicy,
        string? ResumeSource,
        int RespawnCount,
        int EventCount,
        int StageAttemptCount,
        int GateCount,
        int ArtifactVersionCount,
        string StoreBackend);

    private sealed record ReplaySnapshot(
        string RunId,
        string GeneratedAt,
        IReadOnlyList<ReplayItemSnapshot> Events);

    private sealed record ReplayItemSnapshot(
        int Sequence,
        string TimestampUtc,
        string EventType,
        string? NodeId,
        string? StageAttemptId,
        string? GateId,
        string? LeaseId,
        string Summary,
        IReadOnlyDictionary<string, object?> Data);
}

internal static class DictionaryExtensions
{
    public static TResult? Let<TSource, TResult>(this TSource? source, Func<TSource, TResult?> selector)
        where TSource : class
    {
        return source is null ? default : selector(source);
    }
}
