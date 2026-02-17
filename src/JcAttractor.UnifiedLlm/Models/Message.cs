namespace JcAttractor.UnifiedLlm;

public record Message(
    Role Role,
    List<ContentPart> Content,
    string? Name = null,
    string? ToolCallId = null)
{
    /// <summary>
    /// Concatenates all text parts in the message content.
    /// </summary>
    public string Text =>
        string.Concat(Content
            .Where(p => p.Kind == ContentKind.Text && p.Text is not null)
            .Select(p => p.Text));

    public static Message SystemMsg(string text) =>
        new(Role.System, [ContentPart.TextPart(text)]);

    public static Message UserMsg(string text) =>
        new(Role.User, [ContentPart.TextPart(text)]);

    public static Message AssistantMsg(string text) =>
        new(Role.Assistant, [ContentPart.TextPart(text)]);

    public static Message ToolResultMsg(string toolCallId, string content, bool isError = false) =>
        new(Role.Tool,
            [ContentPart.ToolResultPart(new ToolResultData(toolCallId, content, isError))],
            ToolCallId: toolCallId);
}
