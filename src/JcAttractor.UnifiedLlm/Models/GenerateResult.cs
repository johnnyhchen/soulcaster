namespace JcAttractor.UnifiedLlm;

public record StepResult(
    string Text,
    string? Reasoning,
    List<ToolCallData> ToolCalls,
    List<ToolResultData> ToolResults,
    FinishReason FinishReason,
    Usage Usage,
    Response Response,
    List<Warning> Warnings);

public record GenerateResult(
    string Text,
    string? Reasoning,
    List<ToolCallData> ToolCalls,
    List<ToolResultData> ToolResults,
    FinishReason FinishReason,
    Usage Usage,
    Usage TotalUsage,
    List<StepResult> Steps,
    Response Response,
    object? Output = null);
