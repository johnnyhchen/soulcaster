namespace Soulcaster.Attractor;

using System.Text.RegularExpressions;

public static partial class VariableExpander
{
    [GeneratedRegex(@"\$\{(?<scope>context|graph)\.(?<key>[^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ScopedVariablePattern();

    [GeneratedRegex(@"(?<!\$)\$(?<key>[A-Za-z_][A-Za-z0-9_.-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex BareVariablePattern();

    public static string Expand(
        string? template,
        IReadOnlyDictionary<string, string>? graphAttributes,
        IReadOnlyDictionary<string, string>? contextValues,
        string? goal,
        bool allowEnvironment = false)
    {
        if (string.IsNullOrEmpty(template))
            return template ?? string.Empty;

        var expanded = template;

        expanded = ScopedVariablePattern().Replace(expanded, match =>
        {
            var scope = match.Groups["scope"].Value;
            var key = match.Groups["key"].Value;
            var value = scope.Equals("context", StringComparison.OrdinalIgnoreCase)
                ? Lookup(contextValues, key)
                : Lookup(graphAttributes, key);
            return value ?? match.Value;
        });

        expanded = BareVariablePattern().Replace(expanded, match =>
        {
            var key = match.Groups["key"].Value;
            if (key.Equals("goal", StringComparison.OrdinalIgnoreCase))
            {
                var resolvedGoal = string.IsNullOrEmpty(goal)
                    ? Lookup(graphAttributes, "goal") ?? Lookup(contextValues, "goal")
                    : goal;
                return resolvedGoal ?? match.Value;
            }

            return Lookup(contextValues, key) ??
                   Lookup(graphAttributes, key) ??
                   (allowEnvironment ? Environment.GetEnvironmentVariable(key) : null) ??
                   match.Value;
        });

        return expanded.Replace("$$", "$", StringComparison.Ordinal);
    }

    private static string? Lookup(IReadOnlyDictionary<string, string>? values, string key)
    {
        if (values is null || string.IsNullOrWhiteSpace(key))
            return null;

        return values.TryGetValue(key, out var exactValue)
            ? exactValue
            : values.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}
