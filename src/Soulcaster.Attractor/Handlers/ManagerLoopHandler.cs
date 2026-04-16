namespace Soulcaster.Attractor.Handlers;

using System.Text;
using System.Text.Json;

/// <summary>
/// Handler for stack.manager_loop nodes (shape=house).
/// Supports the legacy fixed-cycle loop and a telemetry-driven supervision mode
/// when telemetry_source is provided.
/// </summary>
public class ManagerLoopHandler : INodeHandler
{
    private readonly ICodergenBackend? _backend;
    private readonly ISupervisorController? _supervisorController;

    public ManagerLoopHandler(ICodergenBackend? backend = null, ISupervisorController? supervisorController = null)
    {
        _backend = backend;
        _supervisorController = supervisorController;
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        int maxCycles = node.RawAttributes.TryGetValue("max_cycles", out var maxCyclesRaw) && int.TryParse(maxCyclesRaw, out var maxCyclesValue)
            ? maxCyclesValue
            : 10;
        var stopCondition = node.RawAttributes.GetValueOrDefault("stop_condition", "");
        var childDotfile = node.RawAttributes.GetValueOrDefault("child_dotfile", "");
        int steerCooldownMs = node.RawAttributes.TryGetValue("steer_cooldown", out var cooldownRaw) && int.TryParse(cooldownRaw, out var cooldownValue)
            ? cooldownValue
            : 5000;
        var telemetrySource = node.RawAttributes.GetValueOrDefault("telemetry_source", "");

        var stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);

        if (string.IsNullOrWhiteSpace(telemetrySource) && string.IsNullOrWhiteSpace(childDotfile))
            return await ExecuteLegacyLoopAsync(node, context, graph, logsRoot, stageDir, maxCycles, stopCondition, steerCooldownMs, ct);

