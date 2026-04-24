namespace Soulcaster.Attractor.Execution;

using System.Text.Json;

public sealed record RunCancellationRequest(
    string? Actor,
    string? Rationale,
    string? Source,
    string? RequestedAtUtc);

public sealed class RunCancelledException : Exception
{
    public RunCancelledException(RunCancellationRequest request)
        : base($"Run cancelled by operator '{request.Actor ?? "unknown"}'.")
    {
        Request = request;
    }

    public RunCancellationRequest Request { get; }
}

public static class RunControl
{
    public static string GetControlDirectory(string logsRoot) =>
        Path.Combine(logsRoot, "control");

    public static string GetCancelPath(string logsRoot) =>
        Path.Combine(GetControlDirectory(logsRoot), "cancel.json");

    public static void ThrowIfCancellationRequested(string logsRoot)
    {
        if (TryReadCancellation(logsRoot, out var request))
            throw new RunCancelledException(request!);
    }

    public static bool TryReadCancellation(string logsRoot, out RunCancellationRequest? request)
    {
        request = null;
        var cancelPath = GetCancelPath(logsRoot);
        if (!File.Exists(cancelPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cancelPath));
            var root = document.RootElement;
            request = new RunCancellationRequest(
                Actor: root.TryGetProperty("actor", out var actor) ? actor.GetString() : null,
                Rationale: root.TryGetProperty("rationale", out var rationale) ? rationale.GetString() : null,
                Source: root.TryGetProperty("source", out var source) ? source.GetString() : null,
                RequestedAtUtc: root.TryGetProperty("timestamp_utc", out var requestedAt) ? requestedAt.GetString() : null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
