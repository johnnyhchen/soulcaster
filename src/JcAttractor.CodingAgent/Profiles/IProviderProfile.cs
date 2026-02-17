using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public interface IProviderProfile
{
    string Id { get; }
    string Model { get; set; }
    ToolRegistry ToolRegistry { get; }
    bool SupportsReasoning { get; }
    bool SupportsStreaming { get; }
    bool SupportsParallelToolCalls { get; }
    int ContextWindowSize { get; }
    string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null);
    IReadOnlyList<ToolDefinition> Tools();
    Dictionary<string, object>? ProviderOptions();
}
