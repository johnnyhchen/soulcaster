using System.Text.Json.Nodes;

namespace Soulcaster.UnifiedLlm.Models;

public record ImageData(
    string? Url,
    byte[]? Data,
    string? MediaType,
    string? Detail,
    JsonObject? ProviderState = null)
{
    public static ImageData FromFile(string path, string? mediaType = null, string? detail = null, JsonObject? providerState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        return new ImageData(
            Url: null,
            Data: File.ReadAllBytes(fullPath),
            MediaType: mediaType ?? InferMediaType(fullPath),
            Detail: detail,
            ProviderState: providerState?.DeepClone() as JsonObject);
    }

    public static ImageData FromBytes(
        byte[] data,
        string mediaType = "image/png",
        string? detail = null,
        JsonObject? providerState = null) =>
        new(
            Url: null,
            Data: data ?? throw new ArgumentNullException(nameof(data)),
            MediaType: mediaType,
            Detail: detail,
            ProviderState: providerState?.DeepClone() as JsonObject);

    public static ImageData FromUrl(
        string url,
        string? mediaType = null,
        string? detail = null,
        JsonObject? providerState = null) =>
        new(
            Url: url,
            Data: null,
            MediaType: mediaType,
            Detail: detail,
            ProviderState: providerState?.DeepClone() as JsonObject);

    private static string InferMediaType(string path)
    {
        return MediaTypeInference.InferMediaType(path, "image/png");
    }
}

public record AudioData(
    string? Url,
    byte[]? Data,
    string? MediaType,
    string? FileName = null,
    JsonObject? ProviderState = null)
{
    public static AudioData FromFile(string path, string? mediaType = null, string? fileName = null, JsonObject? providerState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        return new AudioData(
            Url: null,
            Data: File.ReadAllBytes(fullPath),
            MediaType: mediaType ?? MediaTypeInference.InferMediaType(fullPath, "audio/mpeg"),
            FileName: fileName ?? Path.GetFileName(fullPath),
            ProviderState: providerState?.DeepClone() as JsonObject);
    }

    public static AudioData FromBytes(
        byte[] data,
        string mediaType = "audio/mpeg",
        string? fileName = null,
        JsonObject? providerState = null) =>
        new(
            Url: null,
            Data: data ?? throw new ArgumentNullException(nameof(data)),
            MediaType: mediaType,
            FileName: fileName,
            ProviderState: providerState?.DeepClone() as JsonObject);

    public static AudioData FromUrl(
        string url,
        string? mediaType = null,
        string? fileName = null,
        JsonObject? providerState = null) =>
        new(
            Url: url,
            Data: null,
            MediaType: mediaType,
            FileName: fileName,
            ProviderState: providerState?.DeepClone() as JsonObject);
}

public record DocumentData(
    string? Url,
    byte[]? Data,
    string? MediaType,
    string? FileName,
    JsonObject? ProviderState = null)
{
    public static DocumentData FromFile(string path, string? mediaType = null, string? fileName = null, JsonObject? providerState = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        return new DocumentData(
            Url: null,
            Data: File.ReadAllBytes(fullPath),
            MediaType: mediaType ?? MediaTypeInference.InferMediaType(fullPath, "application/octet-stream"),
            FileName: fileName ?? Path.GetFileName(fullPath),
            ProviderState: providerState?.DeepClone() as JsonObject);
    }

    public static DocumentData FromBytes(
        byte[] data,
        string mediaType = "application/octet-stream",
        string? fileName = null,
        JsonObject? providerState = null) =>
        new(
            Url: null,
            Data: data ?? throw new ArgumentNullException(nameof(data)),
            MediaType: mediaType,
            FileName: fileName,
            ProviderState: providerState?.DeepClone() as JsonObject);

    public static DocumentData FromUrl(
        string url,
        string? mediaType = null,
        string? fileName = null,
        JsonObject? providerState = null) =>
        new(
            Url: url,
            Data: null,
            MediaType: mediaType,
            FileName: fileName,
            ProviderState: providerState?.DeepClone() as JsonObject);
}

internal static class MediaProviderState
{
    public static JsonObject Create(string provider, params (string Key, JsonNode? Value)[] properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var state = new JsonObject
        {
            ["provider"] = provider
        };

        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;

            state[key] = value.DeepClone();
        }

        return state;
    }

    public static bool IsProvider(JsonObject? providerState, string provider)
    {
        if (providerState is null || string.IsNullOrWhiteSpace(provider))
            return false;

        return string.Equals(
            providerState["provider"]?.GetValue<string>(),
            provider,
            StringComparison.OrdinalIgnoreCase);
    }

    public static JsonNode? CloneValue(JsonObject? providerState, string key)
    {
        if (providerState is null || string.IsNullOrWhiteSpace(key))
            return null;

        return providerState[key]?.DeepClone();
    }

    public static string? GetString(JsonObject? providerState, string key)
    {
        if (providerState is null || string.IsNullOrWhiteSpace(key))
            return null;

        return providerState[key]?.GetValue<string>();
    }
}

internal static class MediaTypeInference
{
    public static string InferMediaType(string path, string defaultMediaType)
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
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => defaultMediaType
        };
    }
}

/// <summary>
/// Represents a tool call. Arguments is a raw JSON string.
/// </summary>
public record ToolCallData(
    string Id,
    string Name,
    string Arguments,
    string Type = "function",
    string? Signature = null);

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
