namespace Soulcaster.UnifiedLlm;

/// <summary>
/// Static catalog of well-known LLM models with their capabilities and pricing.
/// </summary>
public static class ModelCatalog
{
    private static readonly List<ModelInfo> _models =
    [
        // ── Anthropic ──────────────────────────────────────────────────
        new ModelInfo(
            Id: "claude-opus-4-6",
            Provider: "anthropic",
            DisplayName: "Claude Opus 4.6",
            ContextWindow: 200_000,
            MaxOutput: 32_768,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 15.00m,
            OutputCostPerMillion: 75.00m,
            Aliases: ["claude-opus-4-6-20250617"]),

        new ModelInfo(
            Id: "claude-sonnet-4-6",
            Provider: "anthropic",
            DisplayName: "Claude Sonnet 4.6",
            ContextWindow: 200_000,
            MaxOutput: 16_384,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 3.00m,
            OutputCostPerMillion: 15.00m,
            Aliases: ["claude-sonnet-4-6-20250514"]),

        new ModelInfo(
            Id: "claude-sonnet-4-5-20250514",
            Provider: "anthropic",
            DisplayName: "Claude Sonnet 4.5",
            ContextWindow: 200_000,
            MaxOutput: 16_384,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 3.00m,
            OutputCostPerMillion: 15.00m,
            Aliases: ["claude-sonnet-4-5-latest"]),

        new ModelInfo(
            Id: "claude-haiku-4-5",
            Provider: "anthropic",
            DisplayName: "Claude Haiku 4.5",
            ContextWindow: 200_000,
            MaxOutput: 8_192,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: false,
            InputCostPerMillion: 0.80m,
            OutputCostPerMillion: 4.00m,
            Aliases: ["claude-haiku-4-5-20251001"]),

        // ── OpenAI ─────────────────────────────────────────────────────
        new ModelInfo(
            Id: "gpt-5.2",
            Provider: "openai",
            DisplayName: "GPT-5.2",
            ContextWindow: 200_000,
            MaxOutput: 32_768,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 10.00m,
            OutputCostPerMillion: 30.00m,
            Aliases: ["gpt-5.2-2025-01-21"]),

        new ModelInfo(
            Id: "gpt-5.2-mini",
            Provider: "openai",
            DisplayName: "GPT-5.2 Mini",
            ContextWindow: 200_000,
            MaxOutput: 16_384,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 1.10m,
            OutputCostPerMillion: 4.40m,
            Aliases: ["gpt-5.2-mini-2025-01-21"]),

        // GPT-5.4 is accepted explicitly for routing and selection in this runtime.
        // Cost metadata is intentionally left unset until OpenAI publishes a model card.
        new ModelInfo(
            Id: "gpt-5.4",
            Provider: "openai",
            DisplayName: "GPT-5.4",
            ContextWindow: 200_000,
            MaxOutput: 32_768,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: null,
            OutputCostPerMillion: null),

        new ModelInfo(
            Id: "gpt-5.2-codex",
            Provider: "openai",
            DisplayName: "GPT-5.2 Codex",
            ContextWindow: 200_000,
            MaxOutput: 32_768,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 10.00m,
            OutputCostPerMillion: 30.00m,
            Aliases: ["codex-5.2"]),

        new ModelInfo(
            Id: "gpt-5.3-codex",
            Provider: "openai",
            DisplayName: "Codex 5.3",
            ContextWindow: 200_000,
            MaxOutput: 32_768,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 10.00m,
            OutputCostPerMillion: 30.00m,
            Aliases: ["codex-5.3"]),

        // ── Google Gemini ──────────────────────────────────────────────
        new ModelInfo(
            Id: "gemini-3.0-pro-preview",
            Provider: "gemini",
            DisplayName: "Gemini 3 Pro Preview",
            ContextWindow: 1_000_000,
            MaxOutput: 65_536,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 2.50m,
            OutputCostPerMillion: 15.00m,
            Aliases: ["gemini-3-pro"]),

        new ModelInfo(
            Id: "gemini-3.0-flash-preview",
            Provider: "gemini",
            DisplayName: "Gemini 3 Flash Preview",
            ContextWindow: 1_000_000,
            MaxOutput: 65_536,
            SupportsTools: true,
            SupportsVision: true,
            SupportsReasoning: true,
            InputCostPerMillion: 0.15m,
            OutputCostPerMillion: 0.60m,
            Aliases: ["gemini-3-flash"]),

        new ModelInfo(
            Id: "gemini-2.5-pro",
            Provider: "gemini",
            DisplayName: "Gemini 2.5 Pro",
            ContextWindow: 1_048_576,
            MaxOutput: 65_536,
            SupportsTools: null,
            SupportsVision: null,
            SupportsReasoning: true,
            InputCostPerMillion: null,
            OutputCostPerMillion: null),
    ];

    /// <summary>
    /// Gets model information by exact ID or alias.
    /// Returns null if not found.
    /// </summary>
    public static ModelInfo? GetModelInfo(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;

        return _models.FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase) ||
            (m.Aliases?.Any(a => string.Equals(a, modelId, StringComparison.OrdinalIgnoreCase)) ?? false));
    }

    /// <summary>
    /// Lists all known models, optionally filtered by provider.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ListModels(string? provider = null)
    {
        if (provider is null)
            return _models.AsReadOnly();

        return _models
            .Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the latest/best model for a given provider.
    /// Optionally filter by capability ("tools", "vision", "reasoning").
    /// Returns the first matching model in catalog order (which lists best models first per provider).
    /// </summary>
    public static ModelInfo? GetLatestModel(string provider, string? capability = null)
    {
        var candidates = _models
            .Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase));

        if (capability is not null)
        {
            candidates = capability.ToLowerInvariant() switch
            {
                "tools" => candidates.Where(m => m.SupportsTools == true),
                "vision" => candidates.Where(m => m.SupportsVision == true),
                "reasoning" => candidates.Where(m => m.SupportsReasoning == true),
                _ => candidates
            };
        }

        return candidates.FirstOrDefault();
    }
}
