using Soulcaster.Attractor;
using System.Text;

namespace Soulcaster.Runner;

public static class BuilderCommandSupport
{
    public static Graph InitializeGraph(string name, string? goal = null)
    {
        var graph = new Graph { Name = string.IsNullOrWhiteSpace(name) ? "pipeline" : name };
        if (!string.IsNullOrWhiteSpace(goal))
        {
            graph.Goal = goal;
            graph.Attributes["goal"] = goal;
        }

        graph.Nodes["start"] = new GraphNode
        {
            Id = "start",
            Shape = "Mdiamond",
            Label = "Start",
            RawAttributes = new Dictionary<string, string>
            {
                ["shape"] = "Mdiamond",
                ["label"] = "Start"
            }
        };
        graph.Nodes["done"] = new GraphNode
        {
            Id = "done",
            Shape = "Msquare",
            Label = "Done",
            RawAttributes = new Dictionary<string, string>
            {
                ["shape"] = "Msquare",
                ["label"] = "Done"
            }
        };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });
        return graph;
    }

    public static Graph Load(string dotFilePath)
    {
        var source = File.ReadAllText(dotFilePath);
        return DotParser.Parse(source);
    }

    public static void Save(string dotFilePath, Graph graph)
    {
        var fullPath = Path.GetFullPath(dotFilePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, DotWriter.Serialize(graph));
    }

    public static void UpsertGraphAttributes(Graph graph, IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var (key, value) in attributes)
        {
            graph.Attributes[key] = value;
            switch (key)
            {
                case "goal":
                    graph.Goal = value;
                    break;
                case "label":
                    graph.Label = value;
                    break;
                case "model_stylesheet":
                    graph.ModelStylesheet = value;
                    break;
                case "retry_target":
                    graph.RetryTarget = value;
                    break;
                case "fallback_retry_target":
                    graph.FallbackRetryTarget = value;
                    break;
                case "default_fidelity":
                    graph.DefaultFidelity = value;
                    break;
                case "default_max_retry" when int.TryParse(value, out var maxRetry):
                    graph.DefaultMaxRetry = maxRetry;
                    break;
            }
        }
    }

    public static void UpsertNode(Graph graph, string nodeId, IReadOnlyDictionary<string, string> attributes)
    {
        var merged = graph.Nodes.TryGetValue(nodeId, out var existing)
            ? new Dictionary<string, string>(existing.RawAttributes, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in attributes)
            merged[key] = value;

        merged.TryAdd("shape", existing?.Shape ?? "box");
        merged.TryAdd("label", existing?.Label ?? nodeId);
        graph.Nodes[nodeId] = BuildNode(nodeId, merged);
    }

    public static void UpsertEdge(Graph graph, string fromNode, string toNode, IReadOnlyDictionary<string, string> attributes)
    {
        if (!graph.Nodes.ContainsKey(fromNode))
            UpsertNode(graph, fromNode, new Dictionary<string, string> { ["label"] = fromNode });
        if (!graph.Nodes.ContainsKey(toNode))
            UpsertNode(graph, toNode, new Dictionary<string, string> { ["label"] = toNode });

        if (!(string.Equals(fromNode, "start", StringComparison.Ordinal) && string.Equals(toNode, "done", StringComparison.Ordinal)))
        {
            graph.Edges.RemoveAll(edge =>
                string.Equals(edge.FromNode, "start", StringComparison.Ordinal) &&
                string.Equals(edge.ToNode, "done", StringComparison.Ordinal));
        }

        var index = graph.Edges.FindIndex(edge =>
            string.Equals(edge.FromNode, fromNode, StringComparison.Ordinal) &&
            string.Equals(edge.ToNode, toNode, StringComparison.Ordinal));

        var existingAttributes = index >= 0
            ? BuildEdgeAttributes(graph.Edges[index])
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
            existingAttributes[key] = value;

        var edge = BuildEdge(fromNode, toNode, existingAttributes);
        if (index >= 0)
            graph.Edges[index] = edge;
        else
            graph.Edges.Add(edge);
    }

    public static string Describe(Graph graph)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Graph: {graph.Name}");
        sb.AppendLine($"Goal: {(string.IsNullOrWhiteSpace(graph.Goal) ? "[none]" : graph.Goal)}");
        sb.AppendLine($"Nodes: {graph.Nodes.Count}");
        foreach (var node in graph.Nodes.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
            sb.AppendLine($"  {node.Id} [{node.Shape}]");
        sb.AppendLine($"Edges: {graph.Edges.Count}");
        foreach (var edge in graph.Edges)
            sb.AppendLine($"  {edge.FromNode} -> {edge.ToNode}");
        return sb.ToString();
    }

    private static GraphNode BuildNode(string nodeId, IReadOnlyDictionary<string, string> attributes)
    {
        var rawAttributes = new Dictionary<string, string>(attributes, StringComparer.Ordinal);
        return new GraphNode
        {
            Id = nodeId,
            Label = rawAttributes.GetValueOrDefault("label", nodeId),
            Shape = rawAttributes.GetValueOrDefault("shape", "box"),
            Type = rawAttributes.GetValueOrDefault("type", ""),
            Prompt = rawAttributes.GetValueOrDefault("prompt", ""),
            MaxRetries = int.TryParse(rawAttributes.GetValueOrDefault("max_retries", "0"), out var maxRetries) ? maxRetries : 0,
            GoalGate = rawAttributes.GetValueOrDefault("goal_gate", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            RetryTarget = rawAttributes.GetValueOrDefault("retry_target", ""),
            FallbackRetryTarget = rawAttributes.GetValueOrDefault("fallback_retry_target", ""),
            Fidelity = rawAttributes.GetValueOrDefault("fidelity", ""),
            ThreadId = rawAttributes.GetValueOrDefault("thread_id", ""),
            Class = rawAttributes.GetValueOrDefault("class", ""),
            Timeout = rawAttributes.TryGetValue("timeout", out var timeout) ? timeout : null,
            LlmModel = rawAttributes.GetValueOrDefault("model", ""),
            LlmProvider = rawAttributes.GetValueOrDefault("provider", ""),
            ReasoningEffort = rawAttributes.GetValueOrDefault("reasoning_effort", "high"),
            AutoStatus = rawAttributes.GetValueOrDefault("auto_status", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            AllowPartial = rawAttributes.GetValueOrDefault("allow_partial", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            RawAttributes = rawAttributes
        };
    }

    private static GraphEdge BuildEdge(string fromNode, string toNode, IReadOnlyDictionary<string, string> attributes)
    {
        return new GraphEdge
        {
            FromNode = fromNode,
            ToNode = toNode,
            Label = attributes.GetValueOrDefault("label", ""),
            Condition = attributes.GetValueOrDefault("condition", ""),
            Weight = int.TryParse(attributes.GetValueOrDefault("weight", "0"), out var weight) ? weight : 0,
            Fidelity = attributes.GetValueOrDefault("fidelity", ""),
            ThreadId = attributes.GetValueOrDefault("thread_id", ""),
            LoopRestart = attributes.GetValueOrDefault("loop_restart", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            ContextReset = attributes.GetValueOrDefault("context_reset", "false").Equals("true", StringComparison.OrdinalIgnoreCase)
        };
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
}
