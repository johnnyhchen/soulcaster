using System.Diagnostics;
using System.Text.Json;
using Soulcaster.Attractor;
using Soulcaster.Attractor.Execution;
using Soulcaster.Runner.Storage;

namespace Soulcaster.Runner;

internal sealed record ControlMutationResult(
    string Status,
    string Message,
    long RunVersion,
    int? SpawnedPid = null);

internal sealed record ArtifactMutationResult(
    string ArtifactId,
    string ArtifactVersionId,
    string Message);

internal static class WorkflowControlPlane
{
    public static async Task<ControlMutationResult> CancelRunAsync(
        string pipelineDir,
        string? actor,
        string? rationale,
        string? source,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        EnsureRationale(rationale, "cancel");

        var (runStateStore, manifestPath, manifest) = await LoadRunManifestAsync(pipelineDir, ct);

        var logsDir = Path.Combine(pipelineDir, "logs");
        Directory.CreateDirectory(logsDir);
        var controlDir = Path.Combine(logsDir, "control");
        Directory.CreateDirectory(controlDir);
        var payload = BuildAuditPayload(actor, rationale, source);

        var isActive = await RunLeaseCoordinator.IsActiveAsync(pipelineDir, manifest.run_id, ProgramSupport.IsProcessAlive, ct);
        var timestampUtc = payload["timestamp_utc"]?.ToString() ?? DateTimeOffset.UtcNow.ToString("o");
        var actorValue = payload["actor"]?.ToString();
        var rationaleValue = payload["rationale"]?.ToString();
        var sourceValue = payload["source"]?.ToString();
        DurableRunStateSnapshot snapshot;

        snapshot = await runStateStore.MutateAsync(
            manifestPath,
            manifest.run_id,
            expectedVersion,
            next =>
            {
                next.updated_at = timestampUtc;
                next.cancel_requested_at = timestampUtc;
                next.cancel_requested_actor = actorValue;
                next.cancel_requested_rationale = rationaleValue;
                next.cancel_requested_source = sourceValue;
                if (!isActive)
                {
                    next.status = "cancelled";
                    next.crash = "Cancelled by operator.";
                }

                return true;
            },
            ct);

        await File.WriteAllTextAsync(Path.Combine(controlDir, "cancel.json"), JsonSerializer.Serialize(payload, RunnerJson.Options), ct);
        await WorkflowEventLog.AppendAsync(
            logsDir,
            "run_cancel_requested",
            data: new Dictionary<string, object?>(payload, StringComparer.Ordinal)
            {
                ["run_id"] = snapshot.Manifest.run_id,
                ["active_stage"] = snapshot.Manifest.active_stage,
                ["state_version"] = snapshot.Version
            },
            ct: ct);

        if (!isActive)
        {
            var resultPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = "cancelled",
                ["cancelled_at"] = timestampUtc,
                ["actor"] = payload["actor"],
                ["rationale"] = payload["rationale"],
                ["source"] = payload["source"]
            };
            await File.WriteAllTextAsync(Path.Combine(logsDir, "result.json"), JsonSerializer.Serialize(resultPayload, RunnerJson.Options), ct);

            await WorkflowEventLog.AppendAsync(
                logsDir,
                "run_cancelled",
                data: new Dictionary<string, object?>(payload, StringComparer.Ordinal)
                {
                    ["run_id"] = snapshot.Manifest.run_id,
                    ["state_version"] = snapshot.Version
                },
                ct: ct);
        }

        await RecordOperatorMutationAsync(
            pipelineDir,
            new OperatorMutationRecord(
                RunId: snapshot.Manifest.run_id,
                MutationType: "cancel_run",
                MutationStatus: isActive ? "requested" : "completed",
                NodeId: snapshot.Manifest.active_stage,
                TargetNodeId: null,
                Actor: actorValue,
                Rationale: rationaleValue,
                Source: sourceValue,
                Message: isActive
                    ? "Cancellation marker written. Active run will stop at the next control checkpoint."
                    : "Run cancelled.",
                RunVersion: snapshot.Version,
                CreatedAtUtc: timestampUtc),
            ct);

