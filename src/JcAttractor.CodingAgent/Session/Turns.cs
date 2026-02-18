using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public interface ITurn
{
    DateTimeOffset Timestamp { get; }
}

public record UserTurn(string Content, DateTimeOffset Timestamp) : ITurn;

public record AssistantTurn(
    string Content,
    List<ToolCallData> ToolCalls,
    string? Reasoning,
    Usage Usage,
    string? ResponseId,
    DateTimeOffset Timestamp,
    List<ThinkingData>? ThinkingParts = null) : ITurn;

public record ToolResultsTurn(List<ToolResultData> Results, DateTimeOffset Timestamp) : ITurn;

public record SystemTurn(string Content, DateTimeOffset Timestamp) : ITurn;

public record SteeringTurn(string Content, DateTimeOffset Timestamp) : ITurn;
