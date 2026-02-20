namespace JcAttractor.Attractor;

using System.Text.Json;

public interface ICodergenBackend
{
    Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, string? reasoningEffort = null, CancellationToken ct = default);
}

public record CodergenResult(string Response, OutcomeStatus Status, Dictionary<string, string>? ContextUpdates = null);

public class CodergenHandler : INodeHandler
{
    private readonly ICodergenBackend _backend;

    public CodergenHandler(ICodergenBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Expand $goal in prompt
        string expandedPrompt = ExpandVariables(node.Prompt, context, graph);

        // Prepend deterministic file-system preamble so the agent always
        // knows the exact directory layout — no guessing, no hardcoded paths.
        expandedPrompt = BuildPreamble(node, graph, logsRoot) + expandedPrompt;

        // Create stage directory
        string stageDir = Path.Combine(logsRoot, node.Id);
        Directory.CreateDirectory(stageDir);

        // Write prompt
        await File.WriteAllTextAsync(Path.Combine(stageDir, "prompt.md"), expandedPrompt, ct);

        // Execute backend
        string? model = !string.IsNullOrEmpty(node.LlmModel) ? node.LlmModel : null;
        string? provider = !string.IsNullOrEmpty(node.LlmProvider) ? node.LlmProvider : null;

        CodergenResult result;
        try
        {
            string? reasoningEffort = !string.IsNullOrEmpty(node.ReasoningEffort) ? node.ReasoningEffort : null;
            result = await _backend.RunAsync(expandedPrompt, model, provider, reasoningEffort, ct);
        }
        catch (Exception ex)
        {
            result = new CodergenResult(
                Response: $"Error: {ex.Message}",
                Status: OutcomeStatus.Fail
            );
        }

        // Write response
        await File.WriteAllTextAsync(Path.Combine(stageDir, "response.md"), result.Response, ct);

        // Build outcome
        var outcome = new Outcome(
            Status: result.Status,
            ContextUpdates: result.ContextUpdates,
            Notes: $"Codergen node '{node.Id}' completed with status {result.Status}."
        );

        // Write status.json
        var statusData = new Dictionary<string, object?>
        {
            ["node_id"] = node.Id,
            ["status"] = outcome.Status.ToString().ToLowerInvariant(),
            ["notes"] = outcome.Notes,
            ["model"] = model,
            ["provider"] = provider
        };
        await File.WriteAllTextAsync(
            Path.Combine(stageDir, "status.json"),
            JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
            ct
        );

        return outcome;
    }

    private static string BuildPreamble(GraphNode node, Graph graph, string logsRoot)
    {
        // logsRoot is the absolute path to the logs/ directory.
        // The agent's working directory is its parent (the run output root).
        var outputRoot = Path.GetFullPath(Path.Combine(logsRoot, ".."));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[PIPELINE CONTEXT]");
        sb.AppendLine($"You are executing node \"{node.Id}\" in pipeline \"{graph.Name}\".");
        sb.AppendLine();
        sb.AppendLine("Your working directory (CWD) is:");
        sb.AppendLine($"  {outputRoot}");
        sb.AppendLine();
        sb.AppendLine("Artifact directory layout (relative to CWD):");
        sb.AppendLine("  logs/                          <- all pipeline artifacts");

        // List each node that has a logs subdirectory, marking which exist
        foreach (var n in graph.Nodes.Values.OrderBy(n => n.Id))
        {
            if (n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase) ||
                n.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase) ||
                n.Shape.Equals("hexagon", StringComparison.OrdinalIgnoreCase))
                continue; // Skip terminals and gates — they don't produce log dirs

            var nodeDir = Path.Combine(logsRoot, n.Id);
            var exists = Directory.Exists(nodeDir);
            var marker = exists ? "(exists)" : "(not yet created)";
            sb.AppendLine($"  logs/{n.Id,-25} {marker}");
        }

        sb.AppendLine();
        sb.AppendLine("Use RELATIVE paths (e.g. logs/breakdown/BREAKDOWN-1.md) to read/write artifacts.");
        sb.AppendLine("Do NOT hardcode absolute paths for pipeline artifacts.");
        sb.AppendLine("[/PIPELINE CONTEXT]");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string ExpandVariables(string prompt, PipelineContext context, Graph graph)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        var expanded = prompt.Replace("$goal", graph.Goal);

        // Expand context variables: ${context.key}
        foreach (var (key, value) in context.All)
        {
            expanded = expanded.Replace($"${{context.{key}}}", value);
        }

        return expanded;
    }
}
