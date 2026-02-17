namespace JcAttractor.Attractor;

public class Graph
{
    public string Name { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Label { get; set; } = "";
    public string ModelStylesheet { get; set; } = "";
    public int DefaultMaxRetry { get; set; } = 50;
    public string RetryTarget { get; set; } = "";
    public string FallbackRetryTarget { get; set; } = "";
    public string DefaultFidelity { get; set; } = "";
    public Dictionary<string, GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();
    public Dictionary<string, string> Attributes { get; } = new();

    public IReadOnlyList<GraphEdge> OutgoingEdges(string nodeId) =>
        Edges.Where(e => e.FromNode == nodeId).ToList();
}

public record GraphNode
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Shape { get; init; } = "box";
    public string Type { get; init; } = "";
    public string Prompt { get; init; } = "";
    public int MaxRetries { get; init; } = 0;
    public bool GoalGate { get; init; } = false;
    public string RetryTarget { get; init; } = "";
    public string FallbackRetryTarget { get; init; } = "";
    public string Fidelity { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string Class { get; init; } = "";
    public string? Timeout { get; init; }
    public string LlmModel { get; init; } = "";
    public string LlmProvider { get; init; } = "";
    public string ReasoningEffort { get; init; } = "high";
    public bool AutoStatus { get; init; } = false;
    public bool AllowPartial { get; init; } = false;
    public Dictionary<string, string> RawAttributes { get; init; } = new();
}

public record GraphEdge
{
    public string FromNode { get; init; } = "";
    public string ToNode { get; init; } = "";
    public string Label { get; init; } = "";
    public string Condition { get; init; } = "";
    public int Weight { get; init; } = 0;
    public string Fidelity { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public bool LoopRestart { get; init; } = false;
}
