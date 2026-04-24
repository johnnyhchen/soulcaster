namespace Soulcaster.Runner.Storage;

internal interface IWorkflowStore
{
    string BackendId { get; }
    string WorkingDirectory { get; }
    string StoreDirectory { get; }

    Task SyncAsync(CancellationToken ct = default);
}
