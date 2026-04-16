namespace Soulcaster.UnifiedLlm.Providers;

public interface IProviderAdapter
{
    string Name { get; }
    Task<Response> CompleteAsync(Request request, CancellationToken ct = default);
    IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken ct = default);
}
