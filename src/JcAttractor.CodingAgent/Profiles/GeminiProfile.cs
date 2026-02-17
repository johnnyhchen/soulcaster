using System.Text;
using System.Text.Json;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public class GeminiProfile : IProviderProfile
{
    public string Id => "gemini";
    public string Model { get; set; } = "gemini-2.5-pro";
    public ToolRegistry ToolRegistry { get; } = new();
    public bool SupportsReasoning => true;
    public bool SupportsStreaming => true;
    public bool SupportsParallelToolCalls => true;
    public int ContextWindowSize => 1000000;

    public GeminiProfile()
    {
        RegisterTools();
    }

    private void RegisterTools()
    {
        ToolRegistry.Register(new RegisteredTool(
            "read_file",
            new ToolDefinition(
                "read_file",
                "Reads a file from the filesystem. Returns content with line numbers. Supports offset and limit for partial reads of large files.",
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
            "edit_file",
            new ToolDefinition(
                "edit_file",
                "Performs exact string replacement in a file. The old_string must match exactly and appear only once in the file.",
                new List<ToolParameter>
                {
                    new("file_path", "string", "The absolute path to the file to edit", true),
                    new("old_string", "string", "The exact text to replace", true),
                    new("new_string", "string", "The text to replace it with", true)
                }),
            async (args, env) =>
            {
                var json = JsonDocument.Parse(args);
                var filePath = json.RootElement.GetProperty("file_path").GetString()!;
                var oldString = json.RootElement.GetProperty("old_string").GetString()!;
                var newString = json.RootElement.GetProperty("new_string").GetString()!;
                return await env.EditFileAsync(filePath, oldString, newString);
            }));

        ToolRegistry.Register(new RegisteredTool(
            "write_file",
            new ToolDefinition(
                "write_file",
                "Writes content to a file, creating it if needed and overwriting existing content. Creates parent directories automatically.",
                new List<ToolParameter>
                {
                    new("file_path", "string", "The absolute path to the file to write", true),
                    new("content", "string", "The content to write to the file", true)
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
                "Executes a shell command in the working directory. Returns combined stdout and stderr output.",
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
                "Finds files matching a glob pattern. Returns sorted list of absolute file paths.",
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
                "Searches file contents using regex patterns. Returns matches as 'path:line:content' format.",
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

    public string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a coding assistant powered by Gemini. You help users with software development tasks including code editing, file management, and running commands.");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Working directory: {env.WorkingDirectory}");
        sb.AppendLine($"- Platform: {Environment.OSVersion.Platform}");
        sb.AppendLine($"- Date: {DateTimeOffset.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("## Tool Usage Guidelines");
        sb.AppendLine("- Use read_file to view existing files before making changes.");
        sb.AppendLine("- Use edit_file for exact string replacement edits.");
        sb.AppendLine("- Use write_file for creating new files or complete file rewrites.");
        sb.AppendLine("- Use shell for running commands, build tools, tests, and git.");
        sb.AppendLine("- Use glob to find files matching name patterns.");
        sb.AppendLine("- Use grep to search file contents with regex.");
        sb.AppendLine("- Always use absolute file paths.");
        sb.AppendLine("- Read files before editing them.");
        sb.AppendLine("- Prefer minimal, targeted edits over full file rewrites.");
        sb.AppendLine();
        sb.AppendLine("## Coding Guidelines");
        sb.AppendLine("- Match the project's existing code style and conventions.");
        sb.AppendLine("- Write clean, well-structured code.");
        sb.AppendLine("- Handle errors appropriately.");

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
