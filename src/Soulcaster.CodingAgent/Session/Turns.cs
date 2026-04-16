using Soulcaster.UnifiedLlm;

namespace Soulcaster.CodingAgent;

public interface ITurn
{
    DateTimeOffset Timestamp { get; }
}

public record UserTurn(string Content, DateTimeOffset Timestamp, List<ContentPart>? Parts = null) : ITurn
{
    public List<ImageData> Images =>
        Parts?
            .Where(p => p.Kind == ContentKind.Image && p.Image is not null)
            .Select(p => p.Image!)
            .ToList()
        ?? [];
}

public record AssistantTurn(
    string Content,
    List<ToolCallData> ToolCalls,
    string? Reasoning,
    Usage Usage,
    string? ResponseId,
    DateTimeOffset Timestamp,
    List<ThinkingData>? ThinkingParts = null,
    List<ContentPart>? Parts = null) : ITurn
{
    public List<ImageData> Images =>
        Parts?
            .Where(p => p.Kind == ContentKind.Image && p.Image is not null)
            .Select(p => p.Image!)
            .ToList()
        ?? [];
}

public record ToolResultsTurn(List<ToolResultData> Results, DateTimeOffset Timestamp) : ITurn;

public record SystemTurn(string Content, DateTimeOffset Timestamp) : ITurn;

public record SteeringTurn(string Content, DateTimeOffset Timestamp) : ITurn;
