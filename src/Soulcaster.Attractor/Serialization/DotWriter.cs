namespace Soulcaster.Attractor.Serialization;

using System.Text;

public static class DotWriter
{
    public static string Serialize(Graph graph)
    {
        var sb = new StringBuilder();
        var graphName = string.IsNullOrWhiteSpace(graph.Name) ? "pipeline" : graph.Name;
        sb.Append("digraph ").Append(QuoteIdentifier(graphName)).AppendLine(" {");

        foreach (var (key, value) in graph.Attributes.OrderBy(item => item.Key, StringComparer.Ordinal))
            sb.AppendLine($"    {key} = {QuoteValue(value)}");

        if (graph.Attributes.Count > 0)
            sb.AppendLine();

        foreach (var node in graph.Nodes.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var attributes = node.RawAttributes.Count > 0
                ? new Dictionary<string, string>(node.RawAttributes, StringComparer.Ordinal)
                : BuildNodeAttributes(node);
            sb.Append("    ")
                .Append(QuoteIdentifier(node.Id))
                .Append(" [")
                .Append(FormatAttributes(attributes))
                .AppendLine("]");
        }

        if (graph.Nodes.Count > 0 && graph.Edges.Count > 0)
            sb.AppendLine();

        foreach (var edge in graph.Edges)
        {
            var attributes = BuildEdgeAttributes(edge);
            sb.Append("    ")
                .Append(QuoteIdentifier(edge.FromNode))
                .Append(" -> ")
                .Append(QuoteIdentifier(edge.ToNode));
            if (attributes.Count > 0)
            {
                sb.Append(" [")
                    .Append(FormatAttributes(attributes))
                    .Append(']');
            }
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildNodeAttributes(GraphNode node)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shape"] = node.Shape
        };

        if (!string.IsNullOrWhiteSpace(node.Label))
            attributes["label"] = node.Label;
        if (!string.IsNullOrWhiteSpace(node.Type))
            attributes["type"] = node.Type;
        if (!string.IsNullOrWhiteSpace(node.Prompt))
            attributes["prompt"] = node.Prompt;
        if (node.MaxRetries > 0)
            attributes["max_retries"] = node.MaxRetries.ToString();
        if (node.GoalGate)
            attributes["goal_gate"] = "true";
        if (!string.IsNullOrWhiteSpace(node.RetryTarget))
            attributes["retry_target"] = node.RetryTarget;
        if (!string.IsNullOrWhiteSpace(node.FallbackRetryTarget))
            attributes["fallback_retry_target"] = node.FallbackRetryTarget;
        if (!string.IsNullOrWhiteSpace(node.Fidelity))
            attributes["fidelity"] = node.Fidelity;
        if (!string.IsNullOrWhiteSpace(node.ThreadId))
            attributes["thread_id"] = node.ThreadId;
        if (!string.IsNullOrWhiteSpace(node.Class))
            attributes["class"] = node.Class;
        if (!string.IsNullOrWhiteSpace(node.Timeout))
            attributes["timeout"] = node.Timeout;
        if (!string.IsNullOrWhiteSpace(node.LlmModel))
            attributes["model"] = node.LlmModel;
        if (!string.IsNullOrWhiteSpace(node.LlmProvider))
            attributes["provider"] = node.LlmProvider;
        if (!string.IsNullOrWhiteSpace(node.ReasoningEffort))
            attributes["reasoning_effort"] = node.ReasoningEffort;
        if (node.AutoStatus)
            attributes["auto_status"] = "true";
        if (node.AllowPartial)
            attributes["allow_partial"] = "true";

        return attributes;
    }

    private static Dictionary<string, string> BuildEdgeAttributes(GraphEdge edge)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(edge.Label))
            attributes["label"] = edge.Label;
        if (!string.IsNullOrWhiteSpace(edge.Condition))
            attributes["condition"] = edge.Condition;
        if (edge.Weight != 0)
            attributes["weight"] = edge.Weight.ToString();
        if (!string.IsNullOrWhiteSpace(edge.Fidelity))
            attributes["fidelity"] = edge.Fidelity;
        if (!string.IsNullOrWhiteSpace(edge.ThreadId))
            attributes["thread_id"] = edge.ThreadId;
        if (edge.LoopRestart)
            attributes["loop_restart"] = "true";
        if (edge.ContextReset)
            attributes["context_reset"] = "true";
        return attributes;
    }

    private static string FormatAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        return string.Join(
            ", ",
            attributes
                .OrderBy(item => AttributeOrder(item.Key))
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={QuoteValue(item.Value)}"));
    }

    private static int AttributeOrder(string key)
    {
        return key switch
        {
            "shape" => 0,
            "label" => 1,
            "prompt" => 2,
            _ => 10
        };
    }

    private static string QuoteIdentifier(string value)
    {
        return $"\"{Escape(value)}\"";
    }

    private static string QuoteValue(string value)
    {
        return $"\"{Escape(value)}\"";
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
