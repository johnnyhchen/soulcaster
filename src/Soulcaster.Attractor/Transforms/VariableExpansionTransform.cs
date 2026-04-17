namespace Soulcaster.Attractor.Transforms;

public class VariableExpansionTransform : IGraphTransform
{
    public Graph Transform(Graph graph)
    {
        graph.Goal = VariableExpander.Expand(graph.Goal, graph.Attributes, contextValues: null, goal: null);
        if (string.IsNullOrWhiteSpace(graph.Goal) &&
            graph.Attributes.TryGetValue("goal", out var goalOverride) &&
            !string.IsNullOrWhiteSpace(goalOverride))
        {
            graph.Goal = goalOverride;
        }

        var updatedNodes = new Dictionary<string, GraphNode>();

        foreach (var (id, node) in graph.Nodes)
        {
            var expandedPrompt = VariableExpander.Expand(node.Prompt, graph.Attributes, contextValues: null, goal: graph.Goal);
            updatedNodes[id] = node with { Prompt = expandedPrompt };
        }

        // Replace nodes in graph
        graph.Nodes.Clear();
        foreach (var (id, node) in updatedNodes)
        {
            graph.Nodes[id] = node;
        }

        return graph;
    }
}
