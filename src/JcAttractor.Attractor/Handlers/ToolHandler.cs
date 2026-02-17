namespace JcAttractor.Attractor;

using System.Diagnostics;
using System.Text.Json;

public class ToolHandler : INodeHandler
{
    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        string command = node.RawAttributes.GetValueOrDefault("command", "");
        string tool = node.RawAttributes.GetValueOrDefault("tool", "");

        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(tool))
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Tool node '{node.Id}' has no command or tool attribute.");
        }

        string executable = !string.IsNullOrWhiteSpace(command) ? command : tool;

        // Expand variables in command
        executable = executable.Replace("$goal", graph.Goal);
        foreach (var (key, value) in context.All)
        {
            executable = executable.Replace($"${{context.{key}}}", value);
        }

        // Create stage directory
        string stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);

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

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

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
        catch (Exception ex)
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Tool node '{node.Id}' failed: {ex.Message}");
        }
    }
}
