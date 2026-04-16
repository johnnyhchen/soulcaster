namespace Soulcaster.CodingAgent;

public static class LoopDetection
{
    private static readonly HashSet<string> ExplorationToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file",
        "grep",
        "glob",
        "list_dir",
        "read_many_files"
    };

    /// <summary>
    /// Extracts a list of tool call signatures from the session history.
    /// Each signature is a string combining the tool name and a hash of its arguments.
    /// </summary>
    public static List<string> ExtractToolCallSignatures(IReadOnlyList<ITurn> history)
    {
        var signatures = new List<string>();

        foreach (var turn in history)
        {
            if (turn is AssistantTurn assistantTurn)
            {
                foreach (var toolCall in assistantTurn.ToolCalls)
                {
                    var sig = $"{toolCall.Name}:{ComputeHash(toolCall.Arguments)}";
                    signatures.Add(sig);
                }
            }
        }

        return signatures;
    }

    public static bool IsExplorationTool(string toolName) =>
        ExplorationToolNames.Contains(toolName);

    /// <summary>
    /// Counts consecutive assistant tool rounds at the end of the history that are purely
    /// exploratory (read/search/list style tools only). This catches non-terminating loops
    /// where the model keeps gathering slightly different context without making progress.
    /// </summary>
    public static int CountConsecutiveExplorationOnlyRounds(IReadOnlyList<ITurn> history)
    {
        var rounds = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            switch (history[i])
            {
                case ToolResultsTurn:
                case SteeringTurn:
                    continue;
                case UserTurn:
                case SystemTurn:
                    return rounds;
                case AssistantTurn assistantTurn:
                    if (assistantTurn.ToolCalls.Count == 0)
                        return rounds;

                    if (assistantTurn.ToolCalls.All(tc => IsExplorationTool(tc.Name)))
                    {
                        rounds++;
                        continue;
                    }

                    return rounds;
            }
        }

        return rounds;
    }

    public static int CountConsecutiveExplorationOnlyToolCalls(IReadOnlyList<ITurn> history)
    {
        var toolCalls = 0;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            switch (history[i])
            {
                case ToolResultsTurn:
                case SteeringTurn:
                    continue;
                case UserTurn:
                case SystemTurn:
                    return toolCalls;
                case AssistantTurn assistantTurn:
                    if (assistantTurn.ToolCalls.Count == 0)
                        return toolCalls;

                    if (assistantTurn.ToolCalls.All(tc => IsExplorationTool(tc.Name)))
                    {
                        toolCalls += assistantTurn.ToolCalls.Count;
                        continue;
                    }

                    return toolCalls;
            }
        }

        return toolCalls;
    }

    /// <summary>
    /// Detects repeating patterns of length 1, 2, or 3 within the given window
    /// of the most recent tool call signatures.
    /// Returns true if a loop is detected.
    /// </summary>
    public static bool DetectLoop(IReadOnlyList<ITurn> history, int window = 10)
    {
        var signatures = ExtractToolCallSignatures(history);
        if (signatures.Count < 2)
            return false;

        // Take only the last 'window' signatures
        var recent = signatures.Count > window
            ? signatures.Skip(signatures.Count - window).ToList()
            : signatures;

        // Check for repeating patterns of length 1, 2, and 3
        for (var patternLen = 1; patternLen <= 3; patternLen++)
        {
            if (HasRepeatingPattern(recent, patternLen))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if the last N signatures form a repeating pattern of the given length.
    /// Requires at least 3 repetitions to consider it a loop.
    /// </summary>
    private static bool HasRepeatingPattern(List<string> signatures, int patternLen)
    {
        // Need at least 3 repetitions of the pattern
        var minRequired = patternLen * 3;
        if (signatures.Count < minRequired)
            return false;

        // Extract the candidate pattern from the most recent signatures
        var pattern = signatures.Skip(signatures.Count - patternLen).Take(patternLen).ToList();

        // Count consecutive repetitions going backward
        var repetitions = 0;
        var idx = signatures.Count - patternLen;

        while (idx >= 0)
        {
            var segment = signatures.Skip(idx).Take(patternLen).ToList();
            if (segment.Count < patternLen)
                break;

            var matches = true;
            for (var i = 0; i < patternLen; i++)
            {
                if (segment[i] != pattern[i])
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
                break;

            repetitions++;
            idx -= patternLen;
        }

        return repetitions >= 3;
    }

    private static string ComputeHash(string input)
    {
        // Simple hash for signature comparison
        var hash = 0;
        foreach (var c in input)
        {
            hash = ((hash << 5) + hash) ^ c;
        }
        return hash.ToString("x8");
    }
}
