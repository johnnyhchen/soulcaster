namespace Soulcaster.UnifiedLlm.Models;

public record ModelInfo(
    string Id,
    string Provider,
    string DisplayName,
    int ContextWindow,
    int? MaxOutput,
    bool? SupportsTools,
    bool? SupportsVision,
    bool? SupportsReasoning,
    decimal? InputCostPerMillion,
    decimal? OutputCostPerMillion,
    List<string>? Aliases = null,
    bool? SupportsStreaming = null,
    bool? SupportsStructuredOutput = null,
    bool? SupportsImageOutput = null,
    bool? SupportsAudioOutput = null,
    bool? SupportsLongContext = null,
    bool? RequiresContinuityTokens = null,
    long? ExpectedLatencyMs = null,
    bool? SupportsImageInput = null,
    bool? SupportsDocumentInput = null,
    bool? SupportsAudioInput = null);
