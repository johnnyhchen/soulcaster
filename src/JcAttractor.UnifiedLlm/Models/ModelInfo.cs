namespace JcAttractor.UnifiedLlm;

public record ModelInfo(
    string Id,
    string Provider,
    string DisplayName,
    int ContextWindow,
    int? MaxOutput,
    bool SupportsTools,
    bool SupportsVision,
    bool SupportsReasoning,
    decimal? InputCostPerMillion,
    decimal? OutputCostPerMillion,
    List<string>? Aliases = null);