        var workflowStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
        await workflowStore.SyncAsync(ct);

        return new ControlMutationResult(
            Status: isActive ? "cancellation_requested" : "cancelled",
            Message: isActive
                ? "Cancellation marker written. Active run will stop at the next control checkpoint."
                : "Run cancelled.",
            RunVersion: snapshot.Version);
    }

    public static async Task<ControlMutationResult> RetryStageAsync(
        string pipelineDir,
        string? nodeId,
        string? actor,
        string? rationale,
        string? source,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        EnsureRationale(rationale, "retry");
        var (runStateStore, manifestPath, manifest) = await LoadRunManifestAsync(pipelineDir, ct);
        await EnsureInactiveAsync(pipelineDir, manifest, ct);

        var logsDir = Path.Combine(pipelineDir, "logs");
        var checkpoint = Checkpoint.Load(logsDir)
            ?? throw new InvalidOperationException("Cannot retry a stage without a checkpoint.");

        var targetNodeId = string.IsNullOrWhiteSpace(nodeId)
            ? checkpoint.CurrentNodeId
            : nodeId.Trim();
        if (string.IsNullOrWhiteSpace(targetNodeId))
            throw new InvalidOperationException("Unable to resolve a target node for retry.");

        var graph = TryLoadGraph(manifest.graph_path);
        if (graph is not null && !graph.Nodes.ContainsKey(targetNodeId))
            throw new InvalidOperationException($"Retry target node '{targetNodeId}' does not exist in the workflow graph.");

        var policy = WorkflowPolicySupport.LoadForRun(pipelineDir, manifest.graph_path);
        var retryBudgetUsage = WorkflowPolicySupport.ReadRetryBudgetUsage(logsDir, targetNodeId);
        if (!WorkflowPolicySupport.TryValidateOperatorRetry(policy, targetNodeId, retryBudgetUsage, out var retryDeniedReason))
        {
            await AuditDeniedMutationAsync(
                logsDir,
                "operator_retry_stage_denied",
                manifest.run_id,
                targetNodeId,
                actor,
                rationale,
                source,
                retryDeniedReason!,
                ct);

            var deniedStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
            await deniedStore.SyncAsync(ct);
            await RecordOperatorMutationAsync(
                pipelineDir,
                new OperatorMutationRecord(
                    RunId: manifest.run_id,
                    MutationType: "retry_stage",
                    MutationStatus: "denied",
                    NodeId: targetNodeId,
                    TargetNodeId: targetNodeId,
                    Actor: actor,
                    Rationale: rationale,
                    Source: source,
                    Message: retryDeniedReason,
                    RunVersion: expectedVersion),
                ct);
            throw new InvalidOperationException(retryDeniedReason);
        }

        var trimmedCompletedNodes = new List<string>(checkpoint.CompletedNodes);
        var lastIndex = trimmedCompletedNodes.FindLastIndex(node =>
            string.Equals(node, targetNodeId, StringComparison.Ordinal));
        if (lastIndex >= 0)
            trimmedCompletedNodes = trimmedCompletedNodes.Take(lastIndex).ToList();

        var retryCounts = new Dictionary<string, int>(checkpoint.RetryCounts);
        retryCounts.Remove(targetNodeId);

        var updatedCheckpoint = checkpoint with
        {
            CurrentNodeId = targetNodeId,
            CompletedNodes = trimmedCompletedNodes,
            RetryCounts = retryCounts
        };
        var timestampUtc = DateTimeOffset.UtcNow.ToString("o");
        var snapshot = await runStateStore.MutateAsync(
            manifestPath,
            manifest.run_id,
            expectedVersion,
            next =>
            {
                next.status = "pending_resume";
                next.active_stage = targetNodeId;
                next.updated_at = timestampUtc;
                next.crash = null;
                ClearCancellationRequest(next);
                return true;
            },
            ct);

        updatedCheckpoint.Save(logsDir);

        var payload = BuildAuditPayload(actor, rationale, source);
        await WorkflowEventLog.AppendAsync(
            logsDir,
            "operator_retry_stage_requested",
            nodeId: targetNodeId,
            data: new Dictionary<string, object?>(payload, StringComparer.Ordinal)
            {
                ["run_id"] = snapshot.Manifest.run_id,
                ["state_version"] = snapshot.Version
            },
            ct: ct);

        await RecordOperatorMutationAsync(
            pipelineDir,
            new OperatorMutationRecord(
                RunId: snapshot.Manifest.run_id,
                MutationType: "retry_stage",
                MutationStatus: "ready",
                NodeId: targetNodeId,
                TargetNodeId: targetNodeId,
                Actor: payload["actor"]?.ToString(),
                Rationale: payload["rationale"]?.ToString(),
                Source: payload["source"]?.ToString(),
                Message: $"Stage '{targetNodeId}' is staged for retry.",
                RunVersion: snapshot.Version,
                CreatedAtUtc: timestampUtc),
            ct);

        var workflowStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
        await workflowStore.SyncAsync(ct);

        return new ControlMutationResult(
            "retry_ready",
            $"Stage '{targetNodeId}' is staged for retry.",
            snapshot.Version);
    }

    public static async Task<ControlMutationResult> ForceAdvanceAsync(
        string pipelineDir,
        string targetNodeId,
        string? actor,
        string? rationale,
        string? source,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        EnsureRationale(rationale, "force-advance");
        if (string.IsNullOrWhiteSpace(targetNodeId))
            throw new InvalidOperationException("force-advance requires a target node.");

        var (runStateStore, manifestPath, manifest) = await LoadRunManifestAsync(pipelineDir, ct);
        await EnsureInactiveAsync(pipelineDir, manifest, ct);

        var logsDir = Path.Combine(pipelineDir, "logs");
        var checkpoint = Checkpoint.Load(logsDir)
            ?? throw new InvalidOperationException("Cannot force-advance a run without a checkpoint.");

        if (string.Equals(manifest.status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot force-advance a completed run.");

        var graph = TryLoadGraph(manifest.graph_path);
        if (graph is not null)
        {
            if (!graph.Nodes.TryGetValue(targetNodeId.Trim(), out var targetNode))
                throw new InvalidOperationException($"Force-advance target node '{targetNodeId.Trim()}' does not exist in the workflow graph.");

            if (string.Equals(targetNode.Shape, "Mdiamond", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot force-advance to the start node.");
        }

        var policy = WorkflowPolicySupport.LoadForRun(pipelineDir, manifest.graph_path);
        if (!WorkflowPolicySupport.TryValidateForceAdvanceTarget(policy, targetNodeId.Trim(), out var forceAdvanceDeniedReason))
        {
            await AuditDeniedMutationAsync(
                logsDir,
                "operator_force_advance_denied",
                manifest.run_id,
                targetNodeId.Trim(),
                actor,
                rationale,
                source,
                forceAdvanceDeniedReason!,
                ct);

            var deniedStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
            await deniedStore.SyncAsync(ct);
            await RecordOperatorMutationAsync(
                pipelineDir,
                new OperatorMutationRecord(
                    RunId: manifest.run_id,
                    MutationType: "force_advance",
                    MutationStatus: "denied",
                    NodeId: targetNodeId.Trim(),
                    TargetNodeId: targetNodeId.Trim(),
                    Actor: actor,
                    Rationale: rationale,
                    Source: source,
                    Message: forceAdvanceDeniedReason,
                    RunVersion: expectedVersion),
                ct);
            throw new InvalidOperationException(forceAdvanceDeniedReason);
        }

        var previousNode = checkpoint.CurrentNodeId;
        var updatedCheckpoint = checkpoint with { CurrentNodeId = targetNodeId.Trim() };
        var timestampUtc = DateTimeOffset.UtcNow.ToString("o");
        var snapshot = await runStateStore.MutateAsync(
            manifestPath,
            manifest.run_id,
            expectedVersion,
            next =>
            {
                next.status = "pending_resume";
                next.active_stage = targetNodeId.Trim();
                next.updated_at = timestampUtc;
                next.crash = null;
                ClearCancellationRequest(next);
                return true;
            },
            ct);

        updatedCheckpoint.Save(logsDir);

        var payload = BuildAuditPayload(actor, rationale, source);
        await WorkflowEventLog.AppendAsync(
            logsDir,
            "operator_force_advanced",
            nodeId: previousNode,
            data: new Dictionary<string, object?>(payload, StringComparer.Ordinal)
            {
                ["run_id"] = snapshot.Manifest.run_id,
                ["previous_node"] = previousNode,
                ["target_node"] = targetNodeId.Trim(),
                ["state_version"] = snapshot.Version
            },
            ct: ct);

        await RecordOperatorMutationAsync(
            pipelineDir,
            new OperatorMutationRecord(
                RunId: snapshot.Manifest.run_id,
                MutationType: "force_advance",
                MutationStatus: "ready",
                NodeId: previousNode,
                TargetNodeId: targetNodeId.Trim(),
                Actor: payload["actor"]?.ToString(),
                Rationale: payload["rationale"]?.ToString(),
                Source: payload["source"]?.ToString(),
                Message: $"Run will resume at '{targetNodeId.Trim()}' instead of '{previousNode}'.",
                RunVersion: snapshot.Version,
                CreatedAtUtc: timestampUtc),
            ct);

        var workflowStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
        await workflowStore.SyncAsync(ct);

        return new ControlMutationResult(
            "force_advance_ready",
            $"Run will resume at '{targetNodeId.Trim()}' instead of '{previousNode}'.",
            snapshot.Version);
    }

    public static async Task<ControlMutationResult> ResumeRunAsync(
        string pipelineDir,
        string? actor,
        string? rationale,
        string? source,
        long? expectedVersion = null,
        CancellationToken ct = default)
    {
        EnsureRationale(rationale, "resume");

        var (runStateStore, manifestPath, manifest) = await LoadRunManifestAsync(pipelineDir, ct);
        await EnsureInactiveAsync(pipelineDir, manifest, ct);

        if (string.IsNullOrWhiteSpace(manifest.graph_path) || !File.Exists(manifest.graph_path))
            throw new InvalidOperationException("Run manifest does not point to a valid graph_path for resume.");

        var logsDir = Path.Combine(pipelineDir, "logs");
        var controlDir = Path.Combine(logsDir, "control");
        var cancelPath = Path.Combine(controlDir, "cancel.json");
        var timestampUtc = DateTimeOffset.UtcNow.ToString("o");
        var snapshot = await runStateStore.MutateAsync(
            manifestPath,
            manifest.run_id,
            expectedVersion,
            next =>
            {
                next.status = "resume_requested";
                next.updated_at = timestampUtc;
                next.crash = null;
                ClearCancellationRequest(next);
                return true;
            },
            ct);

        if (File.Exists(cancelPath))
            File.Delete(cancelPath);

        var payload = BuildAuditPayload(actor, rationale, source);
        var process = TrySpawnResumeProcess(snapshot.Manifest, pipelineDir);
        await WorkflowEventLog.AppendAsync(
            logsDir,
            "run_resume_requested",
            data: new Dictionary<string, object?>(payload, StringComparer.Ordinal)
            {
                ["run_id"] = snapshot.Manifest.run_id,
                ["graph_path"] = snapshot.Manifest.graph_path,
                ["state_version"] = snapshot.Version,
                ["spawned_pid"] = process.Id
            },
            ct: ct);

        await RecordOperatorMutationAsync(
            pipelineDir,
            new OperatorMutationRecord(
                RunId: snapshot.Manifest.run_id,
                MutationType: "resume_run",
                MutationStatus: "spawned",
                NodeId: snapshot.Manifest.active_stage,
                TargetNodeId: snapshot.Manifest.active_stage,
                Actor: payload["actor"]?.ToString(),
                Rationale: payload["rationale"]?.ToString(),
                Source: payload["source"]?.ToString(),
                Message: $"Spawned resume runner pid {process.Id}.",
                RunVersion: snapshot.Version,
                CreatedAtUtc: timestampUtc),
            ct);

        var workflowStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
        await workflowStore.SyncAsync(ct);

        return new ControlMutationResult(
            "resume_spawned",
            $"Spawned resume runner pid {process.Id}.",
            snapshot.Version,
            SpawnedPid: process.Id);
    }

    public static async Task<ArtifactMutationResult> PromoteArtifactAsync(
        string pipelineDir,
        string artifactSelector,
        string artifactVersionId,
        string? actor,
        string? rationale,
        string? source,
        CancellationToken ct = default)
    {
        EnsureRationale(rationale, "artifact promotion");
        var workflowStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
        await workflowStore.SyncAsync(ct);

        var (artifactId, _) = ResolveArtifactIdentity(pipelineDir, artifactSelector, artifactVersionId);
        var selection = await ArtifactRegistryStateStore.PromoteAsync(
            Path.Combine(pipelineDir, "store"),
            artifactId,
            artifactVersionId,
            action: "promote",
            actor: actor,
            rationale: rationale,
            source: source,
            ct: ct);

        await WorkflowEventLog.AppendAsync(
            Path.Combine(pipelineDir, "logs"),
            "artifact_promoted",
            data: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["artifact_id"] = artifactId,
                ["artifact_version_id"] = artifactVersionId,
                ["actor"] = selection.Actor,
                ["rationale"] = selection.Rationale,
                ["source"] = selection.Source,
                ["approved_at"] = selection.UpdatedAtUtc
            },
            ct: ct);

        await workflowStore.SyncAsync(ct);
        return new ArtifactMutationResult(artifactId, artifactVersionId, $"Promoted artifact version '{artifactVersionId}'.");
    }

    public static async Task<ArtifactMutationResult> RollbackArtifactAsync(
        string pipelineDir,
        string artifactSelector,
        string? targetVersionId,
        string? actor,
        string? rationale,
        string? source,
        CancellationToken ct = default)
    {
        EnsureRationale(rationale, "artifact rollback");
        var workflowStore = WorkflowStoreFactory.CreateDefault(pipelineDir);
        await workflowStore.SyncAsync(ct);

        var (artifactId, currentVersionId) = ResolveArtifactIdentity(pipelineDir, artifactSelector, targetVersionId);
        var resolvedTargetVersion = targetVersionId;
        if (string.IsNullOrWhiteSpace(resolvedTargetVersion))
        {
            var versions = ReadArtifactVersions(pipelineDir)
                .Where(version => string.Equals(GetString(version, "artifact_id"), artifactId, StringComparison.Ordinal))
                .OrderByDescending(version => GetString(version, "produced_at"), StringComparer.Ordinal)
                .ToList();

            resolvedTargetVersion = versions
                .Select(version => GetString(version, "artifact_version_id"))
                .FirstOrDefault(versionId =>
                    !string.IsNullOrWhiteSpace(versionId) &&
                    !string.Equals(versionId, currentVersionId, StringComparison.Ordinal));
        }

        if (string.IsNullOrWhiteSpace(resolvedTargetVersion))
            throw new InvalidOperationException($"No rollback candidate found for artifact '{artifactId}'.");

        var selection = await ArtifactRegistryStateStore.PromoteAsync(
            Path.Combine(pipelineDir, "store"),
            artifactId,
            resolvedTargetVersion,
            action: "rollback",
            actor: actor,
            rationale: rationale,
            source: source,
            ct: ct);

        await WorkflowEventLog.AppendAsync(
            Path.Combine(pipelineDir, "logs"),
            "artifact_rolled_back",
            data: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["artifact_id"] = artifactId,
                ["artifact_version_id"] = resolvedTargetVersion,
                ["actor"] = selection.Actor,
                ["rationale"] = selection.Rationale,
                ["source"] = selection.Source,
                ["approved_at"] = selection.UpdatedAtUtc
            },
            ct: ct);

        await workflowStore.SyncAsync(ct);
        return new ArtifactMutationResult(artifactId, resolvedTargetVersion, $"Rolled back artifact '{artifactId}' to '{resolvedTargetVersion}'.");
    }

    public static IReadOnlyList<Dictionary<string, object?>> ReadArtifacts(string pipelineDir)
    {
        var path = Path.Combine(pipelineDir, "store", "artifacts.json");
        return ReadJsonArray(path);
    }

    public static IReadOnlyList<Dictionary<string, object?>> ReadArtifactVersions(string pipelineDir)
    {
        var path = Path.Combine(pipelineDir, "store", "artifact_versions.json");
        return ReadJsonArray(path);
    }

    public static IReadOnlyList<Dictionary<string, object?>> ReadArtifactLineage(string pipelineDir)
    {
        var path = Path.Combine(pipelineDir, "store", "artifact_lineage.json");
        return ReadJsonArray(path);
    }

    public static IReadOnlyList<Dictionary<string, object?>> ReadModelScorecards(string pipelineDir)
    {
        var path = Path.Combine(pipelineDir, "store", "model_scorecards.json");
        return ReadJsonArray(path);
    }

    private static (string ArtifactId, string? CurrentVersionId) ResolveArtifactIdentity(
        string pipelineDir,
        string artifactSelector,
        string? artifactVersionId)
    {
        if (string.IsNullOrWhiteSpace(artifactSelector))
            throw new InvalidOperationException("An artifact identifier or logical path is required.");

        var artifacts = ReadArtifacts(pipelineDir);
        var artifact = artifacts.FirstOrDefault(item =>
            string.Equals(GetString(item, "artifact_id"), artifactSelector, StringComparison.Ordinal) ||
            string.Equals(GetString(item, "logical_path"), artifactSelector, StringComparison.OrdinalIgnoreCase));

        if (artifact is null)
            throw new InvalidOperationException($"Artifact '{artifactSelector}' was not found.");

        var artifactId = GetString(artifact, "artifact_id")
            ?? throw new InvalidOperationException($"Artifact '{artifactSelector}' is missing an artifact_id.");
        var currentVersionId = GetString(artifact, "current_version_id");

        if (!string.IsNullOrWhiteSpace(artifactVersionId))
        {
            var versionExists = ReadArtifactVersions(pipelineDir).Any(item =>
                string.Equals(GetString(item, "artifact_id"), artifactId, StringComparison.Ordinal) &&
                string.Equals(GetString(item, "artifact_version_id"), artifactVersionId, StringComparison.Ordinal));
            if (!versionExists)
                throw new InvalidOperationException($"Artifact version '{artifactVersionId}' does not belong to '{artifactId}'.");
        }

        return (artifactId, currentVersionId);
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReadJsonArray(string path)
    {
        if (!File.Exists(path))
            return [];

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<Dictionary<string, object?>>();
        foreach (var element in document.RootElement.EnumerateArray())
            items.Add(ConvertObject(element));

        return items;
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
            JsonValueKind.Number when value.TryGetInt64(out var i64) => i64,
            JsonValueKind.Number when value.TryGetDouble(out var dbl) => dbl,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static void EnsureRationale(string? rationale, string operation)
    {
        if (string.IsNullOrWhiteSpace(rationale))
            throw new InvalidOperationException($"{operation} requires a non-empty rationale.");
    }

    private static Dictionary<string, object?> BuildAuditPayload(string? actor, string? rationale, string? source)
    {
        var timestampUtc = DateTimeOffset.UtcNow.ToString("o");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actor"] = string.IsNullOrWhiteSpace(actor) ? Environment.UserName : actor.Trim(),
            ["rationale"] = rationale?.Trim(),
            ["source"] = string.IsNullOrWhiteSpace(source) ? "control-plane" : source.Trim(),
            ["timestamp_utc"] = timestampUtc
        };
    }

    private static async Task EnsureInactiveAsync(string pipelineDir, RunManifest manifest, CancellationToken ct)
    {
        if (await RunLeaseCoordinator.IsActiveAsync(pipelineDir, manifest.run_id, ProgramSupport.IsProcessAlive, ct))
            throw new InvalidOperationException($"Run '{manifest.run_id}' is still active.");
    }

    private static Graph? TryLoadGraph(string? graphPath)
    {
        if (string.IsNullOrWhiteSpace(graphPath) || !File.Exists(graphPath))
            return null;

        try
        {
            return DotParser.Parse(File.ReadAllText(graphPath));
        }
        catch
        {
            return null;
        }
    }

    private static async Task AuditDeniedMutationAsync(
        string logsDir,
        string eventType,
        string runId,
        string? nodeId,
        string? actor,
        string? rationale,
        string? source,
        string denialReason,
        CancellationToken ct)
    {
        var payload = BuildAuditPayload(actor, rationale, source);
        payload["run_id"] = runId;
        payload["denial_reason"] = denialReason;
        await WorkflowEventLog.AppendAsync(logsDir, eventType, nodeId, payload, ct);
    }

    private static async Task RecordOperatorMutationAsync(
        string pipelineDir,
        OperatorMutationRecord mutation,
        CancellationToken ct)
    {
        try
        {
            await OperatorMutationStore.RecordAsync(pipelineDir, mutation, ct);
        }
        catch
        {
            // Best effort. Audit mirroring must not block the control plane.
        }
    }

    private static async Task<(SqliteRunStateStore Store, string ManifestPath, RunManifest Manifest)> LoadRunManifestAsync(
        string pipelineDir,
        CancellationToken ct)
    {
        var manifestPath = Path.Combine(pipelineDir, "run-manifest.json");
        var manifest = RunManifest.Load(manifestPath)
            ?? throw new InvalidOperationException($"Run manifest not found under '{pipelineDir}'.");
        var store = new SqliteRunStateStore(pipelineDir);
        var snapshot = await store.EnsureInitializedAsync(manifestPath, manifest, ct);
        return (store, manifestPath, snapshot.Manifest);
    }

    private static void ClearCancellationRequest(RunManifest manifest)
    {
        manifest.cancel_requested_at = null;
        manifest.cancel_requested_actor = null;
        manifest.cancel_requested_rationale = null;
        manifest.cancel_requested_source = null;
    }

    private static Process TrySpawnResumeProcess(RunManifest manifest, string pipelineDir)
    {
        var runnerProjectPath = ResolveRunnerProjectPath();
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runnerProjectPath)!
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(runnerProjectPath);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add(manifest.graph_path);
        psi.ArgumentList.Add("--resume-from");
        psi.ArgumentList.Add(pipelineDir);
        psi.ArgumentList.Add("--resume");
        psi.ArgumentList.Add("--no-autoresume");
        if (!string.IsNullOrWhiteSpace(manifest.backend_mode))
        {
            psi.ArgumentList.Add("--backend");
            psi.ArgumentList.Add(manifest.backend_mode);
        }

        if (!string.IsNullOrWhiteSpace(manifest.backend_script_path))
        {
            psi.ArgumentList.Add("--backend-script");
            psi.ArgumentList.Add(manifest.backend_script_path);
        }

        return Process.Start(psi)
               ?? throw new InvalidOperationException("Failed to spawn the resume runner process.");
    }

    private static string ResolveRunnerProjectPath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "runner", "Soulcaster.Runner.csproj"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "runner", "Soulcaster.Runner.csproj"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "runner", "Soulcaster.Runner.csproj")
        };

        foreach (var candidate in candidates.Select(Path.GetFullPath))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Unable to locate Soulcaster.Runner.csproj for resume.");
    }
}

internal static class ProgramSupport
{
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
