namespace JcAttractor.Attractor;

public class ExitHandler : INodeHandler
{
    public Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        return Task.FromResult(new Outcome(OutcomeStatus.Success, Notes: "Exit node reached."));
    }
}
