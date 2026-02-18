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
