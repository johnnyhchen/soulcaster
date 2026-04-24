using System.Text.Json;
using Soulcaster.Attractor;

namespace Soulcaster.Runner;

internal sealed record NodeMutationPolicy(
    string NodeId,
    bool AllowOperatorRetry,
    bool AllowForceAdvanceTarget,
    int? OperatorRetryBudget);

internal sealed record WorkflowMutationPolicy(
    bool AllowForceAdvance,
    IReadOnlyList<string> ForceAdvanceTargets,
    int? OperatorRetryBudget,
    int? OperatorRetryStageBudget,
    string? RetryEscalationTarget,
    IReadOnlyDictionary<string, NodeMutationPolicy> NodePolicies);

internal sealed record RetryBudgetUsage(
    int TotalUsed,
    int StageUsed);

internal static class WorkflowPolicySupport
{
    private const string SnapshotFileName = "control_policy.json";

    private sealed record NodeMutationPolicySnapshot(
        string node_id,
        bool allow_operator_retry,
        bool allow_force_advance_target,
        int? operator_retry_budget);

    private sealed record WorkflowMutationPolicySnapshot(
        bool allow_force_advance,
        List<string> force_advance_targets,
        int? operator_retry_budget,
        int? operator_retry_stage_budget,
        string? retry_escalation_target,
        List<NodeMutationPolicySnapshot> nodes);

    public static string GetSnapshotPath(string pipelineDir) =>
        Path.Combine(Path.GetFullPath(pipelineDir), "store", SnapshotFileName);

    public static WorkflowMutationPolicy Build(Graph graph)
    {
        var defaultAllowOperatorRetry = ParseBoolean(
            graph.Attributes.GetValueOrDefault("allow_operator_retry"),
            defaultValue: true);
        var defaultAllowForceAdvanceTarget = ParseBoolean(
            graph.Attributes.GetValueOrDefault("allow_force_advance_target"),
            defaultValue: true);

        var nodePolicies = new Dictionary<string, NodeMutationPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes.Values)
        {
            nodePolicies[node.Id] = new NodeMutationPolicy(
                NodeId: node.Id,
                AllowOperatorRetry: ParseBoolean(
                    node.RawAttributes.GetValueOrDefault("allow_operator_retry"),
                    defaultAllowOperatorRetry),
                AllowForceAdvanceTarget: ParseBoolean(
                    node.RawAttributes.GetValueOrDefault("allow_force_advance_target"),
                    defaultAllowForceAdvanceTarget),
                OperatorRetryBudget: ParseNonNegativeInt(node.RawAttributes.GetValueOrDefault("operator_retry_budget")));
        }

