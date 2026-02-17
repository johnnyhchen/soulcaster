namespace JcAttractor.Attractor;

public interface INodeHandler
{
    Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default);
}
