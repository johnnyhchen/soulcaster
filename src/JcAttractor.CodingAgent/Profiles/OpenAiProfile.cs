using System.Text;
using System.Text.Json;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public class OpenAiProfile : IProviderProfile
{
    public string Id => "openai";
    public string Model { get; set; } = "o3";
    public ToolRegistry ToolRegistry { get; } = new();
    public bool SupportsReasoning => true;
    public bool SupportsStreaming => true;
    public bool SupportsParallelToolCalls => true;
    public int ContextWindowSize => 200000;

    public OpenAiProfile()
    {
        RegisterTools();
    }

    private void RegisterTools()
    {
        ToolRegistry.Register(new RegisteredTool(
            "read_file",
            new ToolDefinition(
                "read_file",
                "Reads a file from the filesystem. Returns content with line numbers. Supports offset and limit parameters for large files.",
                new List<ToolParameter>
                {
                    new("file_path", "string", "The absolute path to the file to read", true),
                    new("offset", "integer", "Line number to start reading from (0-based)", false),
                    new("limit", "integer", "Maximum number of lines to read", false)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var filePath = json.RootElement.GetProperty("file_path").GetString()!;
                int? offset = json.RootElement.TryGetProperty("offset", out var o) ? o.GetInt32() : null;
                int? limit = json.RootElement.TryGetProperty("limit", out var l) ? l.GetInt32() : null;
                return await env.ReadFileAsync(filePath, offset, limit);
            }));

        ToolRegistry.Register(new RegisteredTool(
            "apply_patch",
            new ToolDefinition(
                "apply_patch",
                "Applies a patch to files using the v4a unified diff format. The patch should follow standard unified diff format with --- and +++ file headers and @@ hunk headers.",
                new List<ToolParameter>
                {
                    new("patch", "string", "The patch content in v4a unified diff format", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var patch = json.RootElement.GetProperty("patch").GetString()!;
                return await ApplyPatchAsync(patch, env);
            }));

        ToolRegistry.Register(new RegisteredTool(
            "write_file",
            new ToolDefinition(
                "write_file",
                "Writes content to a file, creating or overwriting as needed. Creates parent directories automatically.",
                new List<ToolParameter>
                {
                    new("file_path", "string", "The absolute path to the file to write", true),
                    new("content", "string", "The content to write", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var filePath = json.RootElement.GetProperty("file_path").GetString()!;
                var content = json.RootElement.GetProperty("content").GetString()!;
                await env.WriteFileAsync(filePath, content);
                return $"Successfully wrote to {filePath}";
            }));

        ToolRegistry.Register(new RegisteredTool(
            "shell",
            new ToolDefinition(
                "shell",
                "Executes a shell command in the working directory. Returns stdout and stderr output.",
                new List<ToolParameter>
                {
                    new("command", "string", "The shell command to execute", true),
                    new("timeout", "integer", "Timeout in milliseconds (default 10000, max 600000)", false)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var command = json.RootElement.GetProperty("command").GetString()!;
                int? timeout = json.RootElement.TryGetProperty("timeout", out var t) ? t.GetInt32() : null;
                return await env.RunCommandAsync(command, timeout);
            }));

        ToolRegistry.Register(new RegisteredTool(
            "glob",
            new ToolDefinition(
                "glob",
                "Finds files matching a glob pattern. Returns sorted list of matching file paths.",
                new List<ToolParameter>
                {
                    new("pattern", "string", "The glob pattern to match files against", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var pattern = json.RootElement.GetProperty("pattern").GetString()!;
                var results = await env.GlobAsync(pattern);
                return results.Count > 0 ? string.Join('\n', results) : "No files found matching pattern.";
            }));

        ToolRegistry.Register(new RegisteredTool(
            "grep",
            new ToolDefinition(
                "grep",
                "Searches file contents using regex. Returns matches as 'path:line:content'.",
                new List<ToolParameter>
                {
                    new("pattern", "string", "The regex pattern to search for", true),
                    new("path", "string", "File or directory to search in (defaults to working directory)", false)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var pattern = json.RootElement.GetProperty("pattern").GetString()!;
                string? path = json.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
                var results = await env.GrepAsync(pattern, path);
                return results.Count > 0 ? string.Join('\n', results) : "No matches found.";
            }));
    }

    /// <summary>
    /// Applies a v4a unified diff patch to files via the execution environment.
    /// Parses the patch into per-file hunks and applies them.
    /// </summary>
    private static async Task<string> ApplyPatchAsync(string patch, IExecutionEnvironment env)
    {
        var results = new List<string>();
        var lines = patch.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            // Find next file header
            if (!lines[i].StartsWith("--- "))
            {
                i++;
                continue;
            }

            var oldFile = lines[i][4..].Trim();
            i++;

            if (i >= lines.Length || !lines[i].StartsWith("+++ "))
            {
                results.Add($"Error: Expected +++ line after --- line for {oldFile}");
                continue;
            }

            var newFile = lines[i][4..].Trim();
            i++;

            // Remove a/ and b/ prefixes if present
            if (oldFile.StartsWith("a/")) oldFile = oldFile[2..];
            if (newFile.StartsWith("b/")) newFile = newFile[2..];

            // Determine if this is a new file
            var isNewFile = oldFile == "/dev/null";
            var targetFile = isNewFile ? newFile : oldFile;

            // Collect all hunks for this file
            var hunks = new List<string>();
            var currentHunk = new StringBuilder();

            while (i < lines.Length && !lines[i].StartsWith("--- "))
            {
                if (lines[i].StartsWith("@@ "))
                {
                    if (currentHunk.Length > 0)
                        hunks.Add(currentHunk.ToString());
                    currentHunk = new StringBuilder();
                    currentHunk.AppendLine(lines[i]);
                }
                else if (currentHunk.Length > 0)
                {
                    currentHunk.AppendLine(lines[i]);
                }
                i++;
            }

            if (currentHunk.Length > 0)
                hunks.Add(currentHunk.ToString());

            // Apply hunks
            if (isNewFile)
            {
                var content = new StringBuilder();
                foreach (var hunk in hunks)
                {
                    foreach (var hunkLine in hunk.Split('\n'))
                    {
                        if (hunkLine.StartsWith('+') && !hunkLine.StartsWith("+++"))
                            content.AppendLine(hunkLine[1..]);
                    }
                }
                await env.WriteFileAsync(targetFile, content.ToString());
                results.Add($"Created {targetFile}");
            }
            else
            {
                // Read existing file and apply patch via shell
                var escapedPatch = patch.Replace("'", "'\"'\"'");
                var result = await env.RunCommandAsync(
                    $"echo '{escapedPatch}' | patch -p1 --forward --no-backup-if-mismatch",
                    timeoutMs: 10000);
                results.Add(result);
                break; // Let patch handle all files at once
            }
        }

        return results.Count > 0 ? string.Join('\n', results) : "Patch applied successfully.";
    }

    public string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a coding assistant powered by OpenAI. You help users with software development by reading, editing, and creating code files, running shell commands, and searching codebases.");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Working directory: {env.WorkingDirectory}");
        sb.AppendLine($"- Platform: {Environment.OSVersion.Platform}");
        sb.AppendLine($"- Date: {DateTimeOffset.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("## Tool Usage Guidelines");
        sb.AppendLine("- Use read_file to examine code before editing.");
        sb.AppendLine("- Use apply_patch with v4a unified diff format for targeted code changes.");
        sb.AppendLine("- Use write_file for creating new files or full file rewrites.");
        sb.AppendLine("- Use shell for running build tools, tests, git, and CLI operations.");
        sb.AppendLine("- Use glob to find files by name patterns.");
        sb.AppendLine("- Use grep to search file contents.");
        sb.AppendLine("- Always use absolute file paths.");
        sb.AppendLine("- Read files before editing to understand existing code.");
        sb.AppendLine();
        sb.AppendLine("## Coding Guidelines");
        sb.AppendLine("- Follow the existing code style and conventions.");
        sb.AppendLine("- Write clean, maintainable code.");
        sb.AppendLine("- Handle errors gracefully.");

        if (projectDocs is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Project Documentation");
            foreach (var doc in projectDocs)
            {
                sb.AppendLine(doc);
            }
        }

        return sb.ToString();
    }

    public IReadOnlyList<ToolDefinition> Tools() => ToolRegistry.GetDefinitions();

    public Dictionary<string, object>? ProviderOptions() => null;
}
