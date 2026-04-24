using Soulcaster.Attractor;
using Soulcaster.Runner.Storage;

namespace Soulcaster.Runner;

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
    private readonly IWorkflowStore? _workflowStore;
    private readonly SqliteRunStateStore? _runStateStore;

    public RunnerRuntimeObserver(
        RunManifest manifest,
        string manifestPath,
        string checkpointPath,
        SqliteRunStateStore? runStateStore = null,
        IWorkflowStore? workflowStore = null)
    {
        _manifest = manifest;
        _manifestPath = manifestPath;
        _checkpointPath = checkpointPath;
        _runStateStore = runStateStore;
        _workflowStore = workflowStore;
    }

    public async Task OnStageStartedAsync(
        string nodeId,
        GraphNode node,
        PipelineContext context,
        CancellationToken ct = default)
    {
        _manifest.active_stage = nodeId;
        _manifest.status = "running";
        _manifest.updated_at = DateTime.UtcNow.ToString("o");
        if (_runStateStore is not null)
            await _runStateStore.PersistAsync(_manifestPath, _manifest, ct: ct);
        else
            _manifest.Save(_manifestPath);
        await SyncStoreAsync(ct);
    }

    public async Task OnCheckpointSavedAsync(
        string currentNodeId,
        string nextNodeId,
        GraphEdge? selectedEdge,
        PipelineContext context,
        CancellationToken ct = default)
    {
        _manifest.active_stage = nextNodeId;
        _manifest.updated_at = DateTime.UtcNow.ToString("o");
        _manifest.checkpoint_path = _checkpointPath;
        if (_runStateStore is not null)
            await _runStateStore.PersistAsync(_manifestPath, _manifest, ct: ct);
        else
            _manifest.Save(_manifestPath);
        await SyncStoreAsync(ct);

        var crashTarget = _manifest.crash_after_stage;
        if (!string.IsNullOrWhiteSpace(crashTarget) &&
            (string.Equals(crashTarget, "*", StringComparison.Ordinal) ||
             string.Equals(crashTarget, currentNodeId, StringComparison.Ordinal)) &&
            _manifest.crash_injections_remaining > 0)
        {
            _manifest.crash_injections_remaining -= 1;
            _manifest.crash_after_stage = _manifest.crash_injections_remaining > 0 ? "*" : null;
            _manifest.updated_at = DateTime.UtcNow.ToString("o");
            if (_runStateStore is not null)
                await _runStateStore.PersistAsync(_manifestPath, _manifest, ct: ct);
            else
                _manifest.Save(_manifestPath);

            throw new InjectedRunnerCrashException(currentNodeId);
        }
    }

    private async Task SyncStoreAsync(CancellationToken ct)
    {
        if (_workflowStore is null)
            return;

        try
        {
            await _workflowStore.SyncAsync(ct);
        }
        catch
        {
            // Store sync must not break runtime advancement.
        }
    }
}
