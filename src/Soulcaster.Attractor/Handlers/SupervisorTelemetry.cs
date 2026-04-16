namespace Soulcaster.Attractor.Handlers;

using System.Text.Json;

internal sealed record SupervisorTelemetrySnapshot(
    long StageStartCount,
    long StageEndCount,
    long SuccessCount,
    long PartialSuccessCount,
    long RetryCount,
    long FailCount,
    long ToolCalls,
    long ToolErrors,
    long TouchedFilesCount,
    long TotalTokens,
    string LastNodeId,
    string LastCompletedNode,
    string SourcePath,
    bool MissingSource)
{
    public bool HasSignals =>
        StageStartCount > 0 ||
        StageEndCount > 0 ||
        ToolCalls > 0 ||
        TouchedFilesCount > 0 ||
        TotalTokens > 0;

    public long ProgressScore =>
        (StageEndCount * 1_000) +
        (TouchedFilesCount * 50) +
        (ToolCalls * 10) +
        Math.Max(0, TotalTokens / 1_000);

    public bool HasProgressSince(SupervisorTelemetrySnapshot? previous)
    {
        if (previous is null)
            return HasSignals;

        return StageEndCount > previous.StageEndCount ||
            ToolCalls > previous.ToolCalls ||
            TouchedFilesCount > previous.TouchedFilesCount ||
            TotalTokens > previous.TotalTokens;
    }
}

internal static class SupervisorTelemetry
{
    public static SupervisorTelemetrySnapshot Read(string telemetryPath)
    {
        if (!File.Exists(telemetryPath))
        {
            return new SupervisorTelemetrySnapshot(
                StageStartCount: 0,
                StageEndCount: 0,
                SuccessCount: 0,
                PartialSuccessCount: 0,
                RetryCount: 0,
                FailCount: 0,
                ToolCalls: 0,
                ToolErrors: 0,
                TouchedFilesCount: 0,
                TotalTokens: 0,
                LastNodeId: string.Empty,
                LastCompletedNode: string.Empty,
                SourcePath: telemetryPath,
                MissingSource: true);
        }

        long stageStarts = 0;
        long stageEnds = 0;
        long success = 0;
        long partial = 0;
        long retry = 0;
        long fail = 0;
        long toolCalls = 0;
        long toolErrors = 0;
        long touchedFiles = 0;
        long totalTokens = 0;
        string lastNodeId = string.Empty;
        string lastCompletedNode = string.Empty;

        foreach (var line in File.ReadLines(telemetryPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = root.TryGetProperty("event_type", out var eventTypeElement)
                    ? eventTypeElement.GetString() ?? string.Empty
                    : string.Empty;
                var nodeId = root.TryGetProperty("node_id", out var nodeIdElement)
                    ? nodeIdElement.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(nodeId))
                    lastNodeId = nodeId;

                switch (eventType)
                {
                    case "stage_start":
                        stageStarts++;
                        break;
                    case "stage_end":
                        stageEnds++;
                        lastCompletedNode = nodeId;
                        var status = root.TryGetProperty("status", out var statusElement)
                            ? statusElement.GetString() ?? string.Empty
                            : string.Empty;
                        switch (status)
                        {
                            case "success":
                                success++;
                                break;
                            case "partial_success":
                                partial++;
                                break;
                            case "retry":
                                retry++;
                                break;
                            case "fail":
                                fail++;
                                break;
                        }
                        break;
                    case "stage_retry":
                        retry++;
                        break;
                }

                toolCalls += ReadLong(root, "tool_calls");
                toolErrors += ReadLong(root, "tool_errors");
                touchedFiles += ReadLong(root, "touched_files_count");
                totalTokens += ReadLong(root, "total_tokens");
            }
            catch
            {
                // Ignore malformed lines. Supervision should be best effort.
            }
        }

        return new SupervisorTelemetrySnapshot(
            StageStartCount: stageStarts,
            StageEndCount: stageEnds,
            SuccessCount: success,
            PartialSuccessCount: partial,
            RetryCount: retry,
            FailCount: fail,
            ToolCalls: toolCalls,
            ToolErrors: toolErrors,
            TouchedFilesCount: touchedFiles,
            TotalTokens: totalTokens,
            LastNodeId: lastNodeId,
            LastCompletedNode: lastCompletedNode,
            SourcePath: telemetryPath,
            MissingSource: false);
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }
}
