namespace Soulcaster.Attractor.Handlers;

public interface INodeHandler
{
    Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default);
}
