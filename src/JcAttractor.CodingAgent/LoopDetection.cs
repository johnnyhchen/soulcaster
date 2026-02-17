namespace JcAttractor.CodingAgent;

public static class LoopDetection
{
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
