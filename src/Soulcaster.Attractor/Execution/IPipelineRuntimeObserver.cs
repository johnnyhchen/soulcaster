namespace Soulcaster.Attractor.Execution;

public interface IPipelineRuntimeObserver
{
    Task OnStageStartedAsync(
        string nodeId,
        GraphNode node,
        PipelineContext context,
        CancellationToken ct = default);

    Task OnCheckpointSavedAsync(
        string currentNodeId,
        string nextNodeId,
        GraphEdge? selectedEdge,
        PipelineContext context,
        CancellationToken ct = default);
}
