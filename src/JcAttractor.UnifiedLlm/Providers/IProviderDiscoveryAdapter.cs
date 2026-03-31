namespace JcAttractor.UnifiedLlm;

public interface IProviderDiscoveryAdapter
{
    Task<ProviderPingResult> PingAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProviderModelDescriptor>> ListModelsAsync(CancellationToken ct = default);
}

public sealed record ProviderPingResult(
    string Provider,
    bool Success,
    string Endpoint,
    int? StatusCode = null,
    int? ModelCount = null,
    string? Message = null);

public sealed record ProviderModelDescriptor(
    string Provider,
    string Id,
    string? DisplayName = null,
    int? ContextWindow = null,
    int? MaxOutput = null,
    bool? SupportsTools = null,
    bool? SupportsVision = null,
    bool? SupportsReasoning = null,
    string? RawJson = null);
