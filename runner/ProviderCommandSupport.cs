using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.Runner;

public sealed record ProviderSyncSelection(
    string Provider,
    IReadOnlyList<ProviderModelDescriptor> DiscoveredModels,
    IReadOnlyList<ProviderModelDescriptor> CandidateModels,
    IReadOnlyList<ProviderModelDescriptor> UnknownModels);

public sealed record ProviderSyncManifest(
    string Provider,
    string RepositoryRoot,
    string CatalogFile,
    string TestsProjectFile,
    string GeneratedAtUtc,
    int DiscoveredModelCount,
    int CandidateModelCount,
    IReadOnlyList<string> ExistingCatalogModelIds,
    IReadOnlyList<ProviderModelDescriptor> UnknownModels);

public sealed record ProviderSyncArtifacts(
    string Provider,
    string RunName,
    string DotfilePath,
    string ManifestPath,
    string OutputDirectory,
    string ValidationReportPath,
    string OrchestratorProvider,
    string OrchestratorModel,
    string OrchestratorReasoningEffort,
    IReadOnlyList<ProviderModelDescriptor> UnknownModels);

public static class ProviderCommandSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyDictionary<string, IProviderAdapter> CreateCompletionAdaptersFromEnv()
    {
        var adapters = new Dictionary<string, IProviderAdapter>(StringComparer.OrdinalIgnoreCase);

        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(anthropicKey))
            adapters["anthropic"] = new AnthropicAdapter(anthropicKey);

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiKey))
            adapters["openai"] = new OpenAIAdapter(openAiKey);

        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey))
            adapters["gemini"] = new GeminiAdapter(geminiKey);

        return adapters;
    }

    public static IReadOnlyDictionary<string, IProviderDiscoveryAdapter> CreateDiscoveryAdaptersFromEnv()
    {
        var adapters = new Dictionary<string, IProviderDiscoveryAdapter>(StringComparer.OrdinalIgnoreCase);

        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(anthropicKey))
            adapters["anthropic"] = new AnthropicAdapter(anthropicKey);

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiKey))
            adapters["openai"] = new OpenAIAdapter(openAiKey);

        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey))
            adapters["gemini"] = new GeminiAdapter(geminiKey);

        return adapters;
    }

    public static string NormalizeProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider name is required.", nameof(provider));

        return provider.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "anthropic",
            "openai" => "openai",
            "gemini" => "gemini",
            _ => throw new ArgumentException($"Unsupported provider '{provider}'. Expected one of: anthropic, openai, gemini.", nameof(provider))
        };
    }

    public static IReadOnlyList<ResponseModality> ParseOutputModalities(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var modalities = new List<ResponseModality>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            modalities.Add(token.ToLowerInvariant() switch
            {
                "text" => ResponseModality.Text,
                "image" => ResponseModality.Image,
                _ => throw new ArgumentException($"Unsupported output modality '{token}'. Expected one of: text, image.", nameof(raw))
            });
        }

        return modalities
            .Distinct()
            .ToList();
    }

    public static string GetImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png"
    };

    public static string ResolveRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null)
        {
            var runnerProject = Path.Combine(current.FullName, "runner", "Soulcaster.Runner.csproj");
            var modelCatalog = Path.Combine(current.FullName, "src", "Soulcaster.UnifiedLlm", "ModelCatalog.cs");
            if (File.Exists(runnerProject) && File.Exists(modelCatalog))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException($"Could not resolve the soulcaster repository root from '{startDirectory}'.");
    }

    public static ProviderSyncSelection BuildSyncSelection(
        string provider,
        IEnumerable<ProviderModelDescriptor> discoveredModels,
        int? maxModels = null,
        IEnumerable<string>? requestedModelIds = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var requested = requestedModelIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        var discovered = discoveredModels
            .Where(model => string.Equals(model.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = discovered
            .Where(IsCatalogCandidate)
            .ToList();

        if (requested.Count > 0)
        {
            var discoveredIds = candidates
                .Select(model => model.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = requested
                .Where(id => !discoveredIds.Contains(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Requested models were not discovered as sync candidates for provider '{normalizedProvider}': {string.Join(", ", missing)}");
            }

            candidates = candidates
                .Where(model => requested.Contains(model.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var unknown = candidates
            .Where(model => !ModelCatalog.IsBuiltInModel(model.Id))
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0 && maxModels is > 0)
            unknown = unknown.Take(maxModels.Value).ToList();

        return new ProviderSyncSelection(normalizedProvider, discovered, candidates, unknown);
    }

    public static ProviderSyncArtifacts WriteSyncArtifacts(
        string repositoryRoot,
        ProviderSyncSelection selection,
        string? runName = null)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var repoRoot = Path.GetFullPath(repositoryRoot);
        var normalizedProvider = NormalizeProvider(selection.Provider);
        var dotfilesDir = Path.Combine(repoRoot, "dotfiles");
        var generatedDir = Path.Combine(dotfilesDir, "generated");
        Directory.CreateDirectory(dotfilesDir);
        Directory.CreateDirectory(generatedDir);

        var sanitizedRunName = SanitizeRunName(runName ?? $"provider-sync-{normalizedProvider}");
        var dotfilePath = Path.Combine(dotfilesDir, $"{sanitizedRunName}.dot");
        var manifestPath = Path.Combine(generatedDir, $"{sanitizedRunName}.manifest.json");
        var outputDirectory = Path.Combine(dotfilesDir, "output", sanitizedRunName);
        var validationReportPath = Path.Combine(outputDirectory, "logs", "validate_sync", "VALIDATION-RUN-1.md");
        var catalogPath = Path.Combine(repoRoot, "src", "Soulcaster.UnifiedLlm", "ModelCatalog.cs");
        var testsProjectPath = Path.Combine(repoRoot, "tests", "Soulcaster.Tests", "Soulcaster.Tests.csproj");
        var orchestrator = ResolveOrchestrator(normalizedProvider);

        var manifest = new ProviderSyncManifest(
            Provider: normalizedProvider,
            RepositoryRoot: repoRoot,
            CatalogFile: catalogPath,
            TestsProjectFile: testsProjectPath,
            GeneratedAtUtc: DateTime.UtcNow.ToString("o"),
            DiscoveredModelCount: selection.DiscoveredModels.Count,
            CandidateModelCount: selection.CandidateModels.Count,
            ExistingCatalogModelIds: ModelCatalog.ListBuiltInModels(normalizedProvider)
                .SelectMany(model => new[] { model.Id }.Concat(model.Aliases ?? []))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UnknownModels: selection.UnknownModels);

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        File.WriteAllText(dotfilePath, BuildSyncDotfile(repoRoot, dotfilePath, manifestPath, testsProjectPath, sanitizedRunName, manifest, orchestrator));

        return new ProviderSyncArtifacts(
            Provider: normalizedProvider,
            RunName: sanitizedRunName,
            DotfilePath: dotfilePath,
            ManifestPath: manifestPath,
            OutputDirectory: outputDirectory,
            ValidationReportPath: validationReportPath,
            OrchestratorProvider: orchestrator.Provider,
            OrchestratorModel: orchestrator.Model,
            OrchestratorReasoningEffort: orchestrator.ReasoningEffort,
            UnknownModels: selection.UnknownModels);
    }

    public static bool IsCatalogCandidate(ProviderModelDescriptor model)
    {
        var provider = NormalizeProvider(model.Provider);
        var id = model.Id.Trim();

        return provider switch
        {
            "anthropic" => id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase),
            "openai" => id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith("codex-", StringComparison.OrdinalIgnoreCase) ||
                        Regex.IsMatch(id, "^o\\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "gemini" => id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string BuildSyncDotfile(
        string repositoryRoot,
        string dotfilePath,
        string manifestPath,
        string testsProjectPath,
        string runName,
        ProviderSyncManifest manifest,
        ProviderExecutionTarget orchestrator)
    {
        var validationReportPath = Path.Combine(repositoryRoot, "dotfiles", "output", runName, "logs", "validate_sync", "VALIDATION-RUN-1.md");
        var sb = new StringBuilder();
        sb.AppendLine("digraph attractor_run {");
        sb.AppendLine($"    goal = \"{EscapeDot($"Sync unknown {manifest.Provider} models into the Attractor catalog using provider discovery metadata and runtime validation.")}\"");
        sb.AppendLine($"    label = \"{EscapeDot($"Provider Model Sync ({manifest.Provider})")}\"");
        sb.AppendLine("    default_max_retry = 1");
        sb.AppendLine();
        sb.AppendLine("    model_stylesheet = \"");
        sb.AppendLine($"        * {{ provider = \\\"{EscapeDot(orchestrator.Provider)}\\\"; model = \\\"{EscapeDot(orchestrator.Model)}\\\"; reasoning_effort = \\\"{EscapeDot(orchestrator.ReasoningEffort)}\\\" }}");
        sb.AppendLine("    \"");
        sb.AppendLine();
        sb.AppendLine("    node [shape=box]");
        sb.AppendLine();
        sb.AppendLine("    start [shape=Mdiamond, label=\"Start\"]");
        sb.AppendLine("    exit [shape=Msquare, label=\"Exit\"]");
        sb.AppendLine();
        var previousNodeId = "start";
        foreach (var model in manifest.UnknownModels)
        {
            var probeNodeId = $"probe_{SanitizeIdentifier(model.Id)}";
            var probeArtifactPath = $"logs/model_probes/{SanitizeIdentifier(model.Id)}/MODEL-PROBE-1.md";

            sb.AppendLine($"    {probeNodeId} [");
            sb.AppendLine($"        label=\"{EscapeDot($"Probe {model.Id}")}\",");
            sb.AppendLine($"        provider=\"{EscapeDot(model.Provider)}\",");
            sb.AppendLine($"        model=\"{EscapeDot(model.Id)}\",");
            sb.AppendLine("        reasoning_effort=\"\",");
            sb.AppendLine($"        prompt=\"{EscapeDot(BuildProbePrompt(model.Provider, model.Id, probeArtifactPath))}\"");
            sb.AppendLine("    ]");
            sb.AppendLine();
            sb.AppendLine($"    {previousNodeId} -> {probeNodeId}");
            previousNodeId = probeNodeId;
        }

        sb.AppendLine("    implement_sync [");
        sb.AppendLine("        label=\"Implement catalog sync\",");
        sb.AppendLine($"        prompt=\"{EscapeDot(BuildImplementPrompt(repositoryRoot, manifestPath, manifest.UnknownModels))}\"");
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine("    test_sync [");
        sb.AppendLine("        shape=parallelogram,");
        sb.AppendLine("        label=\"Run targeted tests\",");
        sb.AppendLine($"        tool_command=\"{EscapeDot($"cd {ShellEscape(repositoryRoot)} && dotnet test {ShellEscape(testsProjectPath)}")}\"");
        sb.AppendLine("    ]");
        sb.AppendLine();

        sb.AppendLine("    validate_sync [");
        sb.AppendLine("        label=\"Validate synced models\",");
        sb.AppendLine($"        prompt=\"{EscapeDot(BuildValidatePrompt(dotfilePath, manifestPath, repositoryRoot, runName, manifest.UnknownModels))}\"");
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine("    validate_sync_status [");
        sb.AppendLine("        shape=parallelogram,");
        sb.AppendLine("        label=\"Check validation result\",");
        sb.AppendLine($"        tool_command=\"{EscapeDot($"test -f {ShellEscape(validationReportPath)} && rg -q '^STATUS: PASS$' {ShellEscape(validationReportPath)}")}\"");
        sb.AppendLine("    ]");
        sb.AppendLine();
        sb.AppendLine($"    {previousNodeId} -> implement_sync");
        sb.AppendLine("    implement_sync -> test_sync");
        sb.AppendLine("    test_sync -> validate_sync");
        sb.AppendLine("    validate_sync -> validate_sync_status");
        sb.AppendLine("    validate_sync_status -> exit");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string BuildImplementPrompt(
        string repositoryRoot,
        string manifestPath,
        IReadOnlyList<ProviderModelDescriptor> unknownModels)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Read the provider sync manifest at {manifestPath}.");
        sb.AppendLine();
        sb.AppendLine("Goal: add the unknown models from that manifest to the Attractor model catalog and regression tests.");
        sb.AppendLine();
        sb.AppendLine($"Required repo root: {repositoryRoot}");
        sb.AppendLine($"Primary source file: {Path.Combine(repositoryRoot, "src", "Soulcaster.UnifiedLlm", "ModelCatalog.cs")}");
        sb.AppendLine($"Primary test area: {Path.Combine(repositoryRoot, "tests", "Soulcaster.Tests")}");
        sb.AppendLine();
        sb.AppendLine("Read any runtime probe artifacts that already exist before you choose metadata values:");
        if (unknownModels.Count == 0)
        {
            sb.AppendLine("- No probe artifacts are expected because there are zero unknown models.");
        }
        else
        {
            foreach (var model in unknownModels)
                sb.AppendLine($"- logs/model_probes/{SanitizeIdentifier(model.Id)}/MODEL-PROBE-1.md");
        }

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Add only the models listed under unknown_models in the manifest.");
        sb.AppendLine("2. Preserve existing catalog ordering and style for that provider.");
        sb.AppendLine("3. Use provider payload values only when they are directly supported by the manifest raw JSON, parsed fields, or probe artifacts.");
        sb.AppendLine("4. If the current catalog shape cannot represent an unknown field cleanly, make the smallest defensible change needed so the synced model can be represented without inventing data.");
        sb.AppendLine("5. Add regression tests that cover ModelCatalog.GetModelInfo(id) and assert any sourced values as well as intentionally-null values.");
        sb.AppendLine("6. Do not edit markdown files.");
        sb.AppendLine();
        sb.Append("Write a concise implementation report to logs/sync_implement/SYNC-IMPLEMENT-1.md that lists the models added, files changed, and which metadata stayed null because the manifest or probes did not prove a value.");
        return sb.ToString();
    }

    private static string BuildProbePrompt(string provider, string modelId, string artifactPath)
    {
        return
            $"You are validating runtime access to provider {provider} model {modelId}.\n\n" +
            $"Write the exact file {artifactPath} containing these lines:\n" +
            $"provider={provider}\n" +
            $"model={modelId}\n" +
            "status=success\n" +
            $"summary=The attractor executed a live probe on {modelId}.\n\n" +
            "Do not modify repository files. Do not write anywhere else.";
    }

    private static string BuildValidatePrompt(
        string dotfilePath,
        string manifestPath,
        string repositoryRoot,
        string runName,
        IReadOnlyList<ProviderModelDescriptor> unknownModels)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are validating a provider model sync run.");
        sb.AppendLine();
        sb.AppendLine($"Read the manifest at {manifestPath}.");
        sb.AppendLine($"Read the generated DOT pipeline at {dotfilePath}.");
        sb.AppendLine($"Read the repo files under {repositoryRoot} that were changed for this sync.");
        sb.AppendLine($"Read tool outputs under logs/test_sync/ and the implementation report at logs/sync_implement/SYNC-IMPLEMENT-1.md.");
        sb.AppendLine("Read every probe artifact below if it exists:");
        if (unknownModels.Count == 0)
        {
            sb.AppendLine("- No probe artifacts are expected because the manifest has zero unknown models.");
        }
        else
        {
            foreach (var model in unknownModels)
                sb.AppendLine($"- logs/model_probes/{SanitizeIdentifier(model.Id)}/MODEL-PROBE-1.md");
        }

        sb.AppendLine();
        sb.AppendLine("Definition of done:");
        sb.AppendLine("1. Every model in unknown_models from the manifest exists in ModelCatalog.");
        sb.AppendLine("2. Every non-null metadata value added for those models is justified by manifest evidence.");
        sb.AppendLine("3. Unproven metadata remains null.");
        sb.AppendLine("4. dotnet test passed in logs/test_sync/status.json with exit_code 0.");
        sb.AppendLine("5. Every expected probe artifact exists and represents a successful attractor call to that configured model.");
        sb.AppendLine();
        sb.AppendLine("Write logs/validate_sync/VALIDATION-RUN-1.md.");
        sb.AppendLine("The first line must be exactly one of:");
        sb.AppendLine("STATUS: PASS");
        sb.AppendLine("STATUS: FAIL");
        sb.AppendLine();
        sb.AppendLine("Then include:");
        sb.AppendLine("- Definition of done");
        sb.AppendLine($"- Run name: {runName}");
        sb.AppendLine("- Discovered evidence summary");
        sb.AppendLine("- Per-model validation findings");
        sb.AppendLine("- Final verdict");
        sb.AppendLine();
        sb.AppendLine("After writing the report, respond with valid stage status JSON.");
        sb.AppendLine("Use status=success only when the report begins with STATUS: PASS. Otherwise use status=fail.");
        return sb.ToString();
    }

    private static ProviderExecutionTarget ResolveOrchestrator(string provider)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            return new ProviderExecutionTarget("openai", "codex-5.3", "high");

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return new ProviderExecutionTarget("anthropic", "claude-sonnet-4-6", "high");

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
            return new ProviderExecutionTarget("gemini", "gemini-3.0-pro-preview", "high");

        return provider switch
        {
            "anthropic" => new ProviderExecutionTarget("anthropic", "claude-sonnet-4-6", "high"),
            "openai" => new ProviderExecutionTarget("openai", "codex-5.3", "high"),
            "gemini" => new ProviderExecutionTarget("gemini", "gemini-3.0-pro-preview", "high"),
            _ => throw new InvalidOperationException($"Unsupported provider '{provider}'.")
        };
    }

    private static string EscapeDot(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string SanitizeRunName(string runName)
    {
        var sanitized = Regex.Replace(runName.Trim(), "[^A-Za-z0-9._-]+", "-", RegexOptions.CultureInvariant);
        return string.IsNullOrWhiteSpace(sanitized) ? "provider-sync" : sanitized;
    }

    private static string SanitizeIdentifier(string value)
    {
        var sanitized = Regex.Replace(value, "[^A-Za-z0-9_]+", "_", RegexOptions.CultureInvariant).Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "model";
        if (char.IsDigit(sanitized[0]))
            sanitized = $"m_{sanitized}";
        return sanitized.ToLowerInvariant();
    }

    private static string ShellEscape(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private sealed record ProviderExecutionTarget(string Provider, string Model, string ReasoningEffort);
}
