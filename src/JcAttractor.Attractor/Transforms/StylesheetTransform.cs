namespace JcAttractor.Attractor;

public class StylesheetTransform : IGraphTransform
{
    public Graph Transform(Graph graph)
    {
        if (string.IsNullOrWhiteSpace(graph.ModelStylesheet))
            return graph;

        var stylesheet = ModelStylesheet.Parse(graph.ModelStylesheet);
        var updatedNodes = new Dictionary<string, GraphNode>();

        foreach (var (id, node) in graph.Nodes)
        {
            var styleProps = stylesheet.ResolveProperties(node);

            if (styleProps.Count == 0)
            {
                updatedNodes[id] = node;
                continue;
            }

            // Apply stylesheet properties only when the node doesn't have an explicit override
            var updated = node;

            if (styleProps.TryGetValue("model", out var model) && string.IsNullOrEmpty(node.LlmModel))
                updated = updated with { LlmModel = model };

            if (styleProps.TryGetValue("provider", out var provider) && string.IsNullOrEmpty(node.LlmProvider))
                updated = updated with { LlmProvider = provider };

            if (styleProps.TryGetValue("reasoning_effort", out var effort) && node.ReasoningEffort == "high")
                updated = updated with { ReasoningEffort = effort };

            if (styleProps.TryGetValue("fidelity", out var fidelity) && string.IsNullOrEmpty(node.Fidelity))
                updated = updated with { Fidelity = fidelity };

            if (styleProps.TryGetValue("max_retries", out var retries) && node.MaxRetries == 0 && int.TryParse(retries, out var r))
                updated = updated with { MaxRetries = r };

            if (styleProps.TryGetValue("timeout", out var timeout) && node.Timeout == null)
                updated = updated with { Timeout = timeout };

            updatedNodes[id] = updated;
        }

        graph.Nodes.Clear();
        foreach (var (id, node) in updatedNodes)
        {
            graph.Nodes[id] = node;
        }

        return graph;
    }
}
