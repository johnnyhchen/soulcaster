namespace JcAttractor.Attractor;

using System.Text.Json;

public class ParallelHandler : INodeHandler
{
    private readonly HandlerRegistry _registry;

    public ParallelHandler(HandlerRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        // Fan out to multiple target nodes via outgoing edges
        var outgoingEdges = graph.OutgoingEdges(node.Id);
        if (outgoingEdges.Count == 0)
        {
            return new Outcome(OutcomeStatus.Success, Notes: $"Parallel node '{node.Id}' has no outgoing edges.");
        }

        // Read policy attributes from node
        var joinPolicy = node.RawAttributes.GetValueOrDefault("join_policy", "wait_all");
        var errorPolicy = node.RawAttributes.GetValueOrDefault("error_policy", "continue");
        int maxParallel = node.RawAttributes.TryGetValue("max_parallel", out var mpStr) && int.TryParse(mpStr, out var mp) ? mp : 0;

        var targetNodeIds = outgoingEdges.Select(e => e.ToNode).Distinct().ToList();

        // Build tasks with isolated context clones per branch
        SemaphoreSlim? semaphore = maxParallel > 0 ? new SemaphoreSlim(maxParallel) : null;
        var failFastCts = errorPolicy == "fail_fast" ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        var effectiveCt = failFastCts?.Token ?? ct;

        var tasks = new List<Task<BranchResult>>();

        foreach (var targetId in targetNodeIds)
        {
            if (!graph.Nodes.TryGetValue(targetId, out _))
                continue;

            // Clone context for branch isolation — spec says branches get isolated context
            var branchContext = context.Clone();
            tasks.Add(ExecuteBranchSubgraphAsync(targetId, branchContext, graph, logsRoot, effectiveCt, semaphore, failFastCts));
        }

        BranchResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (failFastCts is not null && !ct.IsCancellationRequested)
        {
            // Fail-fast triggered — gather completed results
            results = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToArray();
        }
        finally
        {
            semaphore?.Dispose();
            failFastCts?.Dispose();
        }

        // Build parallel.results for fan-in — don't merge branch context back into parent
        var parallelResults = new List<Dictionary<string, object?>>();
        var allSuccess = true;
        var anyFail = false;
        var notes = new List<string>();

        foreach (var result in results)
        {
            parallelResults.Add(new Dictionary<string, object?>
            {
                ["node_id"] = result.EntryNodeId,
                ["status"] = result.CombinedStatus.ToString().ToLowerInvariant(),
                ["notes"] = result.Notes,
                ["completed_nodes"] = result.CompletedNodes
            });

            if (result.CombinedStatus != OutcomeStatus.Success)
                allSuccess = false;
            if (result.CombinedStatus == OutcomeStatus.Fail)
                anyFail = true;

            notes.Add($"{result.EntryNodeId}: {result.CombinedStatus} ({result.CompletedNodes.Count} nodes)");
        }

        // Store parallel results in context for fan-in consumption
        var contextUpdates = new Dictionary<string, string>
        {
            ["parallel.results"] = JsonSerializer.Serialize(parallelResults)
        };

        // Determine combined status based on join policy
        var combinedStatus = joinPolicy switch
        {
            "first_success" => results.Any(r => r.CombinedStatus == OutcomeStatus.Success) ? OutcomeStatus.Success : OutcomeStatus.Fail,
            "quorum" => results.Count(r => r.CombinedStatus == OutcomeStatus.Success) > results.Length / 2 ? OutcomeStatus.Success : OutcomeStatus.Fail,
            _ => allSuccess ? OutcomeStatus.Success : anyFail ? OutcomeStatus.Fail : OutcomeStatus.PartialSuccess // wait_all / k_of_n
        };

        // For "ignore" error policy, always succeed
        if (errorPolicy == "ignore")
            combinedStatus = OutcomeStatus.Success;

        return new Outcome(
            Status: combinedStatus,
            ContextUpdates: contextUpdates,
            Notes: $"Parallel node '{node.Id}' completed ({joinPolicy}): {string.Join(", ", notes)}"
        );
    }

    private record BranchResult(
        string EntryNodeId,
        OutcomeStatus CombinedStatus,
        string Notes,
        List<string> CompletedNodes);

