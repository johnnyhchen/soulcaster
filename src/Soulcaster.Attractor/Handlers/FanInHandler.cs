namespace Soulcaster.Attractor.Handlers;

using System.Text.Json;

public class FanInHandler : INodeHandler
{
    private readonly ICodergenBackend? _backend;

    public FanInHandler(ICodergenBackend? backend = null)
    {
        _backend = backend;
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Read parallel.results from context
        var resultsJson = context.Get("parallel.results");
        if (string.IsNullOrEmpty(resultsJson))
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Fan-in node '{node.Id}' failed: parallel.results was missing.");
        }

        List<Dictionary<string, object?>>? branchResults;
        try
        {
            branchResults = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(resultsJson);
        }
        catch
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Fan-in node '{node.Id}' failed: parallel.results was malformed.");
        }

        if (branchResults is null || branchResults.Count == 0)
        {
            return new Outcome(OutcomeStatus.Fail, Notes: $"Fan-in node '{node.Id}' failed: parallel.results was empty.");
        }

        // If node has a prompt, use LLM-based evaluation
        if (!string.IsNullOrEmpty(node.Prompt) && _backend is not null)
        {
            var evaluationPrompt = $"{node.Prompt}\n\nBranch results:\n{resultsJson}";
            var result = await _backend.RunAsync(evaluationPrompt, ct: ct);

            // Write evaluation output
            var stageDir = Path.Combine(logsRoot, node.Id);
            Directory.CreateDirectory(stageDir);
            await File.WriteAllTextAsync(Path.Combine(stageDir, "evaluation.md"), result.Response, ct);

            return new Outcome(
                Status: result.Status,
                ContextUpdates: result.ContextUpdates,
                Notes: $"Fan-in node '{node.Id}' evaluated {branchResults.Count} branches via LLM."
            );
        }

        // Heuristic ranking: sort by (outcome_rank, node_id)
        var ranked = branchResults
            .OrderBy(r => OutcomeRank(r.GetValueOrDefault("status")?.ToString()))
            .ThenBy(r => r.GetValueOrDefault("node_id")?.ToString())
            .ToList();

        var bestResult = ranked.FirstOrDefault();
        var bestStatus = NormalizeStatus(bestResult?.GetValueOrDefault("status")?.ToString());

        var combinedStatus = bestStatus switch
        {
            "success" => OutcomeStatus.Success,
            "partialsuccess" => OutcomeStatus.PartialSuccess,
            _ => OutcomeStatus.Fail
        };

        // Store the ranked results for downstream consumption
        context.Set("fan_in.ranked_results", JsonSerializer.Serialize(ranked));

        if (branchResults.Any(result => result.ContainsKey("queue_item") || result.ContainsKey("queue_item_id")))
        {
            var queueSummary = new Dictionary<string, object?>
            {
                ["total_items"] = branchResults.Count,
                ["success_count"] = branchResults.Count(result => NormalizeStatus(result.GetValueOrDefault("status")?.ToString()) == "success"),
                ["partial_success_count"] = branchResults.Count(result => NormalizeStatus(result.GetValueOrDefault("status")?.ToString()) == "partialsuccess"),
                ["fail_count"] = branchResults.Count(result => NormalizeStatus(result.GetValueOrDefault("status")?.ToString()) == "fail"),
                ["best_item"] = bestResult?.GetValueOrDefault("node_id")
            };
            context.Set("fan_in.queue_summary", JsonSerializer.Serialize(queueSummary));
        }

        return new Outcome(
            Status: combinedStatus,
            Notes: $"Fan-in node '{node.Id}' merged {branchResults.Count} branches. Best: {bestResult?.GetValueOrDefault("node_id")}."
        );
    }

    private static int OutcomeRank(string? status) => NormalizeStatus(status) switch
    {
        "success" => 0,
        "partialsuccess" => 1,
        "retry" => 2,
        "fail" => 3,
        _ => 4
    };

    private static string NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return string.Empty;

        return status.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
