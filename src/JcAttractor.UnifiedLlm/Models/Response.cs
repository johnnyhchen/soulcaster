namespace JcAttractor.UnifiedLlm;

public record FinishReason(string Reason, string? Raw = null)
{
    public static readonly FinishReason Stop = new("stop");
    public static readonly FinishReason Length = new("length");
    public static readonly FinishReason ToolCalls = new("tool_calls");
    public static readonly FinishReason ContentFilter = new("content_filter");
    public static readonly FinishReason Error = new("error");
}

public record Usage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int? ReasoningTokens = null,
    int? CacheReadTokens = null,
    int? CacheWriteTokens = null)
{
    public static Usage operator +(Usage a, Usage b) =>
        new(
            InputTokens: a.InputTokens + b.InputTokens,
            OutputTokens: a.OutputTokens + b.OutputTokens,
            TotalTokens: a.TotalTokens + b.TotalTokens,
            ReasoningTokens: (a.ReasoningTokens ?? 0) + (b.ReasoningTokens ?? 0) is var r && r > 0 ? r : null,
            CacheReadTokens: (a.CacheReadTokens ?? 0) + (b.CacheReadTokens ?? 0) is var cr && cr > 0 ? cr : null,
            CacheWriteTokens: (a.CacheWriteTokens ?? 0) + (b.CacheWriteTokens ?? 0) is var cw && cw > 0 ? cw : null);

    public static readonly Usage Empty = new(0, 0, 0);
}

public record RateLimitInfo(
    int? RequestsRemaining,
    int? RequestsLimit,
    int? TokensRemaining,
    int? TokensLimit,
    DateTimeOffset? ResetAt);

public record Warning(string Message, string? Code = null);

public record Response(
    string Id,
    string Model,
    string Provider,
    Message Message,
    FinishReason FinishReason,
    Usage Usage,
    Dictionary<string, object>? Raw = null,
    List<Warning>? Warnings = null,
    RateLimitInfo? RateLimit = null)
{
    /// <summary>
    /// Concatenated text content from the response message.
    /// </summary>
    public string Text => Message.Text;

    /// <summary>
    /// All tool calls from the response message.
    /// </summary>
    public List<ToolCallData> ToolCalls =>
        Message.Content
            .Where(p => p.Kind == ContentKind.ToolCall && p.ToolCall is not null)
            .Select(p => p.ToolCall!)
            .ToList();

    /// <summary>
    /// Concatenated reasoning/thinking text from the response message.
    /// </summary>
    public string? Reasoning
    {
        get
        {
            var parts = Message.Content
                .Where(p => p.Kind == ContentKind.Thinking && p.Thinking is not null)
                .Select(p => p.Thinking!.Text)
                .ToList();
            return parts.Count > 0 ? string.Concat(parts) : null;
        }
    }
}
