namespace JcAttractor.Attractor;

public enum LintSeverity { Error, Warning }

public record LintResult(string Rule, LintSeverity Severity, string? NodeId, string? EdgeId, string Message);

public static class Validator
{
    public static List<LintResult> Validate(Graph graph)
    {
        var results = new List<LintResult>();

        ValidateStartNode(graph, results);
        ValidateExitNode(graph, results);
        ValidateStartNodeNoIncoming(graph, results);
        ValidateExitNodeNoOutgoing(graph, results);
        ValidateAllNodesReachable(graph, results);
        ValidateEdgesReferenceValidNodes(graph, results);
        ValidateCodergenNodesHavePrompt(graph, results);
        ValidateConditionExpressions(graph, results);

        return results;
    }

    public static void ValidateOrRaise(Graph graph)
    {
        var results = Validate(graph);
        var errors = results.Where(r => r.Severity == LintSeverity.Error).ToList();
        if (errors.Any())
            throw new InvalidOperationException($"Validation failed: {string.Join("; ", errors.Select(e => e.Message))}");
    }

    private static void ValidateStartNode(Graph graph, List<LintResult> results)
    {
        var startNodes = graph.Nodes.Values.Where(n => n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase)).ToList();
        if (startNodes.Count == 0)
        {
            results.Add(new LintResult("start_node", LintSeverity.Error, null, null, "Graph must have exactly one start node (shape=Mdiamond). Found none."));
        }
        else if (startNodes.Count > 1)
        {
            results.Add(new LintResult("start_node", LintSeverity.Error, null, null,
                $"Graph must have exactly one start node (shape=Mdiamond). Found {startNodes.Count}: {string.Join(", ", startNodes.Select(n => n.Id))}"));
        }
    }

    private static void ValidateExitNode(Graph graph, List<LintResult> results)
    {
        var exitNodes = graph.Nodes.Values.Where(n => n.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase)).ToList();
        if (exitNodes.Count == 0)
        {
            results.Add(new LintResult("exit_node", LintSeverity.Error, null, null, "Graph must have exactly one exit node (shape=Msquare). Found none."));
        }
        else if (exitNodes.Count > 1)
        {
            results.Add(new LintResult("exit_node", LintSeverity.Error, null, null,
                $"Graph must have exactly one exit node (shape=Msquare). Found {exitNodes.Count}: {string.Join(", ", exitNodes.Select(n => n.Id))}"));
        }
    }

    private static void ValidateStartNodeNoIncoming(Graph graph, List<LintResult> results)
    {
        var startNodes = graph.Nodes.Values.Where(n => n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var start in startNodes)
        {
            var incoming = graph.Edges.Where(e => e.ToNode == start.Id).ToList();
            if (incoming.Count > 0)
            {
                results.Add(new LintResult("start_no_incoming", LintSeverity.Error, start.Id, null,
                    $"Start node '{start.Id}' must not have incoming edges. Found {incoming.Count} incoming edge(s)."));
            }
        }
    }

    private static void ValidateExitNodeNoOutgoing(Graph graph, List<LintResult> results)
    {
        var exitNodes = graph.Nodes.Values.Where(n => n.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var exit in exitNodes)
        {
            var outgoing = graph.Edges.Where(e => e.FromNode == exit.Id).ToList();
            if (outgoing.Count > 0)
            {
                results.Add(new LintResult("exit_no_outgoing", LintSeverity.Error, exit.Id, null,
                    $"Exit node '{exit.Id}' must not have outgoing edges. Found {outgoing.Count} outgoing edge(s)."));
            }
        }
    }

    private static void ValidateAllNodesReachable(Graph graph, List<LintResult> results)
    {
        var startNode = graph.Nodes.Values.FirstOrDefault(n => n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase));
        if (startNode == null) return; // Can't check reachability without a start node

        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startNode.Id);
        visited.Add(startNode.Id);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in graph.Edges.Where(e => e.FromNode == current))
            {
                if (visited.Add(edge.ToNode))
                {
                    queue.Enqueue(edge.ToNode);
                }
            }
        }

        foreach (var node in graph.Nodes.Values)
        {
            if (!visited.Contains(node.Id))
            {
                results.Add(new LintResult("reachability", LintSeverity.Error, node.Id, null,
                    $"Node '{node.Id}' is not reachable from the start node '{startNode.Id}'."));
            }
        }
    }

    private static void ValidateEdgesReferenceValidNodes(Graph graph, List<LintResult> results)
    {
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            string edgeId = $"{edge.FromNode}->{edge.ToNode}";

            if (!graph.Nodes.ContainsKey(edge.FromNode))
            {
                results.Add(new LintResult("edge_valid_nodes", LintSeverity.Error, null, edgeId,
                    $"Edge from '{edge.FromNode}' to '{edge.ToNode}' references unknown source node '{edge.FromNode}'."));
            }

            if (!graph.Nodes.ContainsKey(edge.ToNode))
            {
                results.Add(new LintResult("edge_valid_nodes", LintSeverity.Error, null, edgeId,
                    $"Edge from '{edge.FromNode}' to '{edge.ToNode}' references unknown target node '{edge.ToNode}'."));
            }
        }
    }

    private static void ValidateCodergenNodesHavePrompt(Graph graph, List<LintResult> results)
    {
        foreach (var node in graph.Nodes.Values)
        {
            if (node.Shape.Equals("box", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(node.Prompt))
            {
                results.Add(new LintResult("codergen_prompt", LintSeverity.Warning, node.Id, null,
                    $"Codergen node '{node.Id}' (shape=box) has no prompt attribute."));
            }
        }
    }

    private static void ValidateConditionExpressions(Graph graph, List<LintResult> results)
    {
        foreach (var edge in graph.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Condition))
                continue;

            string edgeId = $"{edge.FromNode}->{edge.ToNode}";

            try
            {
                // Attempt to parse the condition to check for syntax errors
                var clauses = edge.Condition.Split("&&", StringSplitOptions.TrimEntries);
                foreach (var clause in clauses)
                {
                    var trimmed = clause.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        results.Add(new LintResult("condition_syntax", LintSeverity.Error, null, edgeId,
                            $"Edge {edgeId} has an empty clause in condition '{edge.Condition}'."));
                        continue;
                    }

                    // Must contain = or !=
                    bool hasOperator = false;
                    if (trimmed.Contains("!="))
                    {
                        var parts = trimmed.Split("!=", 2, StringSplitOptions.TrimEntries);
                        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                        {
                            results.Add(new LintResult("condition_syntax", LintSeverity.Error, null, edgeId,
                                $"Edge {edgeId} has malformed clause '{trimmed}' in condition '{edge.Condition}'."));
                        }
                        hasOperator = true;
                    }
                    else if (trimmed.Contains('='))
                    {
                        var parts = trimmed.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                        {
                            results.Add(new LintResult("condition_syntax", LintSeverity.Error, null, edgeId,
                                $"Edge {edgeId} has malformed clause '{trimmed}' in condition '{edge.Condition}'."));
                        }
                        hasOperator = true;
                    }

                    if (!hasOperator)
                    {
                        results.Add(new LintResult("condition_syntax", LintSeverity.Error, null, edgeId,
                            $"Edge {edgeId} has clause '{trimmed}' without a valid operator (= or !=) in condition '{edge.Condition}'."));
                    }
                }
            }
            catch (Exception ex)
            {
                results.Add(new LintResult("condition_syntax", LintSeverity.Error, null, edgeId,
                    $"Edge {edgeId} has unparseable condition '{edge.Condition}': {ex.Message}"));
            }
        }
    }
}
