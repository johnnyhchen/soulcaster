namespace JcAttractor.UnifiedLlm;

public record ToolParameter(
    string Name,
    string Type,
    string? Description,
    bool Required);

public record ToolDefinition(
    string Name,
    string Description,
    List<ToolParameter> Parameters,
    Func<string, Task<string>>? Execute = null);
