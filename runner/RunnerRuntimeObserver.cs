using JcAttractor.Attractor;

namespace JcAttractor.Runner;

internal sealed class InjectedRunnerCrashException : Exception
{
    public InjectedRunnerCrashException(string stageId)
        : base($"Injected crash after checkpoint for stage '{stageId}'.")
    {
        StageId = stageId;
    }

    public string StageId { get; }
}

internal sealed class RunnerRuntimeObserver : IPipelineRuntimeObserver
{
    private readonly RunManifest _manifest;
    private readonly string _manifestPath;
    private readonly string _checkpointPath;
    private readonly string? _crashAfterStage;
    private int _crashInjected;

    public RunnerRuntimeObserver(
        RunManifest manifest,
        string manifestPath,
        string checkpointPath,
        string? crashAfterStage)
    {
        _manifest = manifest;
        _manifestPath = manifestPath;
        _checkpointPath = checkpointPath;
        _crashAfterStage = crashAfterStage;
    }

    public Task OnStageStartedAsync(
        string nodeId,
        GraphNode node,
        PipelineContext context,
        CancellationToken ct = default)
    {
        _manifest.active_stage = nodeId;
        _manifest.status = "running";
        _manifest.updated_at = DateTime.UtcNow.ToString("o");
        _manifest.Save(_manifestPath);
        return Task.CompletedTask;
    }

    public Task OnCheckpointSavedAsync(
        string currentNodeId,
        string nextNodeId,
        GraphEdge? selectedEdge,
        PipelineContext context,
        CancellationToken ct = default)
    {
        _manifest.active_stage = nextNodeId;
        _manifest.updated_at = DateTime.UtcNow.ToString("o");
        _manifest.checkpoint_path = _checkpointPath;
        _manifest.Save(_manifestPath);

        if (!string.IsNullOrWhiteSpace(_crashAfterStage) &&
            string.Equals(_crashAfterStage, currentNodeId, StringComparison.Ordinal) &&
            Interlocked.Exchange(ref _crashInjected, 1) == 0)
        {
            throw new InjectedRunnerCrashException(currentNodeId);
        }

        return Task.CompletedTask;
    }
}
