namespace JcAttractor.Attractor;

public class StartHandler : INodeHandler
{
    public Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        return Task.FromResult(new Outcome(OutcomeStatus.Success, Notes: "Start node executed."));
    }
}
