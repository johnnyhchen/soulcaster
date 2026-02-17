namespace JcAttractor.UnifiedLlm;

public record Request
{
    public required string Model { get; init; }
    public required List<Message> Messages { get; init; }
    public string? Provider { get; init; }
    public List<ToolDefinition>? Tools { get; init; }
    public ToolChoice? ToolChoice { get; init; }
    public ResponseFormat? ResponseFormat { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxTokens { get; init; }
    public List<string>? StopSequences { get; init; }
    public string? ReasoningEffort { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
    public Dictionary<string, object>? ProviderOptions { get; init; }
}
