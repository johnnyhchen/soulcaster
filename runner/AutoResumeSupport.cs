using System.Diagnostics;

namespace Soulcaster.Runner;

public sealed record ResumeDecision(
    bool ShouldResume,
    string ResumeSource,
    RunManifest? ExistingManifest);

public static class AutoResumeSupport
{
    public static ResumeDecision DecideResume(
        RunOptions options,
        string checkpointPath,
        string manifestPath)
    {
        var manifest = RunManifest.Load(manifestPath);
        var checkpointExists = File.Exists(checkpointPath);

        if (options.Resume)
            return new ResumeDecision(ShouldResume: checkpointExists, ResumeSource: "manual", ExistingManifest: manifest);

        if (options.AutoResumePolicy == AutoResumePolicy.Off || !checkpointExists)
            return new ResumeDecision(ShouldResume: false, ResumeSource: "fresh", ExistingManifest: manifest);

        if (manifest is null)
            return new ResumeDecision(ShouldResume: true, ResumeSource: "checkpoint", ExistingManifest: null);

        return new ResumeDecision(
            ShouldResume: !string.Equals(manifest.status, "completed", StringComparison.OrdinalIgnoreCase),
            ResumeSource: options.AutoResumePolicy == AutoResumePolicy.Always ? "policy:always" : "policy:on",
            ExistingManifest: manifest);
    }

    public static string FormatPolicy(AutoResumePolicy policy)
    {
        return policy switch
        {
            AutoResumePolicy.Off => "off",
            AutoResumePolicy.Always => "always",
            _ => "on"
        };
    }

    public static bool TrySpawnResumeProcess(
        RunOptions options,
        string dotFilePath,
        string workingDir,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        out Process? process,
        out string error)
    {
        error = string.Empty;
        process = null;

        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                processPath = Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(processPath))
            {
                error = "Could not resolve the current runner executable.";
                return false;
            }

            var entryPoint = Environment.GetCommandLineArgs().FirstOrDefault() ?? processPath;
            var childArguments = BuildRespawnArguments(options, dotFilePath, workingDir);

            ProcessStartInfo startInfo;
            if (entryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"{Quote(entryPoint)} {childArguments}",
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = processPath,
                    Arguments = childArguments,
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
            }

            foreach (var (key, value) in environmentOverrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
                startInfo.Environment[key] = value;

            process = Process.Start(startInfo);
            if (process is null)
            {
                error = "Process.Start returned null.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            process = null;
            return false;
        }
    }

    private static string BuildRespawnArguments(RunOptions options, string dotFilePath, string workingDir)
    {
        var args = new List<string>
        {
            "run",
            Quote(dotFilePath),
            "--resume-from",
            Quote(workingDir),
            "--resume",
            "--autoresume-policy",
            "always"
        };

        if (!string.IsNullOrWhiteSpace(options.SteerText))
        {
            args.Add("--steer-text");
            args.Add(Quote(options.SteerText));
        }

        if (!string.IsNullOrWhiteSpace(options.SteerFilePath))
        {
            args.Add("--steer-file");
            args.Add(Quote(options.SteerFilePath));
        }

        if (!string.Equals(options.BackendMode, "live", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--backend");
            args.Add(Quote(options.BackendMode));
        }

        if (!string.IsNullOrWhiteSpace(options.BackendScriptPath))
        {
            args.Add("--backend-script");
            args.Add(Quote(options.BackendScriptPath));
        }

        return string.Join(' ', args);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
