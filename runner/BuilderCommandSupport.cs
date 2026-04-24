using System.Text;
using Soulcaster.Attractor;
using Soulcaster.Attractor.Validation;
using Soulcaster.UnifiedLlm;

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

    public static Graph CreateTemplate(
        string templateKind,
        string name,
        string? goal = null,
        string? referenceImage = null,
        string? referenceDocument = null)
    {
        var normalized = templateKind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "coding-loop" or "five-phase-coding" or "coding" => CreateCodingLoopTemplate(name, goal),
            "fanout-review" or "review-fanout" or "fanout" => CreateFanoutReviewTemplate(name, goal),
            "multimodal-edit-loop" or "multimodal-loop" or "image-edit-loop" => CreateMultimodalEditLoopTemplate(name, goal, referenceImage),
            "document-critique-loop" or "document-review-loop" or "doc-review-loop" => CreateDocumentCritiqueLoopTemplate(name, goal, referenceDocument),
            _ => throw new ArgumentException(
                $"Unknown workflow template '{templateKind}'. Use coding-loop, fanout-review, multimodal-edit-loop, or document-critique-loop.",
                nameof(templateKind))
        };
    }

    public static Graph Load(string dotFilePath)
    {
        var source = File.ReadAllText(dotFilePath);
        return DotParser.Parse(source);
    }

    public static Graph ApplyStandardTransforms(Graph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        IGraphTransform[] transforms =
        [
            new StylesheetTransform(),
            new VariableExpansionTransform()
        ];

        var current = graph;
        foreach (var transform in transforms)
            current = transform.Transform(current);

        return current;
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

    public static Dictionary<string, object?> BuildPreview(Graph graph, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var lintResults = Validator.Validate(graph);
        var errors = lintResults.Count(item => item.Severity == LintSeverity.Error);
        var warnings = lintResults.Count(item => item.Severity == LintSeverity.Warning);
        var controlPolicy = WorkflowPolicySupport.Build(graph);

        var nodePayloads = graph.Nodes.Values
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => BuildNodePreview(graph, node, lintResults))
            .Cast<object?>()
            .ToList();

        var edgePayloads = graph.Edges
            .Select(edge => new Dictionary<string, object?>
            {
                ["from"] = edge.FromNode,
                ["to"] = edge.ToNode,
                ["label"] = edge.Label,
                ["condition"] = edge.Condition,
                ["weight"] = edge.Weight,
                ["loop_restart"] = edge.LoopRestart,
                ["context_reset"] = edge.ContextReset
            })
            .Cast<object?>()
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["graph_name"] = graph.Name,
            ["goal"] = graph.Goal,
            ["source_path"] = sourcePath,
            ["node_count"] = graph.Nodes.Count,
            ["edge_count"] = graph.Edges.Count,
            ["lint"] = new Dictionary<string, object?>
            {
                ["error_count"] = errors,
                ["warning_count"] = warnings,
                ["issues"] = lintResults.Select(item => new Dictionary<string, object?>
                {
                    ["rule"] = item.Rule,
                    ["severity"] = item.Severity.ToString().ToLowerInvariant(),
                    ["node_id"] = item.NodeId,
                    ["edge_id"] = item.EdgeId,
                    ["message"] = item.Message
                }).Cast<object?>().ToList()
            },
            ["control_policy"] = new Dictionary<string, object?>
            {
                ["allow_force_advance"] = controlPolicy.AllowForceAdvance,
                ["force_advance_targets"] = controlPolicy.ForceAdvanceTargets,
                ["operator_retry_budget"] = controlPolicy.OperatorRetryBudget,
                ["operator_retry_stage_budget"] = controlPolicy.OperatorRetryStageBudget,
                ["retry_escalation_target"] = controlPolicy.RetryEscalationTarget,
                ["node_count"] = controlPolicy.NodePolicies.Count
            },
            ["nodes"] = nodePayloads,
            ["edges"] = edgePayloads
        };
    }

    public static string FormatPreview(Dictionary<string, object?> preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Graph: {preview.GetValueOrDefault("graph_name")}");
        sb.AppendLine($"Goal: {preview.GetValueOrDefault("goal") ?? "[none]"}");
        sb.AppendLine($"Nodes: {preview.GetValueOrDefault("node_count")}  Edges: {preview.GetValueOrDefault("edge_count")}");

        if (preview.TryGetValue("control_policy", out var controlPolicyRaw) &&
            controlPolicyRaw is Dictionary<string, object?> controlPolicy)
        {
            sb.AppendLine("Control policy:");
            sb.AppendLine($"  allow_force_advance: {controlPolicy.GetValueOrDefault("allow_force_advance")}");
            sb.AppendLine($"  operator_retry_budget: {controlPolicy.GetValueOrDefault("operator_retry_budget") ?? "unbounded"}");
            sb.AppendLine($"  operator_retry_stage_budget: {controlPolicy.GetValueOrDefault("operator_retry_stage_budget") ?? "unbounded"}");
            var targets = controlPolicy.GetValueOrDefault("force_advance_targets") as IReadOnlyList<string>;
            sb.AppendLine($"  force_advance_targets: {(targets is { Count: > 0 } ? string.Join(", ", targets) : "[any non-blocked node]")}");
        }

        if (preview.TryGetValue("nodes", out var nodesRaw) &&
            nodesRaw is IEnumerable<object?> nodes)
        {
            sb.AppendLine("Nodes:");
            foreach (var nodeRaw in nodes.OfType<Dictionary<string, object?>>())
            {
                var outputs = FormatCsv(nodeRaw.GetValueOrDefault("output_modalities"));
                var attachmentSummary = BuildAttachmentSummary(nodeRaw);
                sb.AppendLine(
                    $"  {nodeRaw.GetValueOrDefault("id")} [{nodeRaw.GetValueOrDefault("shape")}] lane={nodeRaw.GetValueOrDefault("execution_lane")} model={nodeRaw.GetValueOrDefault("model") ?? "[default]"} outputs={outputs} {attachmentSummary}");
            }
        }

        if (preview.TryGetValue("lint", out var lintRaw) &&
            lintRaw is Dictionary<string, object?> lint)
        {
            sb.AppendLine($"Lint: {lint.GetValueOrDefault("error_count")} errors, {lint.GetValueOrDefault("warning_count")} warnings");
            if (lint.TryGetValue("issues", out var issuesRaw) &&
                issuesRaw is IEnumerable<object?> issues)
            {
                foreach (var issueRaw in issues.OfType<Dictionary<string, object?>>().Take(8))
                {
                    var location = issueRaw.GetValueOrDefault("node_id") ?? issueRaw.GetValueOrDefault("edge_id") ?? "graph";
                    sb.AppendLine(
                        $"  [{issueRaw.GetValueOrDefault("severity")}] {issueRaw.GetValueOrDefault("rule")} ({location}) {issueRaw.GetValueOrDefault("message")}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Dictionary<string, object?> BuildNodePreview(
        Graph graph,
        GraphNode node,
        IReadOnlyList<LintResult> lintResults)
    {
        var inputImages = ResolveInputPaths(node, graph, "input_images", "input_image_paths")
            .Select(path => BuildAttachmentPreview(path, graph, "image"))
            .Cast<object?>()
            .ToList();
        var inputDocuments = ResolveInputPaths(node, graph, "input_documents", "input_document_paths")
            .Select(path => BuildAttachmentPreview(path, graph, "document"))
            .Cast<object?>()
            .ToList();
        var inputAudio = ResolveInputPaths(node, graph, "input_audio", "input_audio_paths")
            .Select(path => BuildAttachmentPreview(path, graph, "audio"))
            .Cast<object?>()
            .ToList();
        var nodeLint = lintResults
            .Where(item => string.Equals(item.NodeId, node.Id, StringComparison.Ordinal))
            .Select(item => new Dictionary<string, object?>
            {
                ["rule"] = item.Rule,
                ["severity"] = item.Severity.ToString().ToLowerInvariant(),
                ["message"] = item.Message
            })
            .Cast<object?>()
            .ToList();

        var outgoing = graph.OutgoingEdges(node.Id)
            .Select(edge => new Dictionary<string, object?>
            {
                ["to"] = edge.ToNode,
                ["label"] = edge.Label,
                ["condition"] = edge.Condition
            })
            .Cast<object?>()
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = node.Id,
            ["shape"] = node.Shape,
            ["label"] = node.Label,
            ["provider"] = node.LlmProvider,
            ["model"] = node.LlmModel,
            ["reasoning_effort"] = node.ReasoningEffort,
            ["execution_lane"] = ResolveExecutionLane(node, graph),
            ["output_modalities"] = ResolveOutputModalities(node, graph),
            ["resolved_input_images"] = inputImages,
            ["resolved_input_documents"] = inputDocuments,
            ["resolved_input_audio"] = inputAudio,
            ["retry_target"] = node.RetryTarget,
            ["fallback_retry_target"] = node.FallbackRetryTarget,
            ["goal_gate"] = node.GoalGate,
            ["join_policy"] = node.RawAttributes.GetValueOrDefault("join_policy"),
            ["queue_source"] = node.RawAttributes.GetValueOrDefault("queue_source"),
            ["preferred_model"] = ResolvePolicyAttribute(node, graph, "preferred_model"),
            ["fallback_models"] = ResolveCsvPolicyAttribute(node, graph, "fallback_models"),
            ["max_expected_latency_ms"] = ResolvePolicyAttribute(node, graph, "max_expected_latency_ms"),
            ["outgoing"] = outgoing,
            ["lint"] = nodeLint
        };
    }

    private static Graph CreateCodingLoopTemplate(string name, string? goal)
    {
        var graph = new Graph
        {
            Name = string.IsNullOrWhiteSpace(name) ? "coding_loop" : name,
            Goal = string.IsNullOrWhiteSpace(goal) ? "Ship a code change through plan, review, implementation, validation, and critique." : goal
        };

        graph.Attributes["goal"] = graph.Goal;
        graph.Attributes["retry_target"] = "orient";
        graph.Attributes["default_max_retry"] = "1";
        graph.Attributes["allow_force_advance"] = "false";
        graph.Attributes["operator_retry_stage_budget"] = "1";
        graph.Attributes["retry_escalation_target"] = "human-review";
        graph.Attributes["model_stylesheet"] =
            """
                * { provider = "anthropic"; model = "claude-sonnet-4-6" }
                .code { provider = "openai"; model = "gpt-5.3-codex" }
                .review { provider = "openai"; model = "gpt-5.4" }
            """;

        UpsertNode(graph, "start", new Dictionary<string, string>
        {
            ["shape"] = "Mdiamond",
            ["label"] = "Start"
        });
        UpsertNode(graph, "orient", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Orient",
            ["prompt"] = "Review the goal, repository state, and prior artifacts. Produce ORIENT-{N}.md.",
            ["class"] = "review"
        });
        UpsertNode(graph, "plan", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Plan",
            ["prompt"] = "Produce a concrete implementation plan and Definition of Done in logs/plan/PLAN-{N}.md.",
            ["class"] = "review"
        });
        UpsertNode(graph, "review_plan", new Dictionary<string, string>
        {
            ["shape"] = "hexagon",
            ["label"] = "Review Plan"
        });
        UpsertNode(graph, "implement", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Implement",
            ["prompt"] = "Implement the approved plan and update logs/implement/PROGRESS-{N}.md incrementally.",
            ["class"] = "code",
            ["max_retries"] = "1",
            ["allow_force_advance_target"] = "false"
        });
        UpsertNode(graph, "validate", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Validate",
            ["prompt"] = "Run validation and write logs/validate/VALIDATION-RUN-{N}.md.",
            ["class"] = "review",
            ["validation_mode"] = "required"
        });
        UpsertNode(graph, "critique", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Critique",
            ["prompt"] = "Produce adversarial critique and Pareto summary artifacts for the implementation.",
            ["class"] = "review"
        });
        UpsertNode(graph, "ship", new Dictionary<string, string>
        {
            ["shape"] = "hexagon",
            ["label"] = "Ship?"
        });
        UpsertNode(graph, "done", new Dictionary<string, string>
        {
            ["shape"] = "Msquare",
            ["label"] = "Done"
        });

        graph.Edges.Clear();
        UpsertEdge(graph, "start", "orient", new Dictionary<string, string>());
        UpsertEdge(graph, "orient", "plan", new Dictionary<string, string>());
        UpsertEdge(graph, "plan", "review_plan", new Dictionary<string, string>());
        UpsertEdge(graph, "review_plan", "implement", new Dictionary<string, string> { ["label"] = "approve" });
        UpsertEdge(graph, "review_plan", "plan", new Dictionary<string, string> { ["label"] = "revise" });
        UpsertEdge(graph, "implement", "validate", new Dictionary<string, string>());
        UpsertEdge(graph, "validate", "critique", new Dictionary<string, string> { ["condition"] = "outcome=success" });
        UpsertEdge(graph, "validate", "orient", new Dictionary<string, string> { ["condition"] = "outcome=fail" });
        UpsertEdge(graph, "critique", "ship", new Dictionary<string, string>());
        UpsertEdge(graph, "ship", "done", new Dictionary<string, string> { ["label"] = "ship" });
        UpsertEdge(graph, "ship", "orient", new Dictionary<string, string> { ["label"] = "iterate" });
        return graph;
    }

    private static Graph CreateFanoutReviewTemplate(string name, string? goal)
    {
        var graph = new Graph
        {
            Name = string.IsNullOrWhiteSpace(name) ? "fanout_review" : name,
            Goal = string.IsNullOrWhiteSpace(goal) ? "Collect multiple perspectives in parallel, merge them, and route through a human review gate." : goal
        };

        graph.Attributes["goal"] = graph.Goal;
        graph.Attributes["allow_force_advance"] = "true";
        graph.Attributes["force_advance_targets"] = "merge,done";

        UpsertNode(graph, "start", new Dictionary<string, string> { ["shape"] = "Mdiamond", ["label"] = "Start" });
        UpsertNode(graph, "fanout", new Dictionary<string, string>
        {
            ["shape"] = "component",
            ["label"] = "Parallel Gather",
            ["join_policy"] = "quorum"
        });
        UpsertNode(graph, "research", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Research",
            ["prompt"] = "Collect facts and produce a concise research summary."
        });
        UpsertNode(graph, "risk", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Risk Review",
            ["prompt"] = "Highlight risks, regressions, and open questions."
        });
        UpsertNode(graph, "merge", new Dictionary<string, string>
        {
            ["shape"] = "tripleoctagon",
            ["label"] = "Merge Results"
        });
        UpsertNode(graph, "review", new Dictionary<string, string>
        {
            ["shape"] = "hexagon",
            ["label"] = "Review Summary"
        });
        UpsertNode(graph, "done", new Dictionary<string, string> { ["shape"] = "Msquare", ["label"] = "Done" });

        graph.Edges.Clear();
        UpsertEdge(graph, "start", "fanout", new Dictionary<string, string>());
        UpsertEdge(graph, "fanout", "research", new Dictionary<string, string>());
        UpsertEdge(graph, "fanout", "risk", new Dictionary<string, string>());
        UpsertEdge(graph, "research", "merge", new Dictionary<string, string>());
        UpsertEdge(graph, "risk", "merge", new Dictionary<string, string>());
        UpsertEdge(graph, "merge", "review", new Dictionary<string, string>());
        UpsertEdge(graph, "review", "done", new Dictionary<string, string> { ["label"] = "approve" });
        UpsertEdge(graph, "review", "fanout", new Dictionary<string, string> { ["label"] = "rerun" });
        return graph;
    }

    private static Graph CreateMultimodalEditLoopTemplate(string name, string? goal, string? referenceImage)
    {
        var graph = new Graph
        {
            Name = string.IsNullOrWhiteSpace(name) ? "multimodal_edit_loop" : name,
            Goal = string.IsNullOrWhiteSpace(goal)
                ? "Generate a visual draft from a reference image, review it, and refine it over a shared multimodal thread."
                : goal
        };

        graph.Attributes["goal"] = graph.Goal;
        graph.Attributes["allow_force_advance"] = "false";
        graph.Attributes["operator_retry_stage_budget"] = "1";
        graph.Attributes["reference_image"] = referenceImage ?? string.Empty;

        UpsertNode(graph, "start", new Dictionary<string, string>
        {
            ["shape"] = "Mdiamond",
            ["label"] = "Start"
        });
        UpsertNode(graph, "generate", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Generate Draft",
            ["provider"] = "openai",
            ["model"] = "gpt-5.4",
            ["execution_lane"] = "multimodal_leaf",
            ["output_modalities"] = "text,image",
            ["thread_id"] = "multimodal-thread",
            ["input_images"] = "$reference_image",
            ["prompt"] = "Use the attached reference image when present. Produce a first-pass visual draft and return valid stage status JSON."
        });
        UpsertNode(graph, "review_visual", new Dictionary<string, string>
        {
            ["shape"] = "hexagon",
            ["label"] = "Review Visual"
        });
        UpsertNode(graph, "refine", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Refine Visual",
            ["provider"] = "openai",
            ["model"] = "gpt-5.4",
            ["execution_lane"] = "multimodal_leaf",
            ["output_modalities"] = "text,image",
            ["thread_id"] = "multimodal-thread",
            ["input_images"] = "logs/generate/generated/image-1.png,$reference_image",
            ["prompt"] = "Refine the approved visual direction over the shared multimodal thread and return valid stage status JSON."
        });
        UpsertNode(graph, "done", new Dictionary<string, string>
        {
            ["shape"] = "Msquare",
            ["label"] = "Done"
        });

        graph.Edges.Clear();
        UpsertEdge(graph, "start", "generate", new Dictionary<string, string>());
        UpsertEdge(graph, "generate", "review_visual", new Dictionary<string, string>());
        UpsertEdge(graph, "review_visual", "refine", new Dictionary<string, string> { ["label"] = "approve" });
        UpsertEdge(graph, "review_visual", "generate", new Dictionary<string, string> { ["label"] = "revise" });
        UpsertEdge(graph, "refine", "done", new Dictionary<string, string>());
        return graph;
    }

    private static Graph CreateDocumentCritiqueLoopTemplate(string name, string? goal, string? referenceDocument)
    {
        var graph = new Graph
        {
            Name = string.IsNullOrWhiteSpace(name) ? "document_critique_loop" : name,
            Goal = string.IsNullOrWhiteSpace(goal)
                ? "Review a source document, critique it, and iterate through a human approval gate."
                : goal
        };

        graph.Attributes["goal"] = graph.Goal;
        graph.Attributes["allow_force_advance"] = "false";
        graph.Attributes["operator_retry_stage_budget"] = "1";
        graph.Attributes["reference_document"] = referenceDocument ?? string.Empty;

        UpsertNode(graph, "start", new Dictionary<string, string>
        {
            ["shape"] = "Mdiamond",
            ["label"] = "Start"
        });
        UpsertNode(graph, "review_document", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Review Document",
            ["provider"] = "gemini",
            ["model"] = "gemini-2.5-pro",
            ["execution_lane"] = "leaf",
            ["input_documents"] = "$reference_document",
            ["prompt"] = "Read the attached document and return valid stage status JSON with the strongest critique or summary."
        });
        UpsertNode(graph, "review_gate", new Dictionary<string, string>
        {
            ["shape"] = "hexagon",
            ["label"] = "Approve Critique"
        });
        UpsertNode(graph, "revise_document", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Revise Critique",
            ["provider"] = "gemini",
            ["model"] = "gemini-2.5-pro",
            ["execution_lane"] = "leaf",
            ["input_documents"] = "$reference_document",
            ["prompt"] = "Use the attached document plus steering to revise the critique and return valid stage status JSON."
        });
        UpsertNode(graph, "done", new Dictionary<string, string>
        {
            ["shape"] = "Msquare",
            ["label"] = "Done"
        });

        graph.Edges.Clear();
        UpsertEdge(graph, "start", "review_document", new Dictionary<string, string>());
        UpsertEdge(graph, "review_document", "review_gate", new Dictionary<string, string>());
        UpsertEdge(graph, "review_gate", "done", new Dictionary<string, string> { ["label"] = "approve" });
        UpsertEdge(graph, "review_gate", "revise_document", new Dictionary<string, string> { ["label"] = "revise" });
        UpsertEdge(graph, "revise_document", "done", new Dictionary<string, string>());
        return graph;
    }

    private static string ResolveExecutionLane(GraphNode node, Graph graph)
    {
        return (ResolvePolicyAttribute(node, graph, "execution_lane")
                ?? ResolvePolicyAttribute(node, graph, "lane")
                ?? "agent")
            .Trim()
            .ToLowerInvariant();
    }

    private static IReadOnlyList<string> ResolveOutputModalities(GraphNode node, Graph graph) =>
        ResolveCsvPolicyAttribute(node, graph, "output_modalities");

    private static IReadOnlyList<string> ResolveInputPaths(GraphNode node, Graph graph, params string[] keys)
    {
        string? raw = null;
        foreach (var key in keys)
        {
            raw = ResolvePolicyAttribute(node, graph, key);
            if (!string.IsNullOrWhiteSpace(raw))
                break;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, object?> BuildAttachmentPreview(string path, Graph graph, string kind)
    {
        var resolvedPath = ResolveInputPath(path, graph);
        var availability = IsDeferredArtifactPath(path) ? "runtime_artifact" : "source_file";
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["declared_path"] = path,
            ["resolved_path"] = resolvedPath,
            ["exists"] = !string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath),
            ["availability"] = availability,
            ["media_type"] = GetMediaType(resolvedPath)
        };
    }

    private static string ResolveInputPath(string rawPath, Graph graph)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        var expandedPath = VariableExpander.Expand(rawPath, graph.Attributes, contextValues: null, goal: graph.Goal);

        if (Path.IsPathRooted(expandedPath))
            return Path.GetFullPath(expandedPath);

        var sourcePath = graph.Attributes.TryGetValue("source_path", out var sourcePathValue)
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

    private static string GetMediaType(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }

    private static string BuildAttachmentSummary(Dictionary<string, object?> nodeRaw)
    {
        var imageInputs = ReadAttachmentPreviewList(nodeRaw, "resolved_input_images");
        var documentInputs = ReadAttachmentPreviewList(nodeRaw, "resolved_input_documents");
        var audioInputs = ReadAttachmentPreviewList(nodeRaw, "resolved_input_audio");
        var totalInputs = imageInputs.Count + documentInputs.Count + audioInputs.Count;
        if (totalInputs == 0)
            return "inputs=0";

        var missing = CountMissingAttachments(imageInputs) + CountMissingAttachments(documentInputs) + CountMissingAttachments(audioInputs);
        var parts = new List<string>
        {
            $"inputs={totalInputs}",
            $"images={imageInputs.Count}",
            $"docs={documentInputs.Count}",
            $"audio={audioInputs.Count}"
        };
        if (missing > 0)
            parts.Add($"missing={missing}");

        return string.Join(" ", parts);
    }

    private static List<Dictionary<string, object?>> ReadAttachmentPreviewList(
        IReadOnlyDictionary<string, object?> nodeRaw,
        string key)
    {
        return nodeRaw.TryGetValue(key, out var rawValue) &&
               rawValue is IEnumerable<object?> rawItems
            ? rawItems.OfType<Dictionary<string, object?>>().ToList()
            : new List<Dictionary<string, object?>>();
    }

    private static int CountMissingAttachments(IEnumerable<Dictionary<string, object?>> attachments)
    {
        return attachments.Count(item =>
            item.TryGetValue("exists", out var existsValue) &&
            existsValue is bool exists &&
            !exists &&
            (!item.TryGetValue("availability", out var availabilityValue) ||
             !string.Equals(availabilityValue?.ToString(), "runtime_artifact", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsDeferredArtifactPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("gates/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCsv(object? value)
    {
        if (value is IEnumerable<string> strings)
            return strings.Any() ? string.Join(",", strings) : "[default]";

        if (value is IEnumerable<object?> objects)
        {
            var items = objects
                .Where(item => item is not null)
                .Select(item => item!.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
            return items.Count > 0 ? string.Join(",", items) : "[default]";
        }

        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? "[default]" : text;
    }

    private static string? ResolvePolicyAttribute(GraphNode node, Graph graph, string key)
    {
        if (node.RawAttributes.TryGetValue(key, out var nodeValue) && !string.IsNullOrWhiteSpace(nodeValue))
            return nodeValue.Trim();

        if (!string.IsNullOrWhiteSpace(node.Class) &&
            graph.Attributes.TryGetValue($"{node.Class}_{key}", out var classValue) &&
            !string.IsNullOrWhiteSpace(classValue))
        {
            return classValue.Trim();
        }

        if (graph.Attributes.TryGetValue(key, out var graphValue) && !string.IsNullOrWhiteSpace(graphValue))
            return graphValue.Trim();

        return graph.Attributes.TryGetValue($"default_{key}", out var defaultValue) &&
               !string.IsNullOrWhiteSpace(defaultValue)
            ? defaultValue.Trim()
            : null;
    }

    private static IReadOnlyList<string> ResolveCsvPolicyAttribute(GraphNode node, Graph graph, string key)
    {
        var raw = ResolvePolicyAttribute(node, graph, key);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
