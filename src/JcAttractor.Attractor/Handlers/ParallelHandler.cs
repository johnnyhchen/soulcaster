namespace JcAttractor.Attractor;

public class ParallelHandler : INodeHandler
{
    private readonly HandlerRegistry _registry;

    public ParallelHandler(HandlerRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Fan out to multiple target nodes via outgoing edges
        var outgoingEdges = graph.OutgoingEdges(node.Id);
        if (outgoingEdges.Count == 0)
        {
            return new Outcome(OutcomeStatus.Success, Notes: $"Parallel node '{node.Id}' has no outgoing edges.");
        }

        var targetNodeIds = outgoingEdges.Select(e => e.ToNode).Distinct().ToList();
        var tasks = new List<Task<(string NodeId, Outcome Outcome)>>();

        foreach (var targetId in targetNodeIds)
        {
            if (!graph.Nodes.TryGetValue(targetId, out var targetNode))
                continue;

            var handler = _registry.GetHandler(targetNode.Shape);
            if (handler == null)
                continue;

            tasks.Add(ExecuteTargetAsync(targetId, handler, targetNode, context, graph, logsRoot, ct));
        }

        var results = await Task.WhenAll(tasks);

        // Combine outcomes
        var contextUpdates = new Dictionary<string, string>();
        var allSuccess = true;
        var anyFail = false;
        var notes = new List<string>();

        foreach (var (nodeId, outcome) in results)
        {
            if (outcome.ContextUpdates != null)
            {
                foreach (var (k, v) in outcome.ContextUpdates)
                    contextUpdates[k] = v;
            }

            if (outcome.Status != OutcomeStatus.Success)
                allSuccess = false;
            if (outcome.Status == OutcomeStatus.Fail)
                anyFail = true;

            notes.Add($"{nodeId}: {outcome.Status}");
        }

        var combinedStatus = allSuccess ? OutcomeStatus.Success
            : anyFail ? OutcomeStatus.Fail
            : OutcomeStatus.PartialSuccess;

        return new Outcome(
            Status: combinedStatus,
            ContextUpdates: contextUpdates.Count > 0 ? contextUpdates : null,
            Notes: $"Parallel node '{node.Id}' completed: {string.Join(", ", notes)}"
        );
    }

    private static async Task<(string NodeId, Outcome Outcome)> ExecuteTargetAsync(
        string nodeId, INodeHandler handler, GraphNode node,
        PipelineContext context, Graph graph, string logsRoot, CancellationToken ct)
    {
        try
        {
            var outcome = await handler.ExecuteAsync(node, context, graph, logsRoot, ct);
            return (nodeId, outcome);
        }
        catch (Exception ex)
        {
            return (nodeId, new Outcome(OutcomeStatus.Fail, Notes: $"Error in {nodeId}: {ex.Message}"));
        }
    }
}
