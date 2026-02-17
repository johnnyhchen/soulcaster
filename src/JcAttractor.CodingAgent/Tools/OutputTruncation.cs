namespace JcAttractor.CodingAgent;

public static class OutputTruncation
{
    private static readonly Dictionary<string, int> DefaultCharLimits = new()
    {
        ["read_file"] = 50000,
        ["shell"] = 30000,
        ["bash"] = 30000,
        ["grep"] = 20000,
        ["glob"] = 10000
    };

    private static readonly Dictionary<string, int> DefaultLineLimits = new()
    {
        ["shell"] = 256,
        ["bash"] = 256,
        ["grep"] = 200,
        ["glob"] = 500
    };

    private const int DefaultCharLimit = 20000;
    private const double HeadRatio = 0.40;
    private const double TailRatio = 0.40;

    /// <summary>
    /// Truncates tool output based on the tool name and optional custom limits.
    /// Applies character-based truncation first, then line-based truncation.
    /// </summary>
    public static string Truncate(string output, string toolName, Dictionary<string, int>? customLimits = null)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        // Phase 1: Character-based truncation
        var charLimit = GetCharLimit(toolName, customLimits);
        output = TruncateByChars(output, charLimit);

        // Phase 2: Line-based truncation
        var lineLimit = GetLineLimit(toolName);
        if (lineLimit.HasValue)
            output = TruncateByLines(output, lineLimit.Value);

        return output;
    }

    private static int GetCharLimit(string toolName, Dictionary<string, int>? customLimits)
    {
        if (customLimits is not null && customLimits.TryGetValue(toolName, out var custom))
            return custom;
        return DefaultCharLimits.GetValueOrDefault(toolName, DefaultCharLimit);
    }

    private static int? GetLineLimit(string toolName)
    {
        return DefaultLineLimits.TryGetValue(toolName, out var limit) ? limit : null;
    }

    private static string TruncateByChars(string output, int limit)
    {
        if (output.Length <= limit)
            return output;

        var removedCount = output.Length - limit;
        var headSize = (int)(limit * HeadRatio);
        var tailSize = (int)(limit * TailRatio);

        var head = output[..headSize];
        var tail = output[^tailSize..];
        var marker = $"\n[WARNING: Tool output was truncated. {removedCount} characters removed...]\n";

        return head + marker + tail;
    }

    private static string TruncateByLines(string output, int limit)
    {
        var lines = output.Split('\n');
        if (lines.Length <= limit)
            return output;

        var removedCount = lines.Length - limit;
        var headLines = (int)(limit * HeadRatio);
        var tailLines = (int)(limit * TailRatio);

        var head = string.Join('\n', lines.Take(headLines));
        var tail = string.Join('\n', lines.Skip(lines.Length - tailLines));
        var marker = $"\n[WARNING: Tool output was truncated. {removedCount} lines removed...]\n";

        return head + marker + tail;
    }
}
