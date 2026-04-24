namespace Soulcaster.UnifiedLlm.Models;

public record ContentPart(
    ContentKind Kind,
    string? Text = null,
    ImageData? Image = null,
    AudioData? Audio = null,
    DocumentData? Document = null,
    ToolCallData? ToolCall = null,
    ToolResultData? ToolResult = null,
    ThinkingData? Thinking = null,
    string? Signature = null)
{
    public static ContentPart TextPart(string text, string? signature = null) =>
        new(ContentKind.Text, Text: text, Signature: signature);

    public static ContentPart ImagePart(ImageData image) =>
        new(ContentKind.Image, Image: image);

    public static ContentPart AudioPart(AudioData audio) =>
        new(ContentKind.Audio, Audio: audio);

    public static ContentPart DocumentPart(DocumentData document) =>
        new(ContentKind.Document, Document: document);

    public static ContentPart ToolCallPart(ToolCallData toolCall) =>
        new(ContentKind.ToolCall, ToolCall: toolCall);

    public static ContentPart ToolResultPart(ToolResultData toolResult) =>
        new(ContentKind.ToolResult, ToolResult: toolResult);

    public static ContentPart ThinkingPart(ThinkingData thinking) =>
        new(thinking.Redacted ? ContentKind.RedactedThinking : ContentKind.Thinking, Thinking: thinking);
}
