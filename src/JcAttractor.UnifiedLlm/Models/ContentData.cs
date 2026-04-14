namespace JcAttractor.UnifiedLlm;

public record ImageData(
    string? Url,
    byte[]? Data,
    string? MediaType,
    string? Detail)
{
    public static ImageData FromFile(string path, string? mediaType = null, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        return new ImageData(
            Url: null,
            Data: File.ReadAllBytes(fullPath),
            MediaType: mediaType ?? InferMediaType(fullPath),
            Detail: detail);
    }

    public static ImageData FromBytes(byte[] data, string mediaType = "image/png", string? detail = null) =>
        new(Url: null, Data: data ?? throw new ArgumentNullException(nameof(data)), MediaType: mediaType, Detail: detail);

    public static ImageData FromUrl(string url, string? mediaType = null, string? detail = null) =>
        new(Url: url, Data: null, MediaType: mediaType, Detail: detail);

    private static string InferMediaType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".heic" => "image/heic",
            _ => "image/png"
        };
    }
}

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
