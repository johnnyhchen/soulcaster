using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public class ToolRegistry
{
    private readonly Dictionary<string, RegisteredTool> _tools = new();

    public void Register(RegisteredTool tool) => _tools[tool.Name] = tool;
    public RegisteredTool? Get(string name) => _tools.GetValueOrDefault(name);
    public IReadOnlyList<ToolDefinition> GetDefinitions() => _tools.Values.Select(t => t.Definition).ToList();
}

public record RegisteredTool(
    string Name,
    ToolDefinition Definition,
    Func<string, IExecutionEnvironment, Task<string>> Execute
);
