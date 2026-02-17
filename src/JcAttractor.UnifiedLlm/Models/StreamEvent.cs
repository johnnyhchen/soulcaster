namespace JcAttractor.UnifiedLlm;

public record StreamEvent
{
    public required StreamEventType Type { get; init; }
    public string? Delta { get; init; }
    public string? TextId { get; init; }
    public string? ReasoningDelta { get; init; }
    public ToolCallData? ToolCall { get; init; }
    public FinishReason? FinishReason { get; init; }
    public Usage? Usage { get; init; }
    public Response? Response { get; init; }
    public Exception? Error { get; init; }
    public Dictionary<string, object>? Raw { get; init; }
}
