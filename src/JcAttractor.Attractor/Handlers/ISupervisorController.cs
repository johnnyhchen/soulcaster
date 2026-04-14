namespace JcAttractor.Attractor;

public sealed record SupervisorWorkerRuntime(
    string DotFilePath,
    string WorkingDir,
    string LogsDir,
    string TelemetryPath,
    string SteerPath);

public interface ISupervisorController
{
    Task<SupervisorWorkerRuntime> EnsureWorkerAsync(
        GraphNode node,
        Graph graph,
        string logsRoot,
        PipelineContext context,
        CancellationToken ct = default);

    Task WriteSteeringAsync(
        SupervisorWorkerRuntime worker,
        string steeringText,
        CancellationToken ct = default);

    Task StopWorkerAsync(
        SupervisorWorkerRuntime worker,
        CancellationToken ct = default);
}
