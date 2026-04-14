using JcAttractor.Attractor;

namespace JcAttractor.Runner;

public sealed record RunOptions(
    string DotFilePath,
    bool Resume,
    AutoResumePolicy AutoResumePolicy,
    string? ResumeFrom,
    string? StartAt,
    string? SteerText,
    string? SteerFilePath,
    string BackendMode,
    string? BackendScriptPath,
    string? CrashAfterStage)
{
    public bool Autoresume => AutoResumePolicy != AutoResumePolicy.Off;

    public static RunOptions Parse(string[] args)
    {
        string? dotFile = null;
        string? resumeFrom = null;
        string? startAt = null;
        string? steerText = null;
        string? steerFilePath = null;
        string? backendScriptPath = null;
        string? crashAfterStage = null;
        var backendMode = "live";
        var resume = false;
        var autoResumePolicy = AutoResumePolicy.On;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--resume":
                    resume = true;
                    break;
                case "--autoresume" when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    autoResumePolicy = RunCommandSupport.ParseAutoResumePolicy(args[++i]);
                    break;
                case "--autoresume":
                    autoResumePolicy = AutoResumePolicy.On;
                    break;
                case var policyArg when policyArg.StartsWith("--autoresume=", StringComparison.Ordinal):
                    autoResumePolicy = RunCommandSupport.ParseAutoResumePolicy(policyArg["--autoresume=".Length..]);
                    break;
                case "--autoresume-policy" when i + 1 < args.Length:
                    autoResumePolicy = RunCommandSupport.ParseAutoResumePolicy(args[++i]);
                    break;
                case "--no-autoresume":
                    autoResumePolicy = AutoResumePolicy.Off;
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
                case "--steer-file" when i + 1 < args.Length:
                    steerFilePath = args[++i];
                    break;
                case "--backend" when i + 1 < args.Length:
                    backendMode = args[++i];
                    break;
                case "--backend-script" when i + 1 < args.Length:
                    backendScriptPath = args[++i];
                    break;
                case "--crash-after-stage" when i + 1 < args.Length:
                    crashAfterStage = args[++i];
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
            AutoResumePolicy: autoResumePolicy,
            ResumeFrom: resumeFrom,
            StartAt: startAt,
            SteerText: steerText,
            SteerFilePath: steerFilePath,
            BackendMode: backendMode,
            BackendScriptPath: backendScriptPath,
            CrashAfterStage: crashAfterStage);
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

    public static AutoResumePolicy ParseAutoResumePolicy(string? rawPolicy)
    {
        return rawPolicy?.Trim().ToLowerInvariant() switch
        {
            null or "" or "on" or "true" => AutoResumePolicy.On,
            "off" or "false" => AutoResumePolicy.Off,
            "always" => AutoResumePolicy.Always,
            _ => throw new ArgumentException($"Unknown autoresume policy '{rawPolicy}'.", nameof(rawPolicy))
        };
    }
}
