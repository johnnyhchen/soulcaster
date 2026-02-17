namespace JcAttractor.Attractor;

public class ModelStylesheet
{
    private readonly List<StyleRule> _rules = new();

    /// <summary>
    /// Parses CSS-like model stylesheet text into rules.
    /// Selectors:
    ///   shape name:  box { model = "claude-opus-4-6" }
    ///   class name:  .fast { model = "gemini-3-flash-preview" }
    ///   node ID:     #review { reasoning_effort = "high" }
    ///   universal:   * { provider = "anthropic" }
    /// </summary>
    public static ModelStylesheet Parse(string stylesheet)
    {
        var result = new ModelStylesheet();

        if (string.IsNullOrWhiteSpace(stylesheet))
            return result;

        int pos = 0;
        string text = stylesheet.Trim();

        while (pos < text.Length)
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;

            // Read selector
            int selectorStart = pos;
            while (pos < text.Length && text[pos] != '{')
                pos++;

            if (pos >= text.Length) break;

            string selector = text[selectorStart..pos].Trim();
            pos++; // skip {

            // Read properties until }
            var properties = new Dictionary<string, string>();
            while (pos < text.Length && text[pos] != '}')
            {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length || text[pos] == '}') break;

                // Read property name
                int keyStart = pos;
                while (pos < text.Length && text[pos] != '=' && text[pos] != '}')
                    pos++;

                if (pos >= text.Length || text[pos] == '}') break;

                string key = text[keyStart..pos].Trim();
                pos++; // skip =

                SkipWhitespace(text, ref pos);

                // Read property value
                string value;
                if (pos < text.Length && text[pos] == '"')
                {
                    pos++; // skip opening quote
                    int valueStart = pos;
                    while (pos < text.Length && text[pos] != '"')
                    {
                        if (text[pos] == '\\' && pos + 1 < text.Length)
                            pos++; // skip escaped char
                        pos++;
                    }
                    value = text[valueStart..pos];
                    if (pos < text.Length) pos++; // skip closing quote
                }
                else
                {
                    int valueStart = pos;
                    while (pos < text.Length && text[pos] != ';' && text[pos] != '\n' && text[pos] != '}')
                        pos++;
                    value = text[valueStart..pos].Trim();
                }

                if (!string.IsNullOrEmpty(key))
                    properties[key] = value;

                // Skip optional semicolons / newlines
                while (pos < text.Length && (text[pos] == ';' || text[pos] == '\n' || text[pos] == '\r'))
                    pos++;
            }

            if (pos < text.Length) pos++; // skip }

            if (!string.IsNullOrEmpty(selector) && properties.Count > 0)
            {
                result._rules.Add(new StyleRule(selector, properties));
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the effective properties for a given node, applying specificity:
    /// universal (*) &lt; shape &lt; class &lt; ID.
    /// Explicit node attributes always override stylesheet properties.
    /// </summary>
    public Dictionary<string, string> ResolveProperties(GraphNode node)
    {
        var resolved = new Dictionary<string, string>();

        // Apply rules in specificity order
        // 1. Universal (*)
        foreach (var rule in _rules.Where(r => r.Selector == "*"))
        {
            foreach (var (k, v) in rule.Properties)
                resolved[k] = v;
        }

        // 2. Shape selector
        foreach (var rule in _rules.Where(r => !r.Selector.StartsWith('.') && !r.Selector.StartsWith('#') && r.Selector != "*"))
        {
            if (rule.Selector.Equals(node.Shape, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (k, v) in rule.Properties)
                    resolved[k] = v;
            }
        }

        // 3. Class selector (.className)
        if (!string.IsNullOrEmpty(node.Class))
        {
            var nodeClasses = node.Class.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var rule in _rules.Where(r => r.Selector.StartsWith('.')))
            {
                string ruleClass = rule.Selector[1..];
                if (nodeClasses.Any(c => c.Equals(ruleClass, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var (k, v) in rule.Properties)
                        resolved[k] = v;
                }
            }
        }

        // 4. ID selector (#nodeId)
        foreach (var rule in _rules.Where(r => r.Selector.StartsWith('#')))
        {
            string ruleId = rule.Selector[1..];
            if (ruleId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (k, v) in rule.Properties)
                    resolved[k] = v;
            }
        }

        return resolved;
    }

    public IReadOnlyList<StyleRule> Rules => _rules;

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
    }
}

public record StyleRule(string Selector, Dictionary<string, string> Properties);
