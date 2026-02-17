namespace JcAttractor.Attractor;

public class FanInHandler : INodeHandler
{
    public Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Fan-in waits for parallel branches. The engine manages actual coordination.
        return Task.FromResult(new Outcome(OutcomeStatus.Success, Notes: $"Fan-in node '{node.Id}' synchronized."));
    }
}
