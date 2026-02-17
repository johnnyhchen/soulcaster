namespace JcAttractor.UnifiedLlm;

public record ToolChoice(ToolChoiceMode Mode, string? ToolName = null)
{
    public static readonly ToolChoice Auto = new(ToolChoiceMode.Auto);
    public static readonly ToolChoice NoneChoice = new(ToolChoiceMode.None);
    public static readonly ToolChoice Required = new(ToolChoiceMode.Required);

    public static ToolChoice Named(string name) =>
        new(ToolChoiceMode.Named, name);
}
