using Soulcaster.UnifiedLlm;

namespace Soulcaster.CodingAgent.Tools;

public class ToolRegistry
{
    private readonly Dictionary<string, RegisteredTool> _tools = new();

    public void Register(RegisteredTool tool) => _tools[tool.Name] = tool;
    public void Clear() => _tools.Clear();
    public RegisteredTool? Get(string name) => _tools.GetValueOrDefault(name);
    public IReadOnlyList<ToolDefinition> GetDefinitions() => _tools.Values.Select(t => t.Definition).ToList();

    public ToolRegistry Clone()
    {
        var clone = new ToolRegistry();
        foreach (var tool in _tools.Values)
            clone.Register(tool);

        return clone;
    }
}

public record RegisteredTool(
    string Name,
    ToolDefinition Definition,
    Func<string, IExecutionEnvironment, Task<string>> Execute
);
