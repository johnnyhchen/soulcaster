namespace Soulcaster.Attractor.Validation;

using Soulcaster.UnifiedLlm;

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
        ValidateStuckNodes(graph, results);
        ValidateParallelWithoutFanIn(graph, results);
        ValidateQueueParallelNodes(graph, results);
        ValidateGoalGateWithoutRetryTarget(graph, results);
        ValidateParallelJoinPolicies(graph, results);
        ValidateExecutionLanes(graph, results);
        ValidateHumanGateChoices(graph, results);
        ValidateForceAdvanceTargets(graph, results);
        ValidateRoutingModelReferences(graph, results);
        ValidateImageOutputLanes(graph, results);
        ValidateAttachmentAuthoring(graph, results);

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

    private static void ValidateStuckNodes(Graph graph, List<LintResult> results)
    {
        foreach (var node in graph.Nodes.Values)
        {
            // Skip terminal nodes
            if (node.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase))
                continue;

            var outgoing = graph.Edges.Where(e => e.FromNode == node.Id).ToList();
            if (outgoing.Count == 0)
            {
                results.Add(new LintResult("stuck_node", LintSeverity.Warning, node.Id, null,
                    $"Non-terminal node '{node.Id}' has no outgoing edges and may cause the pipeline to get stuck."));
            }
        }
    }

    private static void ValidateParallelWithoutFanIn(Graph graph, List<LintResult> results)
    {
        var parallelNodes = graph.Nodes.Values
            .Where(n => n.Shape.Equals("component", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasFanIn = graph.Nodes.Values
            .Any(n => n.Shape.Equals("tripleoctagon", StringComparison.OrdinalIgnoreCase));

        foreach (var parallel in parallelNodes)
        {
            if (!hasFanIn)
            {
                results.Add(new LintResult("parallel_no_fan_in", LintSeverity.Warning, parallel.Id, null,
                    $"Parallel node '{parallel.Id}' has no corresponding fan-in node (shape=tripleoctagon) to collect results."));
            }
        }
    }

    private static void ValidateQueueParallelNodes(Graph graph, List<LintResult> results)
    {
        var queueParallelNodes = graph.Nodes.Values
            .Where(n => n.Shape.Equals("component", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.RawAttributes.ContainsKey("queue_source"))
            .ToList();

        foreach (var parallel in queueParallelNodes)
        {
            var queueSource = parallel.RawAttributes.GetValueOrDefault("queue_source", "").Trim();
            if (string.IsNullOrWhiteSpace(queueSource))
            {
                results.Add(new LintResult(
                    "queue_parallel_source",
                    LintSeverity.Error,
                    parallel.Id,
                    null,
                    $"Queue-backed parallel node '{parallel.Id}' must provide a non-empty queue_source attribute."));
            }

            var distinctTargets = graph.Edges
                .Where(e => e.FromNode == parallel.Id)
                .Select(e => e.ToNode)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (distinctTargets.Count != 1)
            {
                results.Add(new LintResult(
                    "queue_parallel_worker",
                    LintSeverity.Error,
                    parallel.Id,
                    null,
                    $"Queue-backed parallel node '{parallel.Id}' must have exactly one outgoing worker edge. Found {distinctTargets.Count}."));
            }
        }
    }

    private static void ValidateGoalGateWithoutRetryTarget(Graph graph, List<LintResult> results)
    {
        var goalGateNodes = graph.Nodes.Values.Where(n => n.GoalGate).ToList();
        foreach (var gate in goalGateNodes)
        {
            var hasRetryTarget = !string.IsNullOrEmpty(gate.RetryTarget)
                || !string.IsNullOrEmpty(gate.FallbackRetryTarget)
                || !string.IsNullOrEmpty(graph.RetryTarget)
                || !string.IsNullOrEmpty(graph.FallbackRetryTarget);

            if (!hasRetryTarget)
            {
                results.Add(new LintResult("goal_gate_no_retry", LintSeverity.Warning, gate.Id, null,
                    $"Goal gate node '{gate.Id}' has no retry_target or fallback_retry_target. Pipeline will fail if this gate is unsatisfied."));
            }
        }
    }

    private static void ValidateParallelJoinPolicies(Graph graph, List<LintResult> results)
    {
        foreach (var node in graph.Nodes.Values.Where(n => n.Shape.Equals("component", StringComparison.OrdinalIgnoreCase)))
        {
            var joinPolicy = node.RawAttributes.GetValueOrDefault("join_policy", "wait_all").Trim();
            if (string.IsNullOrWhiteSpace(joinPolicy))
                joinPolicy = "wait_all";

            var branchCount = graph.Edges.Count(edge => edge.FromNode == node.Id);
            var normalized = joinPolicy.ToLowerInvariant();
            var inlineThreshold = default(int?);
            if (normalized.StartsWith("k_of_n:", StringComparison.Ordinal))
            {
                inlineThreshold = int.TryParse(normalized["k_of_n:".Length..], out var parsedInline)
                    ? parsedInline
                    : null;
                normalized = "k_of_n";
            }

            if (normalized is not ("wait_all" or "first_success" or "quorum" or "k_of_n"))
            {
                results.Add(new LintResult(
                    "parallel_join_policy",
                    LintSeverity.Error,
                    node.Id,
                    null,
                    $"Parallel node '{node.Id}' uses unknown join_policy '{joinPolicy}'."));
                continue;
            }

            if (normalized != "k_of_n")
                continue;

            var threshold = inlineThreshold
                ?? ParseInt(node.RawAttributes.GetValueOrDefault("join_k"))
                ?? ParseInt(node.RawAttributes.GetValueOrDefault("k"));
            if (threshold is null)
            {
                results.Add(new LintResult(
                    "parallel_join_policy",
                    LintSeverity.Error,
                    node.Id,
                    null,
                    $"Parallel node '{node.Id}' requires join_k or k when join_policy=k_of_n."));
                continue;
            }

            if (threshold < 1 || threshold > branchCount)
            {
                results.Add(new LintResult(
                    "parallel_join_policy",
                    LintSeverity.Error,
                    node.Id,
                    null,
                    $"Parallel node '{node.Id}' configured k_of_n={threshold}, but branch count is {branchCount}."));
            }
        }
    }

    private static void ValidateExecutionLanes(Graph graph, List<LintResult> results)
    {
        foreach (var node in graph.Nodes.Values.Where(n => n.Shape.Equals("box", StringComparison.OrdinalIgnoreCase)))
        {
            var lane = ResolveExecutionLane(node, graph);
            if (lane is "agent" or "leaf" or "multimodal_leaf")
                continue;

            results.Add(new LintResult(
                "execution_lane",
                LintSeverity.Error,
                node.Id,
                null,
                $"Node '{node.Id}' uses unknown execution lane '{lane}'."));
        }
    }

    private static void ValidateHumanGateChoices(Graph graph, List<LintResult> results)
    {
        foreach (var gate in graph.Nodes.Values.Where(n => n.Shape.Equals("hexagon", StringComparison.OrdinalIgnoreCase)))
        {
            var outgoing = graph.Edges.Where(edge => edge.FromNode == gate.Id).ToList();
            var labels = outgoing
                .Select(edge => edge.Label?.Trim())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Cast<string>()
                .ToList();

            if (labels.Count == 0)
            {
                results.Add(new LintResult(
                    "wait_human_choices",
                    LintSeverity.Warning,
                    gate.Id,
                    null,
                    $"Human gate '{gate.Id}' should use labelled outgoing edges so choices are explicit."));
                continue;
            }

            var duplicates = labels
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                results.Add(new LintResult(
                    "wait_human_choices",
                    LintSeverity.Warning,
                    gate.Id,
                    null,
                    $"Human gate '{gate.Id}' has duplicate choice labels: {string.Join(", ", duplicates)}."));
            }
        }
    }

    private static void ValidateForceAdvanceTargets(Graph graph, List<LintResult> results)
    {
        if (!graph.Attributes.TryGetValue("force_advance_targets", out var rawTargets) || string.IsNullOrWhiteSpace(rawTargets))
            return;

        foreach (var target in ParseCsv(rawTargets))
        {
            if (!graph.Nodes.TryGetValue(target, out var node))
            {
                results.Add(new LintResult(
                    "force_advance_targets",
                    LintSeverity.Error,
                    null,
                    null,
                    $"force_advance_targets references unknown node '{target}'."));
                continue;
            }

            if (node.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new LintResult(
                    "force_advance_targets",
                    LintSeverity.Error,
                    target,
                    null,
                    "force_advance_targets must not include the start node."));
            }
        }
    }

    private static void ValidateRoutingModelReferences(Graph graph, List<LintResult> results)
    {
        ValidateModelReferenceSet(
            graph.Attributes,
            "graph",
            results);

        foreach (var node in graph.Nodes.Values)
            ValidateModelReferenceSet(node.RawAttributes, node.Id, results);
    }

    private static void ValidateModelReferenceSet(
        IReadOnlyDictionary<string, string> attributes,
        string location,
        List<LintResult> results)
    {
        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('$', StringComparison.Ordinal))
                continue;

            if (key.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith("_preferred_model", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("preferred_model", StringComparison.OrdinalIgnoreCase))
            {
                if (ModelCatalog.GetModelInfo(value) is null)
                {
                    results.Add(new LintResult(
                        "routing_model_reference",
                        LintSeverity.Warning,
                        location == "graph" ? null : location,
                        null,
                        $"Model reference '{value}' in '{key}' is not in the local catalog or registry snapshot."));
                }
            }

            if (!key.EndsWith("_fallback_models", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("fallback_models", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var modelId in ParseCsv(value))
            {
                if (ModelCatalog.GetModelInfo(modelId) is null)
                {
                    results.Add(new LintResult(
                        "routing_model_reference",
                        LintSeverity.Warning,
                        location == "graph" ? null : location,
                        null,
                        $"Fallback model '{modelId}' in '{key}' is not in the local catalog or registry snapshot."));
                }
            }
        }
    }

    private static void ValidateImageOutputLanes(Graph graph, List<LintResult> results)
    {
        foreach (var node in graph.Nodes.Values.Where(n => n.Shape.Equals("box", StringComparison.OrdinalIgnoreCase)))
        {
            var outputModalities = ResolveOutputModalities(node, graph);
            if (!outputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
                continue;

            var lane = ResolveExecutionLane(node, graph);
            if (lane.Equals("multimodal_leaf", StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new LintResult(
                "image_output_lane",
                LintSeverity.Warning,
                node.Id,
                null,
                $"Node '{node.Id}' requests image output but execution_lane='{lane}'. Prefer execution_lane=multimodal_leaf."));
        }
    }

    private static void ValidateAttachmentAuthoring(Graph graph, List<LintResult> results)
    {
        foreach (var node in graph.Nodes.Values.Where(n => n.Shape.Equals("box", StringComparison.OrdinalIgnoreCase)))
        {
            var lane = ResolveExecutionLane(node, graph);
            ValidateDeclaredAttachments(graph, results, node, lane, "image", ResolveInputImages(node, graph));
            ValidateDeclaredAttachments(graph, results, node, lane, "document", ResolveInputDocuments(node, graph));
            ValidateDeclaredAttachments(graph, results, node, lane, "audio", ResolveInputAudio(node, graph));
        }
    }

    private static void ValidateDeclaredAttachments(
        Graph graph,
        List<LintResult> results,
        GraphNode node,
        string lane,
        string attachmentKind,
        IReadOnlyList<string> attachments)
    {
        if (attachments.Count == 0)
            return;

        var recommendedLane = attachmentKind == "image" ? "multimodal_leaf" : "leaf";
        var laneAllowed = attachmentKind == "image"
            ? lane.Equals("multimodal_leaf", StringComparison.OrdinalIgnoreCase)
            : lane.Equals("leaf", StringComparison.OrdinalIgnoreCase) || lane.Equals("multimodal_leaf", StringComparison.OrdinalIgnoreCase);
        if (!laneAllowed)
        {
            var attributeLabel = attachmentKind switch
            {
                "image" => "input_images",
                "document" => "input_documents",
                "audio" => "input_audio",
                _ => $"input_{attachmentKind}"
            };
            results.Add(new LintResult(
                $"input_{attachmentKind}_lane",
                LintSeverity.Warning,
                node.Id,
                null,
                $"Node '{node.Id}' declares {attributeLabel} but execution_lane='{lane}'. Prefer execution_lane={recommendedLane}."));
        }

        foreach (var rawPath in attachments)
        {
            if (LooksLikeRemoteUri(rawPath))
            {
                results.Add(new LintResult(
                    $"input_{attachmentKind}_remote",
                    LintSeverity.Warning,
                    node.Id,
                    null,
                    $"Node '{node.Id}' references remote input {attachmentKind} '{rawPath}'. The current runner expects local file paths."));
                continue;
            }

            var resolvedPath = ResolveInputPath(rawPath, graph);
            if (IsDeferredArtifactPath(rawPath))
                continue;

            if (!File.Exists(resolvedPath))
            {
                results.Add(new LintResult(
                    $"input_{attachmentKind}_missing",
                    LintSeverity.Warning,
                    node.Id,
                    null,
                    $"Node '{node.Id}' references input {attachmentKind} '{rawPath}', but '{resolvedPath}' does not exist."));
            }
        }
    }

    private static string ResolveExecutionLane(GraphNode node, Graph graph)
    {
        if (node.RawAttributes.TryGetValue("execution_lane", out var nodeLane) && !string.IsNullOrWhiteSpace(nodeLane))
            return nodeLane.Trim().ToLowerInvariant();
        if (node.RawAttributes.TryGetValue("lane", out var shortLane) && !string.IsNullOrWhiteSpace(shortLane))
            return shortLane.Trim().ToLowerInvariant();
        if (graph.Attributes.TryGetValue("execution_lane", out var graphLane) && !string.IsNullOrWhiteSpace(graphLane))
            return graphLane.Trim().ToLowerInvariant();
        if (graph.Attributes.TryGetValue("default_execution_lane", out var defaultLane) && !string.IsNullOrWhiteSpace(defaultLane))
            return defaultLane.Trim().ToLowerInvariant();
        return "agent";
    }

    private static IReadOnlyList<string> ResolveOutputModalities(GraphNode node, Graph graph)
    {
        if (node.RawAttributes.TryGetValue("output_modalities", out var nodeValue) && !string.IsNullOrWhiteSpace(nodeValue))
            return ParseCsv(nodeValue);
        if (graph.Attributes.TryGetValue("output_modalities", out var graphValue) && !string.IsNullOrWhiteSpace(graphValue))
            return ParseCsv(graphValue);
        if (graph.Attributes.TryGetValue("default_output_modalities", out var defaultValue) && !string.IsNullOrWhiteSpace(defaultValue))
            return ParseCsv(defaultValue);
        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ResolveInputImages(GraphNode node, Graph graph)
    {
        return ResolveAttachmentList(node, graph, "input_images", "input_image_paths", "default_input_images");
    }

    private static IReadOnlyList<string> ResolveInputDocuments(GraphNode node, Graph graph)
    {
        return ResolveAttachmentList(node, graph, "input_documents", "input_document_paths", "default_input_documents");
    }

    private static IReadOnlyList<string> ResolveInputAudio(GraphNode node, Graph graph)
    {
        return ResolveAttachmentList(node, graph, "input_audio", "input_audio_paths", "default_input_audio");
    }

    private static IReadOnlyList<string> ResolveAttachmentList(GraphNode node, Graph graph, string primaryKey, string aliasKey, string defaultKey)
    {
        if (node.RawAttributes.TryGetValue(primaryKey, out var nodeValue) && !string.IsNullOrWhiteSpace(nodeValue))
            return ParseCsv(nodeValue);
        if (node.RawAttributes.TryGetValue(aliasKey, out var nodeAliasValue) && !string.IsNullOrWhiteSpace(nodeAliasValue))
            return ParseCsv(nodeAliasValue);
        if (graph.Attributes.TryGetValue(primaryKey, out var graphValue) && !string.IsNullOrWhiteSpace(graphValue))
            return ParseCsv(graphValue);
        if (graph.Attributes.TryGetValue(defaultKey, out var defaultValue) && !string.IsNullOrWhiteSpace(defaultValue))
            return ParseCsv(defaultValue);
        return Array.Empty<string>();
    }

    private static string ResolveInputPath(string rawPath, Graph graph)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        var expandedPath = VariableExpander.Expand(rawPath, graph.Attributes, contextValues: null, goal: graph.Goal);

        if (Path.IsPathRooted(expandedPath))
            return Path.GetFullPath(expandedPath);

        var sourcePath = graph.Attributes.TryGetValue("source_path", out var sourcePathValue) &&
                         !string.IsNullOrWhiteSpace(sourcePathValue)
            ? sourcePathValue
            : null;
        var sourceDirectory = !string.IsNullOrWhiteSpace(sourcePath)
            ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        var outputRoot = graph.Attributes.TryGetValue("output_root", out var outputRootValue) &&
                         !string.IsNullOrWhiteSpace(outputRootValue)
            ? Path.GetFullPath(outputRootValue)
            : sourceDirectory;

        var sourceCandidate = Path.GetFullPath(Path.Combine(sourceDirectory, expandedPath));
        if (File.Exists(sourceCandidate))
            return sourceCandidate;

        var outputCandidate = Path.GetFullPath(Path.Combine(outputRoot, expandedPath));
        if (File.Exists(outputCandidate))
            return outputCandidate;

        return sourceCandidate;
    }

    private static bool LooksLikeRemoteUri(string rawPath) =>
        Uri.TryCreate(rawPath, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

    private static bool IsDeferredArtifactPath(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');
        return normalized.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("gates/", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ParseCsv(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, out var parsed) ? parsed : null;
}
