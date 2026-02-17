namespace JcAttractor.Attractor;

public static class ConditionEvaluator
{
    /// <summary>
    /// Evaluates a condition expression against the current outcome and pipeline context.
    /// Supports = (equals), != (not equals), and && (AND conjunction).
    /// Variables: outcome, preferred_label, context.* (context values).
    /// Empty condition always evaluates to true.
    /// </summary>
    public static bool Evaluate(string condition, Outcome outcome, PipelineContext context)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return true;

        var clauses = condition.Split("&&", StringSplitOptions.TrimEntries);

        foreach (var clause in clauses)
        {
            var trimmed = clause.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (!EvaluateClause(trimmed, outcome, context))
                return false;
        }

        return true;
    }

    private static bool EvaluateClause(string clause, Outcome outcome, PipelineContext context)
    {
        // Check for != first (before =, since = is a substring of !=)
        if (clause.Contains("!="))
        {
            var parts = clause.Split("!=", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                string left = ResolveVariable(parts[0].Trim(), outcome, context);
                string right = ResolveValue(parts[1].Trim());
                return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
            }
        }
        else if (clause.Contains('='))
        {
            var parts = clause.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                string left = ResolveVariable(parts[0].Trim(), outcome, context);
                string right = ResolveValue(parts[1].Trim());
                return left.Equals(right, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Unknown expression format, treat as false
        return false;
    }

    private static string ResolveVariable(string variable, Outcome outcome, PipelineContext context)
    {
        return variable.ToLowerInvariant() switch
        {
            "outcome" => outcome.Status.ToString().ToLowerInvariant(),
            "preferred_label" => outcome.PreferredLabel,
            _ when variable.StartsWith("context.", StringComparison.OrdinalIgnoreCase) =>
                context.Get(variable.Substring("context.".Length)),
            _ => context.Get(variable)
        };
    }

    private static string ResolveValue(string value)
    {
        // Strip quotes if present
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            return value[1..^1];
        if (value.Length >= 2 && value.StartsWith('\'') && value.EndsWith('\''))
            return value[1..^1];
        return value;
    }

    /// <summary>
    /// Attempts to parse the condition to check for syntax errors.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    public static string? TryParse(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;

        var clauses = condition.Split("&&", StringSplitOptions.TrimEntries);

        foreach (var clause in clauses)
        {
            var trimmed = clause.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return $"Empty clause in condition '{condition}'";

            bool hasOperator = trimmed.Contains("!=") || trimmed.Contains('=');
            if (!hasOperator)
                return $"Clause '{trimmed}' has no valid operator (= or !=)";

            string separator = trimmed.Contains("!=") ? "!=" : "=";
            var parts = trimmed.Split(separator, 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return $"Malformed clause '{trimmed}'";
        }

        return null;
    }
}
