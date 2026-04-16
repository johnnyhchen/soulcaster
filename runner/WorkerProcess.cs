using System.Diagnostics;

namespace Soulcaster.Runner;

internal sealed class WorkerProcess
{
    private readonly string _dotFilePath;
    private readonly string _workingDir;
    private readonly string _steerPath;
    private readonly RunOptions _parentOptions;
    private readonly IReadOnlyDictionary<string, string> _environmentOverrides;
    private Process? _process;

    public WorkerProcess(
        string dotFilePath,
        string workingDir,
        string steerPath,
        RunOptions parentOptions,
        IReadOnlyDictionary<string, string> environmentOverrides)
    {
        _dotFilePath = dotFilePath;
        _workingDir = workingDir;
        _steerPath = steerPath;
        _parentOptions = parentOptions;
        _environmentOverrides = environmentOverrides;
    }

    public Task EnsureStartedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_process is not null && !_process.HasExited)
            return Task.CompletedTask;

        var startInfo = BuildStartInfo();
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start worker process for '{_dotFilePath}'.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }
        }

        return Task.CompletedTask;
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = Process.GetCurrentProcess().MainModule?.FileName;

        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Could not resolve the current runner executable.");

        var entryPoint = Environment.GetCommandLineArgs().FirstOrDefault() ?? processPath;
        var childArguments = BuildChildArguments();
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

        foreach (var (key, value) in _environmentOverrides)
            startInfo.Environment[key] = value;

        return startInfo;
    }

    private string BuildChildArguments()
    {
        var args = new List<string>
        {
            "run",
            Quote(_dotFilePath),
            "--resume-from",
            Quote(_workingDir),
            "--autoresume-policy",
            "on",
            "--steer-file",
            Quote(_steerPath)
        };

        if (!string.Equals(_parentOptions.BackendMode, "live", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--backend");
            args.Add(Quote(_parentOptions.BackendMode));
        }

        if (!string.IsNullOrWhiteSpace(_parentOptions.BackendScriptPath))
        {
            args.Add("--backend-script");
            args.Add(Quote(_parentOptions.BackendScriptPath));
        }

        return string.Join(' ', args);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
