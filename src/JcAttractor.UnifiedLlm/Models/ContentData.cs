namespace JcAttractor.UnifiedLlm;

public record ImageData(
    string? Url,
    byte[]? Data,
    string? MediaType,
    string? Detail);

public record AudioData(
    string? Url,
    byte[]? Data,
    string? MediaType);

public record DocumentData(
    string? Url,
    byte[]? Data,
    string? MediaType,
    string? FileName);

/// <summary>
/// Represents a tool call. Arguments is a raw JSON string.
/// </summary>
public record ToolCallData(
    string Id,
    string Name,
    string Arguments,
    string Type = "function");

public record ToolResultData(
    string ToolCallId,
    string Content,
    bool IsError,
    byte[]? ImageData = null,
    string? ImageMediaType = null);

public record ThinkingData(
    string Text,
    string? Signature,
    bool Redacted);