        return await ExecuteTelemetryLoopAsync(
            node,
            context,
            graph,
            logsRoot,
            stageDir,
            maxCycles,
            stopCondition,
            childDotfile,
            telemetrySource,
            steerCooldownMs,
            ct);
    }

    private async Task<Outcome> ExecuteLegacyLoopAsync(
        GraphNode node,
        PipelineContext context,
        Graph graph,
        string logsRoot,
        string stageDir,
        int maxCycles,
        string stopCondition,
        int steerCooldownMs,
        CancellationToken ct)
    {
        var cycleLog = new List<Dictionary<string, object?>>();
        int currentCycle = 0;

        while (currentCycle < maxCycles)
        {
            ct.ThrowIfCancellationRequested();
            currentCycle++;

            if (_backend is not null && !string.IsNullOrEmpty(node.Prompt))
            {
                var cyclePrompt = $"[Cycle {currentCycle}/{maxCycles}]\n{node.Prompt}\n\nContext:\n";
                foreach (var (key, value) in context.All)
                    cyclePrompt += $"  {key} = {value}\n";

                var result = await _backend.RunAsync(cyclePrompt, ct: ct);
                await File.WriteAllTextAsync(Path.Combine(stageDir, $"cycle-{currentCycle}.md"), result.Response, ct);

                if (result.ContextUpdates is not null)
                    context.MergeUpdates(result.ContextUpdates);

                cycleLog.Add(new Dictionary<string, object?>
                {
                    ["cycle"] = currentCycle,
                    ["status"] = result.Status.ToString().ToLowerInvariant(),
                    ["response_length"] = result.Response.Length
                });

                if (StopConditionSatisfied(stopCondition, context))
                    break;

                if (result.Status == OutcomeStatus.Fail)
                    break;
            }
            else
            {
                cycleLog.Add(new Dictionary<string, object?>
                {
                    ["cycle"] = currentCycle,
                    ["status"] = "no_backend"
                });
                break;
            }

            if (currentCycle < maxCycles)
                await Task.Delay(steerCooldownMs, ct);
        }

        var reachedMax = currentCycle >= maxCycles;
        var status = reachedMax ? OutcomeStatus.PartialSuccess : OutcomeStatus.Success;

        await WriteStatusAsync(
            stageDir,
            node.Id,
            currentCycle,
            maxCycles,
            cycleLog,
            telemetrySource: null,
            escalated: false,
            finalStatus: status,
            ct: ct);

        return new Outcome(
            Status: status,
            ContextUpdates: new Dictionary<string, string>
            {
                [$"{node.Id}.cycles"] = currentCycle.ToString(),
                [$"{node.Id}.reached_max"] = reachedMax.ToString().ToLowerInvariant()
            },
            Notes: $"Manager loop '{node.Id}' completed {currentCycle}/{maxCycles} cycles.");
    }

    private async Task<Outcome> ExecuteTelemetryLoopAsync(
        GraphNode node,
        PipelineContext context,
        Graph graph,
        string logsRoot,
        string stageDir,
        int maxCycles,
        string stopCondition,
        string childDotfile,
        string telemetrySource,
        int steerCooldownMs,
        CancellationToken ct)
    {
        int pollIntervalMs = node.RawAttributes.TryGetValue("poll_interval_ms", out var pollRaw) && int.TryParse(pollRaw, out var pollValue)
            ? pollValue
            : 250;
        int stallThreshold = node.RawAttributes.TryGetValue("stall_threshold", out var stallRaw) && int.TryParse(stallRaw, out var stallValue)
            ? stallValue
            : 2;
        int escalationThreshold = node.RawAttributes.TryGetValue("escalation_threshold", out var escalationRaw) && int.TryParse(escalationRaw, out var escalationValue)
            ? escalationValue
            : Math.Max(stallThreshold + 1, 3);

        var cycleLog = new List<Dictionary<string, object?>>();
        SupervisorWorkerRuntime? workerRuntime = null;
        var telemetryPath = ResolveTelemetryPath(logsRoot, telemetrySource, childDotfile);
        var lastSnapshot = (SupervisorTelemetrySnapshot?)null;
        var stallStreak = 0;
        var currentCycle = 0;
        var steeringCount = 0;
        var sawProgress = false;
        var stopSatisfied = false;
        DateTimeOffset? lastSteerAt = null;
        var escalated = false;

        while (currentCycle < maxCycles)
        {
            ct.ThrowIfCancellationRequested();
            currentCycle++;

            if (_supervisorController is not null && !string.IsNullOrWhiteSpace(childDotfile))
            {
                workerRuntime ??= await _supervisorController.EnsureWorkerAsync(node, graph, logsRoot, context, ct);
                telemetryPath = workerRuntime.TelemetryPath;
                context.Set($"{node.Id}.worker_dotfile", workerRuntime.DotFilePath);
                context.Set($"{node.Id}.worker_run_dir", workerRuntime.WorkingDir);
                context.Set($"{node.Id}.worker_logs_dir", workerRuntime.LogsDir);
                context.Set($"{node.Id}.worker_steer_path", workerRuntime.SteerPath);
            }

            var snapshot = SupervisorTelemetry.Read(telemetryPath);
            var progressed = snapshot.HasProgressSince(lastSnapshot);
            sawProgress |= progressed;
            stallStreak = progressed ? 0 : stallStreak + 1;

            ApplyTelemetryContext(node.Id, context, snapshot, stallStreak, progressed, escalated);

            var cycleStatus = progressed ? "progressing" : "idle";
            var steeringApplied = false;

            if (!progressed && stallStreak >= stallThreshold && _backend is not null && !string.IsNullOrWhiteSpace(node.Prompt))
            {
                if (SteeringCooldownElapsed(lastSteerAt, steerCooldownMs))
                {
                    var cyclePrompt = BuildSupervisorPrompt(node, context, snapshot, currentCycle, maxCycles, stallStreak);
                    var result = await _backend.RunAsync(cyclePrompt, ct: ct);
                    await File.WriteAllTextAsync(Path.Combine(stageDir, $"cycle-{currentCycle}.md"), result.Response, ct);

                    if (result.ContextUpdates is not null)
                        context.MergeUpdates(result.ContextUpdates);

                    if (workerRuntime is not null)
                        await _supervisorController!.WriteSteeringAsync(
                            workerRuntime,
                            result.StageStatus?.Notes ?? result.Response,
                            ct);

                    steeringApplied = true;
                    steeringCount++;
                    lastSteerAt = DateTimeOffset.UtcNow;
                    cycleStatus = stallStreak >= escalationThreshold ? "escalated" : "warning";
                }
                else
                {
                    cycleStatus = "cooldown";
                }
            }

            if (!progressed && stallStreak >= escalationThreshold)
            {
                escalated = true;
                context.Set($"{node.Id}.escalated", "true");
                context.Set($"{node.Id}.stall_status", "escalated");
                cycleStatus = "escalated";

                if (workerRuntime is not null && _supervisorController is not null)
                    await _supervisorController.StopWorkerAsync(workerRuntime, ct);
            }

            cycleLog.Add(new Dictionary<string, object?>
            {
                ["cycle"] = currentCycle,
                ["status"] = cycleStatus,
                ["progressed"] = progressed,
                ["stall_streak"] = stallStreak,
                ["steering_applied"] = steeringApplied,
                ["progress_score"] = snapshot.ProgressScore,
                ["stage_end_count"] = snapshot.StageEndCount,
                ["tool_calls"] = snapshot.ToolCalls,
                ["last_completed_node"] = snapshot.LastCompletedNode,
                ["missing_source"] = snapshot.MissingSource
            });

            if (StopConditionSatisfied(stopCondition, context))
            {
                stopSatisfied = true;
                break;
            }

            if (escalated)
                break;

            if (currentCycle < maxCycles && pollIntervalMs > 0)
                await Task.Delay(pollIntervalMs, ct);

            lastSnapshot = snapshot;
        }

        var reachedMax = currentCycle >= maxCycles && !escalated;
        var finalStatus = escalated
            ? OutcomeStatus.Retry
            : stopSatisfied || sawProgress || (maxCycles == 1 && steeringCount > 0)
                ? OutcomeStatus.Success
                : reachedMax
                    ? OutcomeStatus.PartialSuccess
                    : OutcomeStatus.Success;

        await WriteStatusAsync(stageDir, node.Id, currentCycle, maxCycles, cycleLog, telemetryPath, escalated, finalStatus, ct);

        return new Outcome(
            Status: finalStatus,
            ContextUpdates: new Dictionary<string, string>
            {
                [$"{node.Id}.cycles"] = currentCycle.ToString(),
                [$"{node.Id}.reached_max"] = reachedMax.ToString().ToLowerInvariant(),
                [$"{node.Id}.stall_streak"] = stallStreak.ToString(),
                [$"{node.Id}.stall_status"] = escalated ? "escalated" : context.Get($"{node.Id}.stall_status"),
                [$"{node.Id}.steering_count"] = steeringCount.ToString(),
                [$"{node.Id}.escalated"] = escalated.ToString().ToLowerInvariant()
            },
            Notes: escalated
                ? $"Manager loop '{node.Id}' escalated after {stallStreak} stalled cycles."
                : $"Manager loop '{node.Id}' supervised telemetry for {currentCycle}/{maxCycles} cycles.");
    }

    private static string ResolveTelemetryPath(string logsRoot, string telemetrySource, string childDotfile)
    {
        if (!string.IsNullOrWhiteSpace(telemetrySource))
        {
            if (Path.IsPathRooted(telemetrySource))
                return Path.GetFullPath(telemetrySource);

            var outputRoot = Path.GetDirectoryName(logsRoot) ?? logsRoot;
            var outputRelative = Path.GetFullPath(Path.Combine(outputRoot, telemetrySource));
            if (File.Exists(outputRelative))
                return outputRelative;

            return Path.GetFullPath(Path.Combine(logsRoot, telemetrySource));
        }

        if (!string.IsNullOrWhiteSpace(childDotfile))
        {
            var childName = Path.GetFileNameWithoutExtension(childDotfile);
            if (!string.IsNullOrWhiteSpace(childName))
            {
                var projectRoot = Path.GetFullPath(Path.Combine(logsRoot, "..", "..", ".."));
                return Path.Combine(projectRoot, "dotfiles", "output", childName, "logs", "events.jsonl");
            }
        }

        return Path.Combine(logsRoot, "events.jsonl");
    }

    private static bool StopConditionSatisfied(string stopCondition, PipelineContext context)
    {
        if (string.IsNullOrWhiteSpace(stopCondition) || !stopCondition.Contains('='))
            return false;

        var parts = stopCondition.Split('=', 2);
        var conditionKey = parts[0].Replace("context.", "", StringComparison.Ordinal).Trim();
        var conditionValue = parts[1].Trim().Trim('"');
        return string.Equals(context.Get(conditionKey), conditionValue, StringComparison.Ordinal);
    }

    private static bool SteeringCooldownElapsed(DateTimeOffset? lastSteerAt, int steerCooldownMs)
    {
        if (lastSteerAt is null || steerCooldownMs <= 0)
            return true;

        return (DateTimeOffset.UtcNow - lastSteerAt.Value).TotalMilliseconds >= steerCooldownMs;
    }

    private static void ApplyTelemetryContext(
        string nodeId,
        PipelineContext context,
        SupervisorTelemetrySnapshot snapshot,
        int stallStreak,
        bool progressed,
        bool escalated)
    {
        context.Set($"{nodeId}.telemetry_source", snapshot.SourcePath);
        context.Set($"{nodeId}.stage_start_count", snapshot.StageStartCount.ToString());
        context.Set($"{nodeId}.stage_end_count", snapshot.StageEndCount.ToString());
        context.Set($"{nodeId}.success_count", snapshot.SuccessCount.ToString());
        context.Set($"{nodeId}.partial_success_count", snapshot.PartialSuccessCount.ToString());
        context.Set($"{nodeId}.retry_count", snapshot.RetryCount.ToString());
        context.Set($"{nodeId}.fail_count", snapshot.FailCount.ToString());
        context.Set($"{nodeId}.tool_calls", snapshot.ToolCalls.ToString());
        context.Set($"{nodeId}.tool_errors", snapshot.ToolErrors.ToString());
        context.Set($"{nodeId}.touched_files_count", snapshot.TouchedFilesCount.ToString());
        context.Set($"{nodeId}.total_tokens", snapshot.TotalTokens.ToString());
        context.Set($"{nodeId}.progress_score", snapshot.ProgressScore.ToString());
        context.Set($"{nodeId}.last_node_id", snapshot.LastNodeId);
        context.Set($"{nodeId}.last_completed_node", snapshot.LastCompletedNode);
        context.Set($"{nodeId}.stall_streak", stallStreak.ToString());
        context.Set($"{nodeId}.stall_status", escalated ? "escalated" : progressed ? "progressing" : "warning");
    }

    private static string BuildSupervisorPrompt(
        GraphNode node,
        PipelineContext context,
        SupervisorTelemetrySnapshot snapshot,
        int currentCycle,
        int maxCycles,
        int stallStreak)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Supervisor Cycle {currentCycle}/{maxCycles}]");
        sb.AppendLine(node.Prompt);
        sb.AppendLine();
        sb.AppendLine("Telemetry summary:");
        sb.AppendLine($"  source = {snapshot.SourcePath}");
        sb.AppendLine($"  stage_starts = {snapshot.StageStartCount}");
        sb.AppendLine($"  stage_ends = {snapshot.StageEndCount}");
        sb.AppendLine($"  success = {snapshot.SuccessCount}");
        sb.AppendLine($"  partial_success = {snapshot.PartialSuccessCount}");
        sb.AppendLine($"  retry = {snapshot.RetryCount}");
        sb.AppendLine($"  fail = {snapshot.FailCount}");
        sb.AppendLine($"  tool_calls = {snapshot.ToolCalls}");
        sb.AppendLine($"  tool_errors = {snapshot.ToolErrors}");
        sb.AppendLine($"  touched_files = {snapshot.TouchedFilesCount}");
        sb.AppendLine($"  total_tokens = {snapshot.TotalTokens}");
        sb.AppendLine($"  progress_score = {snapshot.ProgressScore}");
        sb.AppendLine($"  last_completed_node = {snapshot.LastCompletedNode}");
        sb.AppendLine($"  stall_streak = {stallStreak}");
        sb.AppendLine();
        sb.AppendLine("Current context:");
        foreach (var (key, value) in context.All.OrderBy(item => item.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {key} = {value}");
        return sb.ToString();
    }

    private static async Task WriteStatusAsync(
        string stageDir,
        string nodeId,
        int currentCycle,
        int maxCycles,
        List<Dictionary<string, object?>> cycleLog,
        string? telemetrySource,
        bool escalated,
        OutcomeStatus finalStatus,
        CancellationToken ct)
    {
        var statusData = new Dictionary<string, object?>
        {
            ["node_id"] = nodeId,
            ["status"] = StageStatusContract.ToStatusString(finalStatus),
            ["total_cycles"] = currentCycle,
            ["max_cycles"] = maxCycles,
            ["telemetry_source"] = telemetrySource,
            ["escalated"] = escalated,
            ["contract_validated"] = true,
            ["cycles"] = cycleLog
        };
        await File.WriteAllTextAsync(
            Path.Combine(stageDir, "status.json"),
            JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }
}
