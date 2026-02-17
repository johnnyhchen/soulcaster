namespace JcAttractor.Attractor;

public class VariableExpansionTransform : IGraphTransform
{
    public Graph Transform(Graph graph)
    {
        if (string.IsNullOrEmpty(graph.Goal))
            return graph;

        var updatedNodes = new Dictionary<string, GraphNode>();

        foreach (var (id, node) in graph.Nodes)
        {
            if (!string.IsNullOrEmpty(node.Prompt) && node.Prompt.Contains("$goal"))
            {
                updatedNodes[id] = node with { Prompt = node.Prompt.Replace("$goal", graph.Goal) };
            }
            else
            {
                updatedNodes[id] = node;
            }
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
