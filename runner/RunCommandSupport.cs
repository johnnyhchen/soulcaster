using JcAttractor.Attractor;

namespace JcAttractor.Runner;

public sealed record RunOptions(
    string DotFilePath,
    bool Resume,
    bool Autoresume,
    string? ResumeFrom,
    string? StartAt,
    string? SteerText)
{
    public static RunOptions Parse(string[] args)
    {
        string? dotFile = null;
        string? resumeFrom = null;
        string? startAt = null;
        string? steerText = null;
        var resume = false;
        var autoresume = true;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--resume":
                    resume = true;
                    break;
                case "--autoresume":
                    autoresume = true;
                    break;
                case "--no-autoresume":
                    autoresume = false;
                    break;
                case "--resume-from" when i + 1 < args.Length:
                    resumeFrom = args[++i];
                    break;
                case "--start-at" when i + 1 < args.Length:
                    startAt = args[++i];
                    break;
                case "--steer-text" when i + 1 < args.Length:
                    steerText = args[++i];
                    break;
                default:
                    if (!arg.StartsWith("--", StringComparison.Ordinal))
                        dotFile ??= arg;
                    break;
            }
        }

        return new RunOptions(
            DotFilePath: dotFile ?? string.Empty,
            Resume: resume,
            Autoresume: autoresume,
            ResumeFrom: resumeFrom,
            StartAt: startAt,
            SteerText: steerText);
    }
}

public static class RunCommandSupport
{
    public static string ResolveWorkingDirectory(string dotFilePath, RunOptions options)
    {
        return string.IsNullOrWhiteSpace(options.ResumeFrom)
            ? Path.Combine(Path.GetDirectoryName(dotFilePath)!, "output", Path.GetFileNameWithoutExtension(dotFilePath))
            : Path.GetFullPath(options.ResumeFrom);
    }

    public static bool TryApplyStartAt(Graph graph, string logsDir, string? startAt, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(startAt))
            return true;

        if (!graph.Nodes.ContainsKey(startAt))
        {
            error = $"start-at node not found: {startAt}";
            return false;
        }

        var existing = Checkpoint.Load(logsDir);
        var remapped = existing is null
            ? new Checkpoint(
                CurrentNodeId: startAt,
                CompletedNodes: new List<string>(),
                ContextData: new Dictionary<string, string>(),
                RetryCounts: new Dictionary<string, int>())
            : existing with { CurrentNodeId = startAt };

        remapped.Save(logsDir);
        return true;
    }
}
