using Soulcaster.Attractor;

namespace Soulcaster.Runner;

internal sealed class SupervisorController : ISupervisorController
{
    private readonly RunOptions _parentOptions;
    private readonly string _projectRoot;
    private readonly string _parentDotFilePath;
    private readonly Dictionary<string, WorkerProcess> _workers = new(StringComparer.OrdinalIgnoreCase);

    public SupervisorController(RunOptions parentOptions, string projectRoot, string parentDotFilePath)
    {
        _parentOptions = parentOptions;
        _projectRoot = projectRoot;
        _parentDotFilePath = parentDotFilePath;
    }

    public async Task<SupervisorWorkerRuntime> EnsureWorkerAsync(
        GraphNode node,
        Graph graph,
        string logsRoot,
        PipelineContext context,
        CancellationToken ct = default)
    {
        var childDotfile = node.RawAttributes.GetValueOrDefault("child_dotfile", "");
        if (string.IsNullOrWhiteSpace(childDotfile))
            throw new InvalidOperationException($"Manager node '{node.Id}' is missing child_dotfile.");

        var resolvedDotFile = ResolveChildDotFile(childDotfile);
        var outputRoot = Path.GetDirectoryName(logsRoot) ?? logsRoot;
        var workerName = Path.GetFileNameWithoutExtension(resolvedDotFile);
        var workerDir = Path.Combine(outputRoot, "workers", workerName);
        var workerLogsDir = Path.Combine(workerDir, "logs");
        var steerPath = Path.Combine(workerLogsDir, "control", "steer_next.txt");

        Directory.CreateDirectory(Path.Combine(workerLogsDir, "control"));
        var runtime = new SupervisorWorkerRuntime(
            DotFilePath: resolvedDotFile,
            WorkingDir: workerDir,
            LogsDir: workerLogsDir,
            TelemetryPath: Path.Combine(workerLogsDir, "events.jsonl"),
            SteerPath: steerPath);

        if (!_workers.TryGetValue(workerDir, out var worker))
        {
            worker = new WorkerProcess(
                dotFilePath: resolvedDotFile,
                workingDir: workerDir,
                steerPath: steerPath,
                parentOptions: _parentOptions,
                environmentOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SOULCASTER_WORKER_RUN_DIR"] = workerDir,
                    ["SOULCASTER_WORKER_LOGS_DIR"] = workerLogsDir,
                    ["SOULCASTER_WORKER_STEER_FILE"] = steerPath
                });
            _workers[workerDir] = worker;
        }

        await worker.EnsureStartedAsync(ct);
        return runtime;
    }

    public Task WriteSteeringAsync(SupervisorWorkerRuntime worker, string steeringText, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(worker.SteerPath)!);
        File.WriteAllText(worker.SteerPath, steeringText ?? string.Empty);
        return Task.CompletedTask;
    }

    public async Task StopWorkerAsync(SupervisorWorkerRuntime worker, CancellationToken ct = default)
    {
        if (_workers.TryGetValue(worker.WorkingDir, out var process))
            await process.StopAsync(ct);
    }

    private string ResolveChildDotFile(string childDotfile)
    {
        if (Path.IsPathRooted(childDotfile))
            return Path.GetFullPath(childDotfile);

        var parentDir = Path.GetDirectoryName(_parentDotFilePath) ?? _projectRoot;
        var relativeToParent = Path.GetFullPath(Path.Combine(parentDir, childDotfile));
        if (File.Exists(relativeToParent))
            return relativeToParent;

        return Path.GetFullPath(Path.Combine(_projectRoot, childDotfile));
    }
}