    /// <summary>
    /// Executes a full subgraph traversal starting from entryNodeId.
    /// Walks the graph node-by-node (execute → select edge → advance) until:
    ///   - A fan-in node (tripleoctagon) is reached (stop BEFORE executing it)
    ///   - A dead end (no outgoing edges) is reached
    ///   - An exit node (Msquare) is reached
    ///   - A failure with no recovery path occurs
    /// </summary>
    private async Task<BranchResult> ExecuteBranchSubgraphAsync(
        string entryNodeId,
        PipelineContext branchContext,
        Graph graph,
        string logsRoot,
        CancellationToken ct,
        SemaphoreSlim? semaphore,
        CancellationTokenSource? failFastCts)
    {
        if (semaphore is not null)
            await semaphore.WaitAsync(ct);

        try
        {
            return await ExecuteSubgraphAsync(entryNodeId, branchContext, graph, logsRoot, ct, failFastCts);
        }
        finally
        {
            semaphore?.Release();
        }
    }

    private async Task<BranchResult> ExecuteSubgraphAsync(
        string entryNodeId,
        PipelineContext branchContext,
        Graph graph,
        string logsRoot,
        CancellationToken ct,
        CancellationTokenSource? failFastCts)
    {
        var completedNodes = new List<string>();
        var currentNodeId = entryNodeId;
        var lastOutcome = new Outcome(OutcomeStatus.Success);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!graph.Nodes.TryGetValue(currentNodeId, out var currentNode))
            {
                return new BranchResult(entryNodeId, OutcomeStatus.Fail,
                    $"Node '{currentNodeId}' not found in graph.", completedNodes);
            }

            // Stop BEFORE executing fan-in nodes — they're handled by the engine after parallel completes
            if (currentNode.Shape.Equals("tripleoctagon", StringComparison.OrdinalIgnoreCase))
            {
                return new BranchResult(entryNodeId,
                    completedNodes.Count > 0 ? lastOutcome.Status : OutcomeStatus.Success,
                    $"Branch reached fan-in node '{currentNodeId}'.", completedNodes);
            }

            // Stop at exit nodes
            if (currentNode.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase))
            {
                return new BranchResult(entryNodeId, lastOutcome.Status,
                    $"Branch reached exit node '{currentNodeId}'.", completedNodes);
            }

            // Execute the node
            var handler = _registry.GetHandler(currentNode.Shape);
            if (handler == null)
            {
                return new BranchResult(entryNodeId, OutcomeStatus.Fail,
                    $"No handler for shape '{currentNode.Shape}' on node '{currentNodeId}'.", completedNodes);
            }

            Outcome outcome;
            try
            {
                outcome = await handler.ExecuteAsync(currentNode, branchContext, graph, logsRoot, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Fail-fast from another branch
                return new BranchResult(entryNodeId, OutcomeStatus.Fail,
                    $"Branch cancelled at '{currentNodeId}'.", completedNodes);
            }
            catch (Exception ex)
            {
                outcome = new Outcome(OutcomeStatus.Fail, Notes: $"Error in {currentNodeId}: {ex.Message}");
            }

            // Apply context updates within the branch
            branchContext.MergeUpdates(outcome.ContextUpdates);
            completedNodes.Add(currentNodeId);
            lastOutcome = outcome;

            // On failure, trigger fail-fast if configured and stop this branch
            if (outcome.Status == OutcomeStatus.Fail)
            {
                if (failFastCts is not null)
                    await failFastCts.CancelAsync();

                return new BranchResult(entryNodeId, OutcomeStatus.Fail,
                    $"Branch failed at '{currentNodeId}': {outcome.Notes}", completedNodes);
            }

            // Select next edge
            var outgoingEdges = graph.OutgoingEdges(currentNodeId);
            if (outgoingEdges.Count == 0)
            {
                // Dead end — branch is done
                return new BranchResult(entryNodeId, outcome.Status,
                    $"Branch completed at dead end '{currentNodeId}'.", completedNodes);
            }

            var selectedEdge = EdgeSelector.SelectEdge(outgoingEdges, outcome, branchContext);
            if (selectedEdge == null)
            {
                return new BranchResult(entryNodeId, OutcomeStatus.Fail,
                    $"No matching edge from '{currentNodeId}'.", completedNodes);
            }

            currentNodeId = selectedEdge.ToNode;
        }
    }
}
