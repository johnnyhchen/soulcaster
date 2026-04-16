namespace Soulcaster.UnifiedLlm.Models;

public record ToolParameter(
    string Name,
    string Type,
    string? Description,
    bool Required,
    string? ItemsType = null);

public record ToolDefinition(
    string Name,
    string Description,
    List<ToolParameter> Parameters,
    Func<string, Task<string>>? Execute = null);
