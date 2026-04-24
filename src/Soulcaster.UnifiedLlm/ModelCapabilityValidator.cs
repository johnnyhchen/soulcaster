namespace Soulcaster.UnifiedLlm;

using Soulcaster.UnifiedLlm.Errors;
using Soulcaster.UnifiedLlm.Models;

public sealed record CodergenCapabilityRequirements(
    string ExecutionLane = "agent",
    bool RequireVision = false,
    bool RequireImageInput = false,
    bool RequireDocumentInput = false,
    bool RequireAudioInput = false,
    decimal? MaxInputCostPerMillion = null,
    decimal? MaxOutputCostPerMillion = null,
    long? MaxExpectedLatencyMs = null,
    IReadOnlyList<ResponseModality>? OutputModalities = null);

public static class ModelCapabilityValidator
{
    public static void ValidateExplicitCodergenSelection(
        string? provider,
        string? model,
        string? reasoningEffort,
        CodergenCapabilityRequirements? requirements = null)
    {
        if (ShouldBypassValidation(provider, model) || string.IsNullOrWhiteSpace(model))
            return;

        var effectiveProvider = string.IsNullOrWhiteSpace(provider)
            ? InferProvider(model!)
            : provider;

        if (string.IsNullOrWhiteSpace(effectiveProvider))
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: Soulcaster could not infer a provider for model '{model}'.",
                provider,
                model,
                "tools");
        }

        ValidateResolvedCodergenSelection(effectiveProvider, model!, reasoningEffort, requirements);
    }

    public static void ValidateResolvedCodergenSelection(
        string provider,
        string model,
        string? reasoningEffort,
        CodergenCapabilityRequirements? requirements = null)
    {
        if (ShouldBypassValidation(provider, model))
            return;

        var resolvedModel = Client.ResolveModelAlias(model);
        var info = ModelCatalog.GetModelInfo(resolvedModel);
        if (info is null)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' is not in the local capability catalog, so Soulcaster cannot verify tool support.",
                provider,
                resolvedModel,
                "tools");
        }

        if (!string.Equals(info.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' belongs to provider '{info.Provider}', not '{provider}'.",
                provider,
                resolvedModel,
                "provider_match");
        }

        var lane = NormalizeExecutionLane(requirements?.ExecutionLane);
        if (lane == "agent" && info.SupportsTools != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise tool support, but codergen stages require tools.",
                provider,
                resolvedModel,
                "tools");
        }

        if (lane == "multimodal_leaf" && info.SupportsVision != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise multimodal vision support required for execution_lane='{lane}'.",
                provider,
                resolvedModel,
                "vision");
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort) && info.SupportsReasoning != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise reasoning support, but reasoning_effort='{reasoningEffort}' was requested.",
                provider,
                resolvedModel,
                "reasoning");
        }

        if (requirements?.RequireVision == true && info.SupportsVision != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise vision support required by this stage.",
                provider,
                resolvedModel,
                "vision");
        }

        if (requirements?.RequireImageInput == true && !SupportsImageInput(info))
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise image input support required by this stage.",
                provider,
                resolvedModel,
                "image_input");
        }

        if (requirements?.RequireDocumentInput == true && info.SupportsDocumentInput != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise document input support required by this stage.",
                provider,
                resolvedModel,
                "document_input");
        }

        if (requirements?.RequireAudioInput == true && info.SupportsAudioInput != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise audio input support required by this stage.",
                provider,
                resolvedModel,
                "audio_input");
        }

        if (requirements?.OutputModalities?.Contains(ResponseModality.Image) == true &&
            info.SupportsImageOutput != true)
        {
            throw new CapabilityValidationError(
                $"Model capability validation failed: model '{resolvedModel}' does not advertise image output support required by this stage.",
                provider,
                resolvedModel,
                "image_output");
        }

        if (requirements?.MaxInputCostPerMillion is decimal maxInputCost)
        {
            if (info.InputCostPerMillion is null || info.InputCostPerMillion > maxInputCost)
            {
                throw new CapabilityValidationError(
                    $"Model capability validation failed: model '{resolvedModel}' exceeds max_input_cost_per_million={maxInputCost} or the catalog cannot verify its input cost.",
                    provider,
                    resolvedModel,
                    "budget_input");
            }
        }

        if (requirements?.MaxOutputCostPerMillion is decimal maxOutputCost)
        {
            if (info.OutputCostPerMillion is null || info.OutputCostPerMillion > maxOutputCost)
            {
                throw new CapabilityValidationError(
                    $"Model capability validation failed: model '{resolvedModel}' exceeds max_output_cost_per_million={maxOutputCost} or the catalog cannot verify its output cost.",
                    provider,
                    resolvedModel,
                    "budget_output");
            }
        }

        if (requirements?.MaxExpectedLatencyMs is long maxExpectedLatencyMs)
        {
            if (info.ExpectedLatencyMs is null || info.ExpectedLatencyMs > maxExpectedLatencyMs)
            {
                throw new CapabilityValidationError(
                    $"Model capability validation failed: model '{resolvedModel}' exceeds max_expected_latency_ms={maxExpectedLatencyMs} or the catalog cannot verify its expected latency.",
                    provider,
                    resolvedModel,
                    "latency");
            }
        }
    }

    private static bool ShouldBypassValidation(string? provider, string? model)
    {
        return string.Equals(provider, "scripted", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(model, "scripted-model", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeExecutionLane(string? lane)
    {
        return lane?.Trim().ToLowerInvariant() switch
        {
            null or "" => "agent",
            "agent" => "agent",
            "leaf" => "leaf",
            "multimodal_leaf" => "multimodal_leaf",
            var other => throw new CapabilityValidationError(
                $"Model capability validation failed: execution lane '{other}' is unknown.",
                null,
                null,
                "execution_lane")
        };
    }

    private static bool SupportsImageInput(ModelInfo info) =>
        info.SupportsImageInput == true || (info.SupportsImageInput is null && info.SupportsVision == true);
}
