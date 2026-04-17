namespace Soulcaster.Runner;

using System.Text.Json;
using Soulcaster.Attractor;

public static class ConformanceCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        var subcommand = args.Length > 0 ? args[0] : "help";

        try
        {
            return subcommand.ToLowerInvariant() switch
            {
                "parse" => await ParseAsync(args[1..]),
                "validate" => await ValidateAsync(args[1..]),
                "run" => await RunPipelineAsync(args[1..]),
                "list-handlers" => await ListHandlersAsync(),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => ShowHelp()
            };
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(new Dictionary<string, object?>
            {
                ["status"] = "error",
                ["error"] = ex.Message
            });
            return 1;
        }
    }

    private static async Task<int> ParseAsync(string[] args)
    {
        var dotFilePath = RequireDotFilePath(args);
        var graph = DotParser.Parse(await File.ReadAllTextAsync(dotFilePath));
        await WriteJsonAsync(SerializeGraph(graph));
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        var dotFilePath = RequireDotFilePath(args);
        var graph = DotParser.Parse(await File.ReadAllTextAsync(dotFilePath));
        var diagnostics = Validator.Validate(graph)
            .Select(result => new Dictionary<string, object?>
            {
                ["rule"] = result.Rule,
                ["severity"] = result.Severity == LintSeverity.Error ? "error" : "warning",
                ["node_id"] = result.NodeId,
                ["edge_id"] = result.EdgeId,
                ["message"] = result.Message
            })
            .ToList();

        await WriteJsonAsync(new Dictionary<string, object?>
        {
            ["diagnostics"] = diagnostics
        });

        return diagnostics.Any(diagnostic => string.Equals(diagnostic["severity"]?.ToString(), "error", StringComparison.Ordinal))
            ? 1
            : 0;
    }

    private static async Task<int> RunPipelineAsync(string[] args)
    {
        var parsed = RunOptions.Parse(args);
        var options = parsed with
        {
            Resume = false,
            AutoResumePolicy = AutoResumePolicy.Off
        };

        var dotFilePath = Path.GetFullPath(options.DotFilePath);
        if (!File.Exists(dotFilePath))
            throw new FileNotFoundException($"DOT file not found: {dotFilePath}", dotFilePath);

        var workingDir = RunCommandSupport.ResolveWorkingDirectory(dotFilePath, options);
        if (Directory.Exists(workingDir))
            Directory.Delete(workingDir, recursive: true);

        Directory.CreateDirectory(workingDir);
        var logsDir = Path.Combine(workingDir, "logs");
        Directory.CreateDirectory(logsDir);

        var projectRoot = RunCommandSupport.ResolveProjectRoot(dotFilePath);
        var graph = DotParser.Parse(await File.ReadAllTextAsync(dotFilePath));
        graph.Attributes["source_path"] = dotFilePath;
        graph.Attributes["project_root"] = projectRoot;
        graph.Attributes["output_root"] = workingDir;
        foreach (var (key, value) in options.Variables ?? new Dictionary<string, string>(StringComparer.Ordinal))
            graph.Attributes[key] = value;

        using var backend = RunnerBackendFactory.Create(workingDir, projectRoot, options);
        var engine = new PipelineEngine(new PipelineConfig(
            LogsRoot: logsDir,
            Interviewer: new AutoApproveInterviewer(),
            Backend: backend,
            Transforms: new List<IGraphTransform>
            {
                new StylesheetTransform(),
                new VariableExpansionTransform()
            }));

        var result = await engine.RunAsync(graph);
        var nodeStatuses = result.NodeOutcomes.ToDictionary(
            pair => pair.Key,
            pair => StageStatusContract.ToStatusString(pair.Value.Status),
            StringComparer.Ordinal);

        await WriteJsonAsync(new Dictionary<string, object?>
        {
            ["status"] = StageStatusContract.ToStatusString(result.Status),
            ["completed_nodes"] = result.CompletedNodes,
            ["node_statuses"] = nodeStatuses,
            ["working_directory"] = workingDir,
            ["logs_directory"] = logsDir,
            ["context"] = new Dictionary<string, string>(result.FinalContext.All, StringComparer.Ordinal)
        });

        return 0;
    }

    private static async Task<int> ListHandlersAsync()
    {
        var registry = new HandlerRegistry();
        await WriteJsonAsync(registry.GetRegisteredShapes());
        return 0;
    }

    private static string RequireDotFilePath(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            throw new ArgumentException("A DOT file path is required.");

        return Path.GetFullPath(args[0]);
    }

    private static Dictionary<string, object?> SerializeGraph(Graph graph)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = graph.Name,
            ["goal"] = graph.Goal,
            ["label"] = graph.Label,
            ["default_max_retries"] = graph.DefaultMaxRetry,
            ["default_fidelity"] = graph.DefaultFidelity,
            ["attributes"] = new Dictionary<string, string>(graph.Attributes, StringComparer.Ordinal),
            ["nodes"] = graph.Nodes.Values
                .OrderBy(node => node.Id, StringComparer.Ordinal)
                .Select(SerializeNode)
                .ToList(),
            ["edges"] = graph.Edges
                .Select(SerializeEdge)
                .ToList()
        };
    }

    private static Dictionary<string, object?> SerializeNode(GraphNode node)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = node.Id,
            ["label"] = node.Label,
            ["shape"] = node.Shape,
            ["prompt"] = node.Prompt,
            ["max_retries"] = node.MaxRetries,
            ["goal_gate"] = node.GoalGate,
            ["retry_target"] = node.RetryTarget,
            ["fallback_retry_target"] = node.FallbackRetryTarget,
            ["fidelity"] = node.Fidelity,
            ["thread_id"] = node.ThreadId,
            ["class"] = node.Class,
            ["timeout"] = node.Timeout,
            ["llm_model"] = node.LlmModel,
            ["llm_provider"] = node.LlmProvider,
            ["reasoning_effort"] = node.ReasoningEffort,
            ["attributes"] = new Dictionary<string, string>(node.RawAttributes, StringComparer.Ordinal)
        };
    }

    private static Dictionary<string, object?> SerializeEdge(GraphEdge edge)
    {
        return new Dictionary<string, object?>
        {
            ["from"] = edge.FromNode,
            ["to"] = edge.ToNode,
            ["label"] = edge.Label,
            ["condition"] = edge.Condition,
            ["weight"] = edge.Weight,
            ["fidelity"] = edge.Fidelity,
            ["thread_id"] = edge.ThreadId,
            ["loop_restart"] = edge.LoopRestart,
            ["context_reset"] = edge.ContextReset,
            ["attributes"] = BuildEdgeAttributes(edge)
        };
    }

    private static Dictionary<string, object?> BuildEdgeAttributes(GraphEdge edge)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(edge.Label))
            attributes["label"] = edge.Label;
        if (!string.IsNullOrWhiteSpace(edge.Condition))
            attributes["condition"] = edge.Condition;
        if (edge.Weight != 0)
            attributes["weight"] = edge.Weight;
        if (!string.IsNullOrWhiteSpace(edge.Fidelity))
            attributes["fidelity"] = edge.Fidelity;
        if (!string.IsNullOrWhiteSpace(edge.ThreadId))
            attributes["thread_id"] = edge.ThreadId;
        if (edge.LoopRestart)
            attributes["loop_restart"] = true;
        if (edge.ContextReset)
            attributes["context_reset"] = true;
        return attributes;
    }

    private static async Task WriteJsonAsync(object payload)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static int ShowHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  attractor conformance parse <dotfile>");
        Console.WriteLine("  attractor conformance validate <dotfile>");
        Console.WriteLine("  attractor conformance run <dotfile> [run options]");
        Console.WriteLine("  attractor conformance list-handlers");
        return 1;
    }
}
