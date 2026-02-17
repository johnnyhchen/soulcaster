namespace JcAttractor.UnifiedLlm;

public enum Role
{
    System,
    User,
    Assistant,
    Tool,
    Developer
}

public enum ContentKind
{
    Text,
    Image,
    Audio,
    Document,
    ToolCall,
    ToolResult,
    Thinking,
    RedactedThinking
}

public enum StreamEventType
{
    StreamStart,
    TextStart,
    TextDelta,
    TextEnd,
    ReasoningStart,
    ReasoningDelta,
    ReasoningEnd,
    ToolCallStart,
    ToolCallDelta,
    ToolCallEnd,
    Finish,
    Error,
    ProviderEvent
}

public enum ToolChoiceMode
{
    Auto,
    None,
    Required,
    Named
}
