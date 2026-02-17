using System.Text;
using System.Text.Json;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.CodingAgent;

public class AnthropicProfile : IProviderProfile
{
    public string Id => "anthropic";
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    public ToolRegistry ToolRegistry { get; } = new();
    public bool SupportsReasoning => true;
    public bool SupportsStreaming => true;
    public bool SupportsParallelToolCalls => true;
    public int ContextWindowSize => 200000;

    public AnthropicProfile()
    {
        RegisterTools();
    }

    private void RegisterTools()
    {
        ToolRegistry.Register(new RegisteredTool(
            "read_file",
            new ToolDefinition(
                "read_file",
                "Reads a file from the filesystem. Returns content with line numbers in 'NNN | content' format. Supports optional offset and limit for reading portions of large files.",
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
                "Performs exact string replacement in a file. The old_string must appear exactly once in the file. Provide enough context to make the match unique.",
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
                "Writes content to a file, creating it if it doesn't exist and overwriting if it does. Creates parent directories as needed.",
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
            "bash",
            new ToolDefinition(
                "bash",
                "Executes a bash command in the working directory. Returns stdout and stderr. Use for git, npm, build tools, etc.",
                new List<ToolParameter>
                {
                    new("command", "string", "The bash command to execute", true),
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
                "Finds files matching a glob pattern. Supports patterns like '**/*.cs' or 'src/**/*.ts'. Returns sorted list of matching file paths.",
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
                "Searches file contents using regex patterns. Returns matching lines with file paths and line numbers in 'path:line:content' format.",
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

        sb.AppendLine("You are an expert coding assistant powered by Claude. You help users with software development tasks by reading, writing, and editing code files, running commands, and searching codebases.");
        sb.AppendLine();
        sb.AppendLine("## Environment");
        sb.AppendLine($"- Working directory: {env.WorkingDirectory}");
        sb.AppendLine($"- Platform: {Environment.OSVersion.Platform}");
        sb.AppendLine($"- Date: {DateTimeOffset.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("## Tool Usage Guidelines");
        sb.AppendLine("- Use read_file to examine existing code before making changes.");
        sb.AppendLine("- Use edit_file for precise modifications with old_string/new_string replacement.");
        sb.AppendLine("- Use write_file only when creating new files or replacing entire file content.");
        sb.AppendLine("- Use bash for running build tools, tests, git commands, and other CLI operations.");
        sb.AppendLine("- Use glob to find files by name pattern.");
        sb.AppendLine("- Use grep to search file contents for code patterns.");
        sb.AppendLine("- Always use absolute file paths.");
        sb.AppendLine("- Read files before editing them to understand context.");
        sb.AppendLine("- Make minimal, targeted changes rather than rewriting entire files.");
        sb.AppendLine();
        sb.AppendLine("## Coding Guidelines");
        sb.AppendLine("- Follow the existing code style and conventions of the project.");
        sb.AppendLine("- Write clean, maintainable code with appropriate comments.");
        sb.AppendLine("- Handle errors gracefully.");
        sb.AppendLine("- Do not introduce unnecessary dependencies.");

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
