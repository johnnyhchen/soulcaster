namespace Soulcaster.Runner.Storage;

internal sealed class CompositeWorkflowStore : IWorkflowStore
{
    private readonly IReadOnlyList<IWorkflowStore> _stores;

    public CompositeWorkflowStore(IEnumerable<IWorkflowStore> stores)
    {
        _stores = stores?.Where(store => store is not null).ToList()
            ?? throw new ArgumentNullException(nameof(stores));

        if (_stores.Count == 0)
            throw new ArgumentException("At least one workflow store is required.", nameof(stores));

        WorkingDirectory = _stores[0].WorkingDirectory;
        StoreDirectory = _stores[0].StoreDirectory;
    }

    public string BackendId => string.Join("+", _stores.Select(store => store.BackendId));

    public string WorkingDirectory { get; }

    public string StoreDirectory { get; }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        foreach (var store in _stores)
            await store.SyncAsync(ct);
    }
}
