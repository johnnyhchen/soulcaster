namespace Soulcaster.UnifiedLlm;

using System.Text.Json;
using Soulcaster.UnifiedLlm.Models;
using Soulcaster.UnifiedLlm.Providers;

public sealed record ModelRegistryPaths(
    string RootDirectory,
    string DiscoveryDirectory,
    string OverridesPath);

public sealed record ModelRegistrySnapshot(
    string GeneratedAtUtc,
    IReadOnlyList<ModelInfo> Models,
    IReadOnlyList<ModelRegistrySourceSnapshot> Sources);

public sealed record ModelRegistrySourceSnapshot(
    string SourceId,
    string SourceType,
    string? Provider,
    string? Path,
    string Status,
    int ModelCount,
    string? LoadedAtUtc = null,
    string? ExpiresAtUtc = null,
    bool IsStale = false,
    string? Message = null);

public sealed record ModelRegistryDiscoveryCache(
    string Provider,
    string FetchedAtUtc,
    string ExpiresAtUtc,
    IReadOnlyList<ProviderModelDescriptor> Models,
    string? Endpoint = null);

public sealed record ModelRegistryOverrideFile(
    IReadOnlyList<ModelInfo> Models);

public static class ModelRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static ModelRegistrySnapshot LoadSnapshot(ModelRegistryPaths? paths = null)
    {
        var models = ModelCatalog.GetBaseModels()
            .Select(CloneModel)
            .ToList();
        var sources = new List<ModelRegistrySourceSnapshot>
        {
            new(
                SourceId: "built_in",
                SourceType: "built_in",
                Provider: null,
                Path: null,
                Status: "loaded",
                ModelCount: models.Count,
                LoadedAtUtc: DateTime.UtcNow.ToString("o"))
        };

        paths ??= ResolvePaths();
        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.DiscoveryDirectory);

        foreach (var cachePath in Directory.GetFiles(paths.DiscoveryDirectory, "*.json")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var sourceId = Path.GetFileNameWithoutExtension(cachePath);
            try
            {
                var cache = JsonSerializer.Deserialize<ModelRegistryDiscoveryCache>(
                    File.ReadAllText(cachePath),
                    JsonOptions);
                if (cache is null)
                {
                    sources.Add(new(
                        SourceId: sourceId,
                        SourceType: "discovery_cache",
                        Provider: null,
                        Path: cachePath,
                        Status: "invalid",
                        ModelCount: 0,
                        Message: "The discovery cache file was empty."));
                    continue;
                }

                var expiresAt = TryParseUtc(cache.ExpiresAtUtc);
                var isStale = expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow;
                if (isStale)
                {
                    sources.Add(new(
                        SourceId: sourceId,
                        SourceType: "discovery_cache",
                        Provider: cache.Provider,
                        Path: cachePath,
                        Status: "skipped_stale",
                        ModelCount: cache.Models.Count,
                        LoadedAtUtc: cache.FetchedAtUtc,
                        ExpiresAtUtc: cache.ExpiresAtUtc,
                        IsStale: true));
                    continue;
                }

                foreach (var discoveredModel in cache.Models)
                    MergeModel(models, ToModelInfo(discoveredModel));

                sources.Add(new(
                    SourceId: sourceId,
                    SourceType: "discovery_cache",
                    Provider: cache.Provider,
                    Path: cachePath,
                    Status: "loaded",
                    ModelCount: cache.Models.Count,
                    LoadedAtUtc: cache.FetchedAtUtc,
                    ExpiresAtUtc: cache.ExpiresAtUtc));
            }
            catch (Exception ex)
            {
                sources.Add(new(
                    SourceId: sourceId,
                    SourceType: "discovery_cache",
                    Provider: null,
                    Path: cachePath,
                    Status: "invalid",
                    ModelCount: 0,
                    Message: ex.Message));
            }
        }

        if (File.Exists(paths.OverridesPath))
        {
            try
            {
                var overrideFile = JsonSerializer.Deserialize<ModelRegistryOverrideFile>(
                    File.ReadAllText(paths.OverridesPath),
                    JsonOptions);
                if (overrideFile?.Models is { Count: > 0 })
                {
                    foreach (var overrideModel in overrideFile.Models)
                        MergeModel(models, overrideModel);

                    sources.Add(new(
                        SourceId: "overrides",
                        SourceType: "override_file",
                        Provider: null,
                        Path: paths.OverridesPath,
                        Status: "loaded",
                        ModelCount: overrideFile.Models.Count,
                        LoadedAtUtc: File.GetLastWriteTimeUtc(paths.OverridesPath).ToString("o")));
                }
            }
            catch (Exception ex)
            {
                sources.Add(new(
                    SourceId: "overrides",
                    SourceType: "override_file",
                    Provider: null,
                    Path: paths.OverridesPath,
                    Status: "invalid",
                    ModelCount: 0,
                    Message: ex.Message));
            }
        }

        return new ModelRegistrySnapshot(
            GeneratedAtUtc: DateTime.UtcNow.ToString("o"),
            Models: models.ToList(),
            Sources: sources);
    }

    public static ModelRegistryPaths ResolvePaths()
    {
        var root = Environment.GetEnvironmentVariable("SOULCASTER_MODEL_REGISTRY_DIR");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".attractor",
                "model-registry");
        }

        var rootDirectory = Path.GetFullPath(root);
        var discoveryDirectory = Path.Combine(rootDirectory, "discovery");

        var overridePath = Environment.GetEnvironmentVariable("SOULCASTER_MODEL_REGISTRY_OVERRIDES");
        if (string.IsNullOrWhiteSpace(overridePath))
            overridePath = Path.Combine(rootDirectory, "model-registry.overrides.json");

        return new ModelRegistryPaths(
            RootDirectory: rootDirectory,
            DiscoveryDirectory: discoveryDirectory,
            OverridesPath: Path.GetFullPath(overridePath));
    }

    public static string GetDiscoveryCachePath(string provider, ModelRegistryPaths? paths = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        paths ??= ResolvePaths();
        return Path.Combine(paths.DiscoveryDirectory, $"{normalizedProvider}.json");
    }

    public static async Task<string> WriteDiscoveryCacheAsync(
        string provider,
        IReadOnlyList<ProviderModelDescriptor> models,
        TimeSpan ttl,
        ModelRegistryPaths? paths = null,
        CancellationToken ct = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        paths ??= ResolvePaths();
        Directory.CreateDirectory(paths.DiscoveryDirectory);

        var fetchedAt = DateTimeOffset.UtcNow;
        var cache = new ModelRegistryDiscoveryCache(
            Provider: normalizedProvider,
            FetchedAtUtc: fetchedAt.ToString("o"),
            ExpiresAtUtc: fetchedAt.Add(ttl).ToString("o"),
            Models: models);

        var cachePath = GetDiscoveryCachePath(normalizedProvider, paths);
        await File.WriteAllTextAsync(
            cachePath,
            JsonSerializer.Serialize(cache, JsonOptions),
            ct);
        return cachePath;
    }

    private static string NormalizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required.", nameof(provider));

        return provider.Trim().ToLowerInvariant();
    }

    private static DateTimeOffset? TryParseUtc(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static ModelInfo ToModelInfo(ProviderModelDescriptor descriptor)
    {
        return new ModelInfo(
            Id: descriptor.Id,
            Provider: descriptor.Provider,
            DisplayName: descriptor.DisplayName ?? string.Empty,
            ContextWindow: descriptor.ContextWindow ?? 0,
            MaxOutput: descriptor.MaxOutput,
            SupportsTools: descriptor.SupportsTools,
            SupportsVision: descriptor.SupportsVision,
            SupportsReasoning: descriptor.SupportsReasoning,
            InputCostPerMillion: null,
            OutputCostPerMillion: null,
            SupportsStreaming: descriptor.SupportsStreaming,
            SupportsStructuredOutput: descriptor.SupportsStructuredOutput,
            SupportsImageOutput: descriptor.SupportsImageOutput,
            SupportsAudioOutput: descriptor.SupportsAudioOutput,
            SupportsLongContext: descriptor.SupportsLongContext,
            RequiresContinuityTokens: descriptor.RequiresContinuityTokens,
            ExpectedLatencyMs: descriptor.ExpectedLatencyMs,
            SupportsImageInput: descriptor.SupportsImageInput,
            SupportsDocumentInput: descriptor.SupportsDocumentInput,
            SupportsAudioInput: descriptor.SupportsAudioInput);
    }

    private static void MergeModel(ICollection<ModelInfo> models, ModelInfo update)
    {
        if (string.IsNullOrWhiteSpace(update.Id))
            return;

        var existing = models.FirstOrDefault(model => MatchesModel(model, update.Id));
        if (existing is null)
        {
            models.Add(NormalizeModel(update));
            return;
        }

        models.Remove(existing);
        models.Add(OverlayModel(existing, update));
    }

    private static bool MatchesModel(ModelInfo model, string idOrAlias)
    {
        if (string.Equals(model.Id, idOrAlias, StringComparison.OrdinalIgnoreCase))
            return true;

        return model.Aliases?.Any(alias => string.Equals(alias, idOrAlias, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static ModelInfo OverlayModel(ModelInfo baseline, ModelInfo update)
    {
        var aliases = MergeAliases(baseline.Aliases, update.Aliases);
        return NormalizeModel(baseline with
        {
            Provider = Choose(update.Provider, baseline.Provider),
            DisplayName = Choose(update.DisplayName, baseline.DisplayName),
            ContextWindow = update.ContextWindow > 0 ? update.ContextWindow : baseline.ContextWindow,
            MaxOutput = update.MaxOutput ?? baseline.MaxOutput,
            SupportsTools = update.SupportsTools ?? baseline.SupportsTools,
            SupportsVision = update.SupportsVision ?? baseline.SupportsVision,
            SupportsReasoning = update.SupportsReasoning ?? baseline.SupportsReasoning,
            InputCostPerMillion = update.InputCostPerMillion ?? baseline.InputCostPerMillion,
            OutputCostPerMillion = update.OutputCostPerMillion ?? baseline.OutputCostPerMillion,
            Aliases = aliases,
            SupportsStreaming = update.SupportsStreaming ?? baseline.SupportsStreaming,
            SupportsStructuredOutput = update.SupportsStructuredOutput ?? baseline.SupportsStructuredOutput,
            SupportsImageOutput = update.SupportsImageOutput ?? baseline.SupportsImageOutput,
            SupportsAudioOutput = update.SupportsAudioOutput ?? baseline.SupportsAudioOutput,
            SupportsLongContext = update.SupportsLongContext ?? baseline.SupportsLongContext,
            RequiresContinuityTokens = update.RequiresContinuityTokens ?? baseline.RequiresContinuityTokens,
            ExpectedLatencyMs = update.ExpectedLatencyMs ?? baseline.ExpectedLatencyMs,
            SupportsImageInput = update.SupportsImageInput ?? baseline.SupportsImageInput,
            SupportsDocumentInput = update.SupportsDocumentInput ?? baseline.SupportsDocumentInput,
            SupportsAudioInput = update.SupportsAudioInput ?? baseline.SupportsAudioInput
        });
    }

    private static List<string>? MergeAliases(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var merged = new List<string>();
        foreach (var alias in (left ?? []).Concat(right ?? []))
        {
            if (string.IsNullOrWhiteSpace(alias) ||
                merged.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            merged.Add(alias);
        }

        return merged.Count == 0 ? null : merged;
    }

    private static string Choose(string? preferred, string fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    private static ModelInfo NormalizeModel(ModelInfo model)
    {
        return model with
        {
            DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName,
            Aliases = model.Aliases?
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static ModelInfo CloneModel(ModelInfo model)
    {
        return model with
        {
            Aliases = model.Aliases?.ToList()
        };
    }
}
