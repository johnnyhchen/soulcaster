namespace JcAttractor.CodingAgent;

public enum EventKind
{
    SessionStart,
    SessionEnd,
    UserInput,
    AssistantTextStart,
    AssistantTextDelta,
    AssistantTextEnd,
    ToolCallStart,
    ToolCallOutputDelta,
    ToolCallEnd,
    SteeringInjected,
    TurnLimit,
    LoopDetection,
    Error
}

public record SessionEvent(
    EventKind Kind,
    DateTimeOffset Timestamp,
    string SessionId,
    Dictionary<string, object?> Data
);
