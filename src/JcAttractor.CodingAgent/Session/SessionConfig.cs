namespace JcAttractor.CodingAgent;

public record SessionConfig(
    int MaxTurns = 0,
    int MaxToolRoundsPerInput = 0,
    int DefaultCommandTimeoutMs = 10000,
    int MaxCommandTimeoutMs = 600000,
    string? ReasoningEffort = null,
    Dictionary<string, int>? ToolOutputLimits = null,
    bool EnableLoopDetection = true,
    int LoopDetectionWindow = 10,
    int MaxSubagentDepth = 1
);
