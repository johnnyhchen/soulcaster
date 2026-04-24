namespace Soulcaster.UnifiedLlm;

using Soulcaster.UnifiedLlm.Errors;

public sealed record CodergenRoutingPolicy(
    string? PreferredModel = null,
    IReadOnlyList<string>? FallbackModels = null);

public sealed record CodergenRoutingDecision(
    string Provider,
    string Model,
    string DecisionSource,
    IReadOnlyList<string> Candidates);

public static class CodergenModelRouter
{
    public static CodergenRoutingDecision Resolve(
        string? provider,
        string? model,
        string? reasoningEffort,
        CodergenCapabilityRequirements requirements,
        CodergenRoutingPolicy? routingPolicy = null)
    {
        var candidates = BuildCandidates(provider, model, routingPolicy);
        CapabilityValidationError? lastValidationError = null;

        foreach (var candidate in candidates)
        {
            var resolvedModel = Client.ResolveModelAlias(candidate.Model);
            var resolvedProvider = !string.IsNullOrWhiteSpace(candidate.Provider)
                ? candidate.Provider!
                : InferProvider(resolvedModel);
            if (string.IsNullOrWhiteSpace(resolvedProvider))
            {
                lastValidationError = new CapabilityValidationError(
                    $"Model capability validation failed: Soulcaster could not infer a provider for model '{resolvedModel}'.",
                    provider,
                    resolvedModel,
                    "provider_match");
                continue;
            }

            try
            {
                ModelCapabilityValidator.ValidateResolvedCodergenSelection(
                    resolvedProvider,
                    resolvedModel,
                    reasoningEffort,
                    requirements);
                return new CodergenRoutingDecision(
                    Provider: resolvedProvider,
                    Model: resolvedModel,
                    DecisionSource: candidate.Source,
                    Candidates: candidates.Select(item => Client.ResolveModelAlias(item.Model)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            catch (CapabilityValidationError ex)
            {
                lastValidationError = ex;
            }
        }

        throw lastValidationError ?? new CapabilityValidationError(
            "Model capability validation failed: no model candidates were available for this stage.",
            provider,
            model,
            "routing");
    }

    private static List<ModelCandidate> BuildCandidates(
        string? provider,
        string? model,
        CodergenRoutingPolicy? routingPolicy)
    {
        var candidates = new List<ModelCandidate>();

        if (!string.IsNullOrWhiteSpace(model))
        {
            candidates.Add(new ModelCandidate(
                Provider: provider,
                Model: model!,
                Source: "explicit_model"));
            return candidates;
        }

        if (!string.IsNullOrWhiteSpace(routingPolicy?.PreferredModel))
        {
            candidates.Add(new ModelCandidate(
                Provider: provider,
                Model: routingPolicy.PreferredModel!,
                Source: "preferred_model"));
        }

        foreach (var fallbackModel in routingPolicy?.FallbackModels ?? [])
        {
            if (string.IsNullOrWhiteSpace(fallbackModel))
                continue;

            candidates.Add(new ModelCandidate(
                Provider: provider,
                Model: fallbackModel,
                Source: "fallback_model"));
        }

        if (candidates.Count > 0)
            return DistinctCandidates(candidates);

        if (!string.IsNullOrWhiteSpace(provider))
        {
            candidates.AddRange(
                ModelCatalog.ListModels(provider)
                    .Select(modelInfo => new ModelCandidate(
                        Provider: provider,
                        Model: modelInfo.Id,
                        Source: "provider_catalog")));
            return DistinctCandidates(candidates);
        }

        var defaultProvider = ResolveDefaultProviderFromEnvironment();
        candidates.AddRange(
            ModelCatalog.ListModels(defaultProvider)
                .Select(modelInfo => new ModelCandidate(
                    Provider: defaultProvider,
                    Model: modelInfo.Id,
                    Source: "default_provider_catalog")));
        return DistinctCandidates(candidates);
    }

    private static List<ModelCandidate> DistinctCandidates(IEnumerable<ModelCandidate> candidates)
    {
        var result = new List<ModelCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var key = $"{candidate.Provider ?? InferProvider(candidate.Model) ?? "unknown"}::{Client.ResolveModelAlias(candidate.Model)}";
            if (!seen.Add(key))
                continue;

            result.Add(candidate);
        }

        return result;
    }

    private static string ResolveDefaultProviderFromEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return "anthropic";

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            return "openai";

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
            return "gemini";

        return "anthropic";
    }

    private static string? InferProvider(string model)
    {
        var lower = model.ToLowerInvariant();
        if (lower.StartsWith("claude", StringComparison.Ordinal))
            return "anthropic";
        if (lower.StartsWith("gpt", StringComparison.Ordinal) ||
            lower.StartsWith("o1", StringComparison.Ordinal) ||
            lower.StartsWith("o3", StringComparison.Ordinal) ||
            lower.StartsWith("o4", StringComparison.Ordinal) ||
            lower.StartsWith("codex", StringComparison.Ordinal))
        {
            return "openai";
        }
        if (lower.StartsWith("gemini", StringComparison.Ordinal))
            return "gemini";

        return null;
    }

    private sealed record ModelCandidate(
        string? Provider,
        string Model,
        string Source);
}
