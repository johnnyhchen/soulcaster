namespace Soulcaster.Attractor.Execution;

using System.Text.Json;

public static class WorkflowEventLog
{
    public static async Task AppendAsync(
        string logsRoot,
        string eventType,
        string? nodeId = null,
        Dictionary<string, object?>? data = null,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(logsRoot);

            var payload = data is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(data, StringComparer.Ordinal);

            payload["event_type"] = eventType;
            if (!string.IsNullOrWhiteSpace(nodeId))
                payload["node_id"] = nodeId;
            payload["timestamp_utc"] = DateTimeOffset.UtcNow.ToString("o");

            var path = Path.Combine(logsRoot, "events.jsonl");
            var line = JsonSerializer.Serialize(payload);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
        }
        catch
        {
            // Workflow telemetry must never fail the run.
        }
    }

    public static string? TryResolveLogsRootFromGatesDir(string gatesDir)
    {
        if (string.IsNullOrWhiteSpace(gatesDir))
            return null;

        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(gatesDir));
            if (string.IsNullOrWhiteSpace(parent))
                return null;

            var logsRoot = Path.Combine(parent, "logs");
            return Directory.Exists(logsRoot) ? logsRoot : null;
        }
        catch
        {
            return null;
        }
    }
}
