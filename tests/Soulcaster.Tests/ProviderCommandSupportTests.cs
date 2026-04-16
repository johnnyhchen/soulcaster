using System.Text.Json;
using Soulcaster.Runner;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.Tests;

public class ProviderCommandSupportTests
{
    [Fact]
    public void BuildSyncSelection_FiltersUnsupportedAndKnownModels()
    {
        var discovered = new[]
        {
            new ProviderModelDescriptor("openai", "text-embedding-3-small"),
            new ProviderModelDescriptor("openai", "gpt-5.2"),
            new ProviderModelDescriptor("openai", "gpt-5.4-mini"),
            new ProviderModelDescriptor("openai", "o5-preview")
        };

        var selection = ProviderCommandSupport.BuildSyncSelection("openai", discovered, maxModels: 1);

        Assert.Equal(4, selection.DiscoveredModels.Count);
        Assert.Equal(["gpt-5.2", "gpt-5.4-mini", "o5-preview"], selection.CandidateModels.Select(model => model.Id).ToArray());
        Assert.Equal(["gpt-5.4-mini"], selection.UnknownModels.Select(model => model.Id).ToArray());
    }

    [Fact]
    public void ResolveRepositoryRoot_FindsRootFromNestedDirectory()
    {
        var repoRoot = CreateTempDir("jc_repo_root_");
        var nested = Path.Combine(repoRoot, "src", "Soulcaster.UnifiedLlm", "Providers");
        Directory.CreateDirectory(Path.Combine(repoRoot, "runner"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "Soulcaster.UnifiedLlm"));
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(repoRoot, "runner", "Soulcaster.Runner.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(repoRoot, "src", "Soulcaster.UnifiedLlm", "ModelCatalog.cs"), "public static class ModelCatalog {}");

        try
        {
            var resolved = ProviderCommandSupport.ResolveRepositoryRoot(nested);
            Assert.Equal(repoRoot, resolved);
        }
        finally
        {
            Directory.Delete(repoRoot, true);
        }
    }

    [Fact]
    public void WriteSyncArtifacts_WritesManifestAndValidationDotfile()
    {
        var repoRoot = CreateTempDir("jc_provider_sync_");

        try
        {
            var selection = new ProviderSyncSelection(
                "openai",
                DiscoveredModels:
                [
                    new ProviderModelDescriptor("openai", "gpt-5.4-mini", "GPT-5.4 Mini")
                ],
                CandidateModels:
                [
                    new ProviderModelDescriptor("openai", "gpt-5.4-mini", "GPT-5.4 Mini")
                ],
                UnknownModels:
                [
                    new ProviderModelDescriptor("openai", "gpt-5.4-mini", "GPT-5.4 Mini")
                ]);

            var artifacts = ProviderCommandSupport.WriteSyncArtifacts(repoRoot, selection, "provider-sync-openai-test");

            Assert.True(File.Exists(artifacts.ManifestPath));
            Assert.True(File.Exists(artifacts.DotfilePath));

            var manifestJson = File.ReadAllText(artifacts.ManifestPath);
            var dotfile = File.ReadAllText(artifacts.DotfilePath);
            var manifest = JsonSerializer.Deserialize<ProviderSyncManifest>(manifestJson);

            Assert.NotNull(manifest);
            Assert.Equal("openai", manifest!.Provider);
            Assert.Equal(["gpt-5.4-mini"], manifest.UnknownModels.Select(model => model.Id).ToArray());
            Assert.Contains("probe_gpt_5_4_mini", dotfile);
            Assert.Contains("MODEL-PROBE-1.md", dotfile);
            Assert.Contains("validate_sync_status", dotfile);
            Assert.Contains(artifacts.ValidationReportPath, dotfile, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repoRoot, true);
        }
    }

    private static string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
