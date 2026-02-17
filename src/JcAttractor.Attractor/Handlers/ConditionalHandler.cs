namespace JcAttractor.Attractor;

public class ConditionalHandler : INodeHandler
{
    public Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Pass-through; the engine evaluates edge conditions.
        return Task.FromResult(new Outcome(OutcomeStatus.Success, Notes: $"Conditional node '{node.Id}' passed through."));
    }
}