        return new WorkflowMutationPolicy(
            AllowForceAdvance: ParseBoolean(
                graph.Attributes.GetValueOrDefault("allow_force_advance"),
                defaultValue: true),
            ForceAdvanceTargets: ParseCsvList(graph.Attributes.GetValueOrDefault("force_advance_targets")),
            OperatorRetryBudget: ParseNonNegativeInt(graph.Attributes.GetValueOrDefault("operator_retry_budget")),
            OperatorRetryStageBudget: ParseNonNegativeInt(graph.Attributes.GetValueOrDefault("operator_retry_stage_budget")),
            RetryEscalationTarget: Normalize(graph.Attributes.GetValueOrDefault("retry_escalation_target")),
            NodePolicies: nodePolicies);
    }

    public static WorkflowMutationPolicy LoadForRun(string pipelineDir, string? graphPath = null)
    {
        var snapshotPath = GetSnapshotPath(pipelineDir);
        if (File.Exists(snapshotPath))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<WorkflowMutationPolicySnapshot>(
                    File.ReadAllText(snapshotPath));
                if (snapshot is not null)
                    return FromSnapshot(snapshot);
            }
            catch
            {
                // Fall back to the graph path below.
            }
        }

        if (!string.IsNullOrWhiteSpace(graphPath) && File.Exists(graphPath))
        {
            try
            {
                return Build(DotParser.Parse(File.ReadAllText(graphPath)));
            }
            catch
            {
                // Fall through to the default policy below.
            }
        }

        return new WorkflowMutationPolicy(
            AllowForceAdvance: true,
            ForceAdvanceTargets: Array.Empty<string>(),
            OperatorRetryBudget: null,
            OperatorRetryStageBudget: null,
            RetryEscalationTarget: null,
            NodePolicies: new Dictionary<string, NodeMutationPolicy>(StringComparer.OrdinalIgnoreCase));
    }

    public static void WriteSnapshot(string pipelineDir, Graph graph)
    {
        var policy = Build(graph);
        var snapshotPath = GetSnapshotPath(pipelineDir);
        var directory = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var snapshot = new WorkflowMutationPolicySnapshot(
            allow_force_advance: policy.AllowForceAdvance,
            force_advance_targets: policy.ForceAdvanceTargets.ToList(),
            operator_retry_budget: policy.OperatorRetryBudget,
            operator_retry_stage_budget: policy.OperatorRetryStageBudget,
            retry_escalation_target: policy.RetryEscalationTarget,
            nodes: policy.NodePolicies.Values
                .OrderBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase)
                .Select(item => new NodeMutationPolicySnapshot(
                    node_id: item.NodeId,
                    allow_operator_retry: item.AllowOperatorRetry,
                    allow_force_advance_target: item.AllowForceAdvanceTarget,
                    operator_retry_budget: item.OperatorRetryBudget))
                .ToList());

        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, RunnerJson.Options));
    }

    public static RetryBudgetUsage ReadRetryBudgetUsage(string logsDir, string nodeId)
    {
        var eventsPath = Path.Combine(logsDir, "events.jsonl");
        if (!File.Exists(eventsPath))
            return new RetryBudgetUsage(0, 0);

        var total = 0;
        var stage = 0;
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = root.TryGetProperty("event_type", out var eventTypeElement)
                    ? eventTypeElement.GetString()
                    : null;
                if (!string.Equals(eventType, "operator_retry_stage_requested", StringComparison.Ordinal))
                    continue;

                total += 1;
                var eventNodeId = root.TryGetProperty("node_id", out var nodeIdElement)
                    ? nodeIdElement.GetString()
                    : null;
                if (string.Equals(eventNodeId, nodeId, StringComparison.Ordinal))
                    stage += 1;
            }
            catch
            {
                // Ignore malformed event rows during policy accounting.
            }
        }

        return new RetryBudgetUsage(total, stage);
    }

    public static bool TryValidateOperatorRetry(
        WorkflowMutationPolicy policy,
        string nodeId,
        RetryBudgetUsage usage,
        out string? reason)
    {
        reason = null;
        if (policy.NodePolicies.TryGetValue(nodeId, out var nodePolicy) && !nodePolicy.AllowOperatorRetry)
        {
            reason = $"Stage '{nodeId}' is not eligible for operator-triggered retry.";
            return false;
        }

        if (policy.OperatorRetryBudget is int totalBudget && usage.TotalUsed >= totalBudget)
        {
            reason = BuildBudgetMessage(
                $"Operator retry budget exhausted for this run ({usage.TotalUsed}/{totalBudget}).",
                policy.RetryEscalationTarget);
            return false;
        }

        var stageBudget = policy.NodePolicies.TryGetValue(nodeId, out nodePolicy)
            ? nodePolicy.OperatorRetryBudget ?? policy.OperatorRetryStageBudget
            : policy.OperatorRetryStageBudget;
        if (stageBudget is int perStageBudget && usage.StageUsed >= perStageBudget)
        {
            reason = BuildBudgetMessage(
                $"Operator retry budget exhausted for stage '{nodeId}' ({usage.StageUsed}/{perStageBudget}).",
                policy.RetryEscalationTarget);
            return false;
        }

        return true;
    }

    public static bool TryValidateForceAdvanceTarget(
        WorkflowMutationPolicy policy,
        string nodeId,
        out string? reason)
    {
        reason = null;
        if (!policy.AllowForceAdvance)
        {
            reason = "Force-advance is disabled for this workflow.";
            return false;
        }

        if (policy.ForceAdvanceTargets.Count > 0 &&
            !policy.ForceAdvanceTargets.Contains(nodeId, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"Node '{nodeId}' is not in the configured force_advance_targets allow-list.";
            return false;
        }

        if (policy.NodePolicies.TryGetValue(nodeId, out var nodePolicy) && !nodePolicy.AllowForceAdvanceTarget)
        {
            reason = $"Node '{nodeId}' is not an allowed force-advance target.";
            return false;
        }

        return true;
    }

    private static WorkflowMutationPolicy FromSnapshot(WorkflowMutationPolicySnapshot snapshot)
    {
        var nodePolicies = snapshot.nodes
            .ToDictionary(
                item => item.node_id,
                item => new NodeMutationPolicy(
                    NodeId: item.node_id,
                    AllowOperatorRetry: item.allow_operator_retry,
                    AllowForceAdvanceTarget: item.allow_force_advance_target,
                    OperatorRetryBudget: item.operator_retry_budget),
                StringComparer.OrdinalIgnoreCase);

        return new WorkflowMutationPolicy(
            AllowForceAdvance: snapshot.allow_force_advance,
            ForceAdvanceTargets: snapshot.force_advance_targets,
            OperatorRetryBudget: snapshot.operator_retry_budget,
            OperatorRetryStageBudget: snapshot.operator_retry_stage_budget,
            RetryEscalationTarget: snapshot.retry_escalation_target,
            NodePolicies: nodePolicies);
    }

    private static string BuildBudgetMessage(string message, string? escalationTarget) =>
        string.IsNullOrWhiteSpace(escalationTarget)
            ? message
            : $"{message} Escalate via '{escalationTarget}'.";

    private static List<string> ParseCsvList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static bool ParseBoolean(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("1", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseNonNegativeInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
