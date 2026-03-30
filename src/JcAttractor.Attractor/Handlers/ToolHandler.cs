namespace JcAttractor.Attractor;

using System.Diagnostics;
using System.Text.Json;

public class ToolHandler : INodeHandler
{
    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Spec §4.10: primary attribute is tool_command, with command and tool as fallbacks
        string toolCommand = node.RawAttributes.GetValueOrDefault("tool_command", "");
        string command = node.RawAttributes.GetValueOrDefault("command", "");
        string tool = node.RawAttributes.GetValueOrDefault("tool", "");

        if (string.IsNullOrWhiteSpace(toolCommand) && string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(tool))
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Tool node '{node.Id}' has no tool_command, command, or tool attribute.");
        }

        string executable = !string.IsNullOrWhiteSpace(toolCommand) ? toolCommand
            : !string.IsNullOrWhiteSpace(command) ? command : tool;

        // Expand variables in command
        executable = executable.Replace("$goal", graph.Goal);
        foreach (var (key, value) in context.All)
        {
            executable = executable.Replace($"${{context.{key}}}", value);
        }

        // Create stage directory
        string stageDir = RuntimeStageResolver.ResolveStageDir(logsRoot, context, node.Id);
        Directory.CreateDirectory(stageDir);

        // Determine effective cancellation token (with timeout if specified)
        int? timeoutMs = node.RawAttributes.TryGetValue("timeout", out var timeoutStr) && int.TryParse(timeoutStr, out var t) ? t : null;
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        var effectiveCt = ct;

        if (timeoutMs.HasValue)
        {
            timeoutCts = new CancellationTokenSource(timeoutMs.Value);
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            effectiveCt = linkedCts.Token;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? $"/c {executable}" : $"-c \"{executable.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Strip sensitive environment variables from the process
            var sensitivePatterns = new[] { "_API_KEY", "_SECRET", "_TOKEN", "_PASSWORD" };
            foreach (var envKey in Environment.GetEnvironmentVariables().Keys.Cast<string>())
            {
                if (sensitivePatterns.Any(p => envKey.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
                    psi.Environment[envKey] = "";
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(effectiveCt);
            var stderr = await process.StandardError.ReadToEndAsync(effectiveCt);
            await process.WaitForExitAsync(effectiveCt);

            int exitCode = process.ExitCode;
            var status = exitCode == 0 ? OutcomeStatus.Success : OutcomeStatus.Fail;

            // Write output files
            await File.WriteAllTextAsync(Path.Combine(stageDir, "stdout.txt"), stdout, ct);
            await File.WriteAllTextAsync(Path.Combine(stageDir, "stderr.txt"), stderr, ct);

            var statusData = new Dictionary<string, object?>
            {
                ["node_id"] = node.Id,
                ["command"] = executable,
                ["exit_code"] = exitCode,
                ["status"] = status.ToString().ToLowerInvariant()
            };
            await File.WriteAllTextAsync(
                Path.Combine(stageDir, "status.json"),
                JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
                ct
            );

            var contextUpdates = new Dictionary<string, string>
            {
                [$"{node.Id}.stdout"] = stdout,
                [$"{node.Id}.exit_code"] = exitCode.ToString()
            };

            return new Outcome(
                Status: status,
                ContextUpdates: contextUpdates,
                Notes: $"Tool node '{node.Id}' exited with code {exitCode}."
            );
        }
        catch (OperationCanceledException) when (timeoutCts is not null && !ct.IsCancellationRequested)
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Tool node '{node.Id}' timed out after {timeoutMs}ms.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Tool node '{node.Id}' failed: {ex.Message}");
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
    }
}
