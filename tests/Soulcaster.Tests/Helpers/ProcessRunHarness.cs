using System.Diagnostics;
using System.Text.Json;
using Soulcaster.Runner;

namespace Soulcaster.Tests.Helpers;

internal static class ProcessRunHarness
{
    private static readonly JsonSerializerOptions PlanJson = new() { WriteIndented = true };

    public static string RepoRoot { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string RunnerProjectPath { get; } =
        Path.Combine(RepoRoot, "runner", "Soulcaster.Runner.csproj");

    public static string DotfilesRoot { get; } =
        Path.Combine(RepoRoot, "dotfiles");

    public static ProcessRunWorkspace CreateWorkspace(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"jc_process_{name}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new ProcessRunWorkspace(root);
    }

    public static string WritePlan(ProcessRunWorkspace workspace, string fileName, ScriptedBackendPlan plan)
    {
        var path = Path.Combine(workspace.Root, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(plan, PlanJson));
        return path;
    }

    public static async Task<ProcessRunResult> RunRunnerAsync(
        string[] runnerArgs,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? completionTimeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(RunnerProjectPath);
        psi.ArgumentList.Add("--");
        foreach (var arg in runnerArgs)
            psi.ArgumentList.Add(arg);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var resultPath = Path.Combine(workingDirectory, "logs", "result.json");
        if (completionTimeout is not null)
            await WaitForLogicalCompletionAsync(resultPath, completionTimeout.Value, ct);

        return new ProcessRunResult(
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            WorkingDirectory: workingDirectory);
    }

    public static async Task<ProcessRunResult> RunInteractiveAsync(
        string dotFilePath,
        string commandScript,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(RunnerProjectPath);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("interactive");
        psi.ArgumentList.Add(dotFilePath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.StandardInput.WriteAsync(commandScript);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new ProcessRunResult(
            ExitCode: process.ExitCode,
            Stdout: await stdoutTask,
            Stderr: await stderrTask,
            WorkingDirectory: Path.Combine(Path.GetDirectoryName(dotFilePath)!, "output", Path.GetFileNameWithoutExtension(dotFilePath)));
    }

    public static async Task WaitForLogicalCompletionAsync(string resultPath, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
                return;

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Timed out waiting for '{resultPath}'.");
    }

    public static async Task<string> WaitForPendingGateAsync(string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var pendingPath = Path.Combine(workingDirectory, "gates", "pending");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(pendingPath))
            {
                var gateId = (await File.ReadAllTextAsync(pendingPath, ct)).Trim();
                if (!string.IsNullOrWhiteSpace(gateId))
                    return gateId;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Timed out waiting for pending gate in '{workingDirectory}'.");
    }

    public static async Task WaitForResultStatusAsync(string resultPath, string expectedStatus, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, ct));
                    var status = document.RootElement.TryGetProperty("status", out var statusElement)
                        ? statusElement.GetString()
                        : null;
                    if (string.Equals(status, expectedStatus, StringComparison.OrdinalIgnoreCase))
                        return;
                }
                catch
                {
                    // Keep polling until the file is stable.
                }
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Timed out waiting for result status '{expectedStatus}' in '{resultPath}'.");
    }

    public static Dictionary<string, object?> ReadJsonObject(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return ConvertObject(doc.RootElement);
    }

    public static List<Dictionary<string, object?>> ReadEvents(string eventsPath)
    {
        var events = new List<Dictionary<string, object?>>();
        if (!File.Exists(eventsPath))
            return events;

        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            events.Add(ConvertObject(doc.RootElement));
        }

        return events;
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            dict[property.Name] = ConvertValue(property.Value);
        return dict;
    }

    private static object? ConvertValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(value),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToList(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var i64) => i64,
            JsonValueKind.Number when value.TryGetDouble(out var dbl) => dbl,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }
}

internal sealed class ProcessRunWorkspace : IDisposable
{
    public ProcessRunWorkspace(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string WorkingDir(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

internal sealed record ProcessRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    string WorkingDirectory)
{
    public string LogsDir => Path.Combine(WorkingDirectory, "logs");
    public string ManifestPath => Path.Combine(WorkingDirectory, "run-manifest.json");
    public string ResultPath => Path.Combine(LogsDir, "result.json");
    public string CheckpointPath => Path.Combine(LogsDir, "checkpoint.json");
    public string EventsPath => Path.Combine(LogsDir, "events.jsonl");
}
