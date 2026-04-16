using System.Text;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.CodingAgent.Profiles;

public sealed class ScriptedProfile : IProviderProfile
{
    public ScriptedProfile(string id = "scripted", string model = "scripted-model")
    {
        Id = string.IsNullOrWhiteSpace(id) ? "scripted" : id;
        Model = string.IsNullOrWhiteSpace(model) ? "scripted-model" : model;
    }

    public string Id { get; }
    public string Model { get; set; }
    public ToolRegistry ToolRegistry { get; } = new();
    public bool SupportsReasoning => true;
    public bool SupportsStreaming => false;
    public bool SupportsParallelToolCalls => false;
    public int ContextWindowSize => 1_000_000;

    public string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a deterministic scripted Soulcaster test profile.");
        sb.AppendLine($"Working directory: {env.WorkingDirectory}");
        if (projectDocs is { Count: > 0 })
        {
            sb.AppendLine("Project docs:");
            foreach (var doc in projectDocs)
                sb.AppendLine(doc);
        }

        return sb.ToString();
    }

    public IReadOnlyList<ToolDefinition> Tools() => Array.Empty<ToolDefinition>();

    public Dictionary<string, object>? ProviderOptions() => null;
}
