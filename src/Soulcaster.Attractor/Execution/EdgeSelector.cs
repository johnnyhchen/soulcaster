namespace Soulcaster.Attractor.Execution;

public static class EdgeSelector
{
    /// <summary>
    /// Implements the 5-step edge selection algorithm:
    /// 1. Condition-matching edges (evaluate conditions)
    /// 2. Preferred label match (with normalization)
    /// 3. Suggested next IDs
    /// 4. Highest weight among unconditional edges
    /// 5. Lexical tiebreak on target node ID
    /// </summary>
    public static GraphEdge? SelectEdge(
        IReadOnlyList<GraphEdge> outgoingEdges,
        Outcome outcome,
        PipelineContext context)
    {
        if (outgoingEdges.Count == 0)
            return null;

        // Step 1: condition-matching edges only.
        var conditionMatched = outgoingEdges
            .Where(e => !string.IsNullOrWhiteSpace(e.Condition) && ConditionEvaluator.Evaluate(e.Condition, outcome, context))
            .ToList();

        if (conditionMatched.Count > 0)
            return ApplyWeightAndLexical(conditionMatched);

        // Step 2: Preferred label match
        if (!string.IsNullOrWhiteSpace(outcome.PreferredLabel))
        {
            var normalizedPreferred = NormalizeLabel(outcome.PreferredLabel);
            foreach (var edge in outgoingEdges)
            {
                if (!string.IsNullOrWhiteSpace(edge.Condition))
                    continue;

                if (!string.IsNullOrWhiteSpace(edge.Label) &&
                    NormalizeLabel(edge.Label) == normalizedPreferred)
                {
                    return edge;
                }
            }
        }

        // Step 3: Suggested next IDs
        if (outcome.SuggestedNextIds is { Count: > 0 })
        {
            foreach (var suggestedId in outcome.SuggestedNextIds)
            {
                var match = outgoingEdges.FirstOrDefault(e =>
                    string.IsNullOrWhiteSpace(e.Condition) &&
                    e.ToNode == suggestedId);
                if (match != null)
                    return match;
            }
        }

        // Step 4 & 5: Highest weight among unconditional edges, then lexical tiebreak
        var unconditional = outgoingEdges
            .Where(e => string.IsNullOrWhiteSpace(e.Condition))
            .ToList();

        return unconditional.Count == 0 ? null : ApplyWeightAndLexical(unconditional);
    }

    private static GraphEdge ApplyWeightAndLexical(List<GraphEdge> edges)
    {
        // Step 4: Highest weight
        int maxWeight = edges.Max(e => e.Weight);
        var heaviest = edges.Where(e => e.Weight == maxWeight).ToList();

        if (heaviest.Count == 1)
            return heaviest[0];

        // Step 5: Lexical tiebreak on target node ID
        return heaviest.OrderBy(e => e.ToNode, StringComparer.Ordinal).First();
    }

    /// <summary>
    /// Normalizes a label for comparison: lowercase, trim, strip accelerator prefixes like "[Y] ".
    /// </summary>
    public static string NormalizeLabel(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();

        // Strip accelerator prefix pattern like "[Y] ", "[N] ", "[1] ", etc.
        if (normalized.Length >= 4 && normalized[0] == '[' && normalized[2] == ']' && normalized[3] == ' ')
        {
            normalized = normalized[4..];
        }
        // Also handle longer accelerator prefixes like "[Yes] "
        else
        {
            int closeBracket = normalized.IndexOf("] ");
            if (closeBracket > 0 && normalized[0] == '[' && closeBracket < 10)
            {
                normalized = normalized[(closeBracket + 2)..];
            }
        }

        if (normalized.Length >= 3 && normalized[1] == ')' && normalized[2] == ' ')
            normalized = normalized[3..];
        else if (normalized.Length >= 4 && normalized[1] == ' ' && normalized[2] == '-' && normalized[3] == ' ')
            normalized = normalized[4..];

        return normalized.Trim();
    }
}
