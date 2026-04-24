using Soulcaster.UnifiedLlm;

namespace Soulcaster.CodingAgent;

public sealed record SessionRequestOptions(
    ToolChoice? ToolChoice = null,
    IReadOnlyList<ResponseModality>? OutputModalities = null,
    bool ExecuteToolCalls = true);
