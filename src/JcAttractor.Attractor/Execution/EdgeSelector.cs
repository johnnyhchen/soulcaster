namespace JcAttractor.Attractor;

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

        // If only one edge, return it (no need for complex selection)
        if (outgoingEdges.Count == 1)
            return outgoingEdges[0];

        // Step 1: Condition-matching edges
        var conditionalEdges = outgoingEdges.Where(e => !string.IsNullOrWhiteSpace(e.Condition)).ToList();
        if (conditionalEdges.Count > 0)
        {
            var matching = conditionalEdges
                .Where(e => ConditionEvaluator.Evaluate(e.Condition, outcome, context))
                .ToList();

            if (matching.Count == 1)
                return matching[0];

            if (matching.Count > 1)
            {
                // Among matching conditional edges, apply subsequent steps
                return ApplyTiebreakers(matching, outcome);
            }
        }

        // Step 2: Preferred label match
        if (!string.IsNullOrWhiteSpace(outcome.PreferredLabel))
        {
            var normalizedPreferred = NormalizeLabel(outcome.PreferredLabel);
            var labelMatch = outgoingEdges
                .Where(e => !string.IsNullOrWhiteSpace(e.Label) && NormalizeLabel(e.Label) == normalizedPreferred)
                .ToList();

            if (labelMatch.Count == 1)
                return labelMatch[0];

            if (labelMatch.Count > 1)
                return ApplyWeightAndLexical(labelMatch);
        }

        // Step 3: Suggested next IDs
        if (outcome.SuggestedNextIds is { Count: > 0 })
        {
            foreach (var suggestedId in outcome.SuggestedNextIds)
            {
                var match = outgoingEdges.FirstOrDefault(e => e.ToNode == suggestedId);
                if (match != null)
                    return match;
            }
        }

        // Step 4 & 5: Highest weight among unconditional edges, then lexical tiebreak
        var unconditional = outgoingEdges
            .Where(e => string.IsNullOrWhiteSpace(e.Condition))
            .ToList();

        if (unconditional.Count > 0)
            return ApplyWeightAndLexical(unconditional);

        // Fallback: apply weight and lexical to all edges
        return ApplyWeightAndLexical(outgoingEdges.ToList());
    }

    private static GraphEdge ApplyTiebreakers(List<GraphEdge> edges, Outcome outcome)
    {
        // Step 2 within conditional matches: preferred label
        if (!string.IsNullOrWhiteSpace(outcome.PreferredLabel))
        {
            var normalizedPreferred = NormalizeLabel(outcome.PreferredLabel);
            var labelMatch = edges
                .Where(e => !string.IsNullOrWhiteSpace(e.Label) && NormalizeLabel(e.Label) == normalizedPreferred)
                .ToList();

            if (labelMatch.Count >= 1)
                return ApplyWeightAndLexical(labelMatch);
        }

        return ApplyWeightAndLexical(edges);
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

        return normalized.Trim();
    }
}
