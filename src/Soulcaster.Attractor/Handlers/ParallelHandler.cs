namespace Soulcaster.Attractor.Handlers;

using System.Text.Json;

public class ParallelHandler : INodeHandler
{
    private readonly HandlerRegistry _registry;
    private readonly ICodergenBackend? _backend;

    public ParallelHandler(HandlerRegistry registry, ICodergenBackend? backend = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _backend = backend;
    }

    public async Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        var outgoingEdges = graph.OutgoingEdges(node.Id);
        if (outgoingEdges.Count == 0)
            return new Outcome(OutcomeStatus.Success, Notes: $"Parallel node '{node.Id}' has no outgoing edges.");

        var joinPolicy = node.RawAttributes.GetValueOrDefault("join_policy", "wait_all");
        var errorPolicy = node.RawAttributes.GetValueOrDefault("error_policy", "continue");
        int maxParallel = node.RawAttributes.TryGetValue("max_parallel", out var mpStr) && int.TryParse(mpStr, out var mp) ? mp : 0;
        var queueSource = node.RawAttributes.GetValueOrDefault("queue_source", "").Trim();

        SemaphoreSlim? semaphore = maxParallel > 0 ? new SemaphoreSlim(maxParallel) : null;
        var failFastCts = errorPolicy == "fail_fast" ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        var effectiveCt = failFastCts?.Token ?? ct;
        var tasks = new List<Task<BranchResult>>();
        var queueMode = !string.IsNullOrWhiteSpace(queueSource);

        if (queueMode)
        {
            var workerTargets = outgoingEdges.Select(edge => edge.ToNode).Distinct().ToList();
            if (workerTargets.Count != 1)
            {
                return new Outcome(
                    OutcomeStatus.Fail,
                    Notes: $"Queue-backed parallel node '{node.Id}' must have exactly one outgoing worker edge. Found {workerTargets.Count}.");
            }

            List<QueueWorkItem> queueItems;
            try
            {
                queueItems = ParallelQueueLoader.Load(queueSource, logsRoot);
            }
            catch (Exception ex)
            {
                return new Outcome(
                    OutcomeStatus.Fail,
                    Notes: $"Queue-backed parallel node '{node.Id}' could not load queue source '{queueSource}': {ex.Message}");
            }

            if (queueItems.Count == 0)
            {
                return new Outcome(
                    OutcomeStatus.Success,
                    ContextUpdates: new Dictionary<string, string>
                    {
                        ["parallel.results"] = "[]",
                        ["parallel.queue.count"] = "0"
                    },
                    Notes: $"Queue-backed parallel node '{node.Id}' found no work items in '{queueSource}'.");
            }

            var workerEntryNodeId = workerTargets[0];
            foreach (var queueItem in queueItems)
            {
                var branchContext = context.Clone();
                ApplyQueueItemContext(branchContext, queueItem);
                tasks.Add(ExecuteBranchSubgraphAsync(
                    workerEntryNodeId,
                    branchContext,
                    graph,
                    logsRoot,
                    effectiveCt,
                    semaphore,
                    failFastCts,
                    queueItem));
            }
        }
        else
        {
            foreach (var targetId in outgoingEdges.Select(edge => edge.ToNode).Distinct())
            {
                if (!graph.Nodes.ContainsKey(targetId))
                    continue;

                var branchContext = context.Clone();
                tasks.Add(ExecuteBranchSubgraphAsync(
                    targetId,
                    branchContext,
                    graph,
                    logsRoot,
                    effectiveCt,
                    semaphore,
                    failFastCts));
            }
        }

        BranchResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (failFastCts is not null && !ct.IsCancellationRequested)
        {
            results = tasks.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result).ToArray();
        }
        finally
        {
            semaphore?.Dispose();
            failFastCts?.Dispose();
        }

        var parallelResults = new List<Dictionary<string, object?>>();
        var allSuccess = true;
        var anyFail = false;
        var notes = new List<string>();

        foreach (var result in results)
        {
            var resultPayload = new Dictionary<string, object?>
            {
                ["node_id"] = result.BranchKey,
                ["entry_node_id"] = result.EntryNodeId,
                ["status"] = result.CombinedStatus.ToString().ToLowerInvariant(),
                ["notes"] = result.Notes,
                ["completed_nodes"] = result.CompletedNodes,
                ["completed_stage_nodes"] = result.CompletedStageNodes
            };

            if (result.QueueItem is not null)
            {
                resultPayload["queue_item"] = BuildQueueItemPayload(result.QueueItem);
                resultPayload["queue_item_id"] = result.QueueItem.Id;
                resultPayload["queue_item_index"] = result.QueueItem.Index;
            }

            parallelResults.Add(resultPayload);

            if (result.CombinedStatus != OutcomeStatus.Success)
                allSuccess = false;
            if (result.CombinedStatus == OutcomeStatus.Fail)
                anyFail = true;

            notes.Add($"{result.BranchKey}: {result.CombinedStatus} ({result.CompletedNodes.Count} nodes)");
        }

        var contextUpdates = new Dictionary<string, string>
        {
            ["parallel.results"] = JsonSerializer.Serialize(parallelResults)
        };
        if (queueMode)
            contextUpdates["parallel.queue.count"] = results.Length.ToString();

        var nextNodeId = DetermineDirectNextNode(results);
        if (!string.IsNullOrWhiteSpace(nextNodeId))
            contextUpdates["parallel.next_node"] = nextNodeId!;

        var combinedStatus = joinPolicy switch
        {
            "first_success" => results.Any(result => result.CombinedStatus == OutcomeStatus.Success) ? OutcomeStatus.Success : OutcomeStatus.Fail,
            "quorum" => results.Count(result => result.CombinedStatus == OutcomeStatus.Success) > results.Length / 2 ? OutcomeStatus.Success : OutcomeStatus.Fail,
            _ => allSuccess ? OutcomeStatus.Success : anyFail ? OutcomeStatus.Fail : OutcomeStatus.PartialSuccess
        };

        if (errorPolicy == "ignore")
            combinedStatus = OutcomeStatus.Success;

        return new Outcome(
            Status: combinedStatus,
            ContextUpdates: contextUpdates,
            Notes: $"Parallel node '{node.Id}' completed ({joinPolicy}): {string.Join(", ", notes)}");
    }

    private record BranchResult(
        string EntryNodeId,
        string BranchKey,
        OutcomeStatus CombinedStatus,
        string Notes,
        List<string> CompletedNodes,
        List<string> CompletedStageNodes,
        string? NextNodeId,
        QueueWorkItem? QueueItem);

    private async Task<BranchResult> ExecuteBranchSubgraphAsync(
        string entryNodeId,
        PipelineContext branchContext,
        Graph graph,
        string logsRoot,
        CancellationToken ct,
        SemaphoreSlim? semaphore,
        CancellationTokenSource? failFastCts,
        QueueWorkItem? queueItem = null)
    {
        if (semaphore is not null)
            await semaphore.WaitAsync(ct);

        try
        {
            return await ExecuteSubgraphAsync(entryNodeId, branchContext, graph, logsRoot, ct, failFastCts, queueItem);
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
        CancellationTokenSource? failFastCts,
        QueueWorkItem? queueItem)
    {
        var completedNodes = new List<string>();
        var completedStageNodes = new List<string>();
        var currentNodeId = entryNodeId;
        var lastOutcome = new Outcome(OutcomeStatus.Success);
        var branchKey = BuildBranchKey(entryNodeId, queueItem);
        GraphEdge? incomingEdge = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!graph.Nodes.TryGetValue(currentNodeId, out var currentNode))
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    OutcomeStatus.Fail,
                    $"Node '{currentNodeId}' not found in graph.",
                    completedNodes,
                    completedStageNodes,
                    null,
                    queueItem);
            }

            if (currentNode.Shape.Equals("tripleoctagon", StringComparison.OrdinalIgnoreCase))
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    completedNodes.Count > 0 ? lastOutcome.Status : OutcomeStatus.Success,
                    $"Branch reached fan-in node '{currentNodeId}'.",
                    completedNodes,
                    completedStageNodes,
                    currentNodeId,
                    queueItem);
            }

            if (currentNode.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase))
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    completedNodes.Count > 0 ? lastOutcome.Status : OutcomeStatus.Success,
                    $"Branch reached exit node '{currentNodeId}'.",
                    completedNodes,
                    completedStageNodes,
                    currentNodeId,
                    queueItem);
            }

            currentNode = ResolveRuntimeNode(currentNode, currentNodeId, incomingEdge, graph, branchContext, queueItem);

            var handler = _registry.GetHandler(currentNode.Shape);
            if (handler is null)
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    OutcomeStatus.Fail,
                    $"No handler for shape '{currentNode.Shape}' on node '{currentNodeId}'.",
                    completedNodes,
                    completedStageNodes,
                    null,
                    queueItem);
            }

            Outcome outcome;
            try
            {
                outcome = await handler.ExecuteAsync(currentNode, branchContext, graph, logsRoot, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    OutcomeStatus.Fail,
                    $"Branch cancelled at '{currentNodeId}'.",
                    completedNodes,
                    completedStageNodes,
                    null,
                    queueItem);
            }
            catch (Exception ex)
            {
                outcome = new Outcome(OutcomeStatus.Fail, Notes: $"Error in {currentNodeId}: {ex.Message}");
            }

            branchContext.MergeUpdates(outcome.ContextUpdates);
            completedNodes.Add(currentNodeId);
            completedStageNodes.Add(RuntimeStageResolver.ResolveStageId(branchContext, currentNodeId));
            lastOutcome = outcome;

            if (outcome.Status == OutcomeStatus.Fail)
            {
                if (failFastCts is not null)
                    await failFastCts.CancelAsync();

                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    OutcomeStatus.Fail,
                    $"Branch failed at '{currentNodeId}': {outcome.Notes}",
                    completedNodes,
                    completedStageNodes,
                    null,
                    queueItem);
            }

            var outgoingEdges = graph.OutgoingEdges(currentNodeId);
            if (outgoingEdges.Count == 0)
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    outcome.Status,
                    $"Branch completed at dead end '{currentNodeId}'.",
                    completedNodes,
                    completedStageNodes,
                    null,
                    queueItem);
            }

            var selectedEdge = EdgeSelector.SelectEdge(outgoingEdges, outcome, branchContext);
            if (selectedEdge is null)
            {
                return new BranchResult(
                    entryNodeId,
                    branchKey,
                    OutcomeStatus.Fail,
                    $"No matching edge from '{currentNodeId}'.",
                    completedNodes,
                    completedStageNodes,
                    null,
                    queueItem);
            }

            ApplyContextReset(selectedEdge, graph, branchContext, queueItem);
            incomingEdge = selectedEdge;
            currentNodeId = selectedEdge.ToNode;
        }
    }

    private static void ApplyQueueItemContext(PipelineContext context, QueueWorkItem queueItem)
    {
        foreach (var (key, value) in queueItem.ContextValues)
            context.Set(key, value);
    }

    private static string BuildBranchKey(string entryNodeId, QueueWorkItem? queueItem)
    {
        return queueItem is null ? entryNodeId : $"{entryNodeId}[{queueItem.Id}]";
    }

    private static Dictionary<string, object?> BuildQueueItemPayload(QueueWorkItem queueItem)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = queueItem.Id,
            ["index"] = queueItem.Index
        };

        foreach (var (key, value) in queueItem.ContextValues)
        {
            var shortKey = key.StartsWith("queue.item.", StringComparison.Ordinal)
                ? key["queue.item.".Length..]
                : key;
            payload[shortKey] = value;
        }

        return payload;
    }

    private static string? DetermineDirectNextNode(IEnumerable<BranchResult> results)
    {
        var candidates = results
            .Select(result => result.NextNodeId)
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static GraphNode ResolveRuntimeNode(
        GraphNode currentNode,
        string currentNodeId,
        GraphEdge? incomingEdge,
        Graph graph,
        PipelineContext context,
        QueueWorkItem? queueItem)
    {
        var resolvedFidelity = ResolveFidelity(currentNode, incomingEdge, graph);
        var resolvedThread = ResolveThreadId(currentNode, currentNodeId, incomingEdge, graph);
        var stageId = BuildStageId(currentNodeId, queueItem);

        if (queueItem is not null)
            resolvedThread = BuildQueueScopedThreadId(resolvedThread, queueItem);

        if (!string.Equals(resolvedFidelity, currentNode.Fidelity, StringComparison.Ordinal) ||
            !string.Equals(resolvedThread, currentNode.ThreadId, StringComparison.Ordinal))
        {
            currentNode = currentNode with
            {
                Fidelity = resolvedFidelity,
                ThreadId = resolvedThread
            };
        }

        context.Set($"node.{currentNodeId}.fidelity", resolvedFidelity);
        context.Set($"node.{currentNodeId}.thread_id", resolvedThread);
        context.Set($"node.{currentNodeId}.stage_id", stageId);
        context.Set("runtime.fidelity", resolvedFidelity);
        context.Set("runtime.thread_id", resolvedThread);
        context.Set("runtime.stage_id", stageId);
        context.Set("runtime.current_node", currentNodeId);

        return currentNode;
    }

    private static string ResolveFidelity(GraphNode node, GraphEdge? incomingEdge, Graph graph)
    {
        if (incomingEdge is not null && !string.IsNullOrWhiteSpace(incomingEdge.Fidelity))
            return incomingEdge.Fidelity;

        if (!string.IsNullOrWhiteSpace(node.Fidelity))
            return node.Fidelity;

        if (!string.IsNullOrWhiteSpace(graph.DefaultFidelity))
            return graph.DefaultFidelity;

        return "compact";
    }

    private static string ResolveThreadId(GraphNode node, string nodeId, GraphEdge? incomingEdge, Graph graph)
    {
        if (incomingEdge is not null && !string.IsNullOrWhiteSpace(incomingEdge.ThreadId))
            return incomingEdge.ThreadId;

        if (!string.IsNullOrWhiteSpace(node.ThreadId))
            return node.ThreadId;

        if (graph.Attributes.TryGetValue("default_thread_id", out var graphThread) &&
            !string.IsNullOrWhiteSpace(graphThread))
        {
            return graphThread;
        }

        if (incomingEdge is not null && !string.IsNullOrWhiteSpace(incomingEdge.FromNode))
            return incomingEdge.FromNode;

        return nodeId;
    }

    private static string BuildStageId(string nodeId, QueueWorkItem? queueItem)
    {
        return queueItem is null ? nodeId : $"{nodeId}[{BuildQueueScopeToken(queueItem)}]";
    }

    private static string BuildQueueScopedThreadId(string threadId, QueueWorkItem queueItem)
    {
        var suffix = $"[{BuildQueueScopeToken(queueItem)}]";
        return threadId.EndsWith(suffix, StringComparison.Ordinal) ? threadId : $"{threadId}{suffix}";
    }

    private static string BuildQueueScopeToken(QueueWorkItem queueItem)
    {
        var token = string.IsNullOrWhiteSpace(queueItem.Id)
            ? $"item-{queueItem.Index}"
            : queueItem.Id.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
            token = token.Replace(invalid, '_');

        token = token
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');

        return string.IsNullOrWhiteSpace(token) ? $"item-{queueItem.Index}" : token;
    }

    private void ApplyContextReset(
        GraphEdge selectedEdge,
        Graph graph,
        PipelineContext context,
        QueueWorkItem? queueItem)
    {
        if (!selectedEdge.ContextReset || _backend is not ISessionControlBackend sessionControl)
            return;

        if (!graph.Nodes.TryGetValue(selectedEdge.ToNode, out var nextNode))
            return;

        var threadId = ResolveThreadId(nextNode, selectedEdge.ToNode, selectedEdge, graph);
        if (queueItem is not null)
            threadId = BuildQueueScopedThreadId(threadId, queueItem);

        if (!string.IsNullOrWhiteSpace(threadId) && sessionControl.ResetThread(threadId))
            context.Set($"edge.{selectedEdge.FromNode}->{selectedEdge.ToNode}.context_reset", "true");
    }
}
