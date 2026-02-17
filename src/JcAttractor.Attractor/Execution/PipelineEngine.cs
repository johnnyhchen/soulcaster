namespace JcAttractor.Attractor;

using System.Text.Json;

public record PipelineConfig(
    string LogsRoot = "./logs",
    IInterviewer? Interviewer = null,
    ICodergenBackend? Backend = null,
    List<IGraphTransform>? Transforms = null,
    Dictionary<string, INodeHandler>? CustomHandlers = null
);

public record PipelineResult(
    OutcomeStatus Status,
    List<string> CompletedNodes,
    Dictionary<string, Outcome> NodeOutcomes,
    PipelineContext FinalContext
);

public class PipelineEngine
{
    private readonly PipelineConfig _config;
    private readonly HandlerRegistry _registry;

    public PipelineEngine(PipelineConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = new HandlerRegistry(_config.Backend, _config.Interviewer);

        // Register custom handlers
        if (_config.CustomHandlers != null)
        {
            foreach (var (shape, handler) in _config.CustomHandlers)
            {
                _registry.Register(shape, handler);
            }
        }
    }

    public async Task<PipelineResult> RunAsync(Graph graph, CancellationToken ct = default)
    {
        // Apply transforms
        if (_config.Transforms != null)
        {
            foreach (var transform in _config.Transforms)
            {
                graph = transform.Transform(graph);
            }
        }

        // Validate graph
        Validator.ValidateOrRaise(graph);

        var context = new PipelineContext();
        var completedNodes = new List<string>();
        var nodeOutcomes = new Dictionary<string, Outcome>();
        var retryCounts = new Dictionary<string, int>();

        // Set initial context from graph goal
        if (!string.IsNullOrEmpty(graph.Goal))
            context.Set("goal", graph.Goal);

        // Try to resume from checkpoint
        var checkpoint = Checkpoint.Load(_config.LogsRoot);
        if (checkpoint != null)
        {
            completedNodes.AddRange(checkpoint.CompletedNodes);
            context.MergeUpdates(checkpoint.ContextData.ToDictionary(kv => kv.Key, kv => kv.Value));
            foreach (var (k, v) in checkpoint.RetryCounts)
                retryCounts[k] = v;
        }

        // Step 1: Find start node
        var startNode = graph.Nodes.Values.First(n => n.Shape.Equals("Mdiamond", StringComparison.OrdinalIgnoreCase));
        string currentNodeId = checkpoint?.CurrentNodeId ?? startNode.Id;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!graph.Nodes.TryGetValue(currentNodeId, out var currentNode))
            {
                throw new InvalidOperationException($"Node '{currentNodeId}' not found in graph.");
            }

            // Check if this is the exit node
            bool isExitNode = currentNode.Shape.Equals("Msquare", StringComparison.OrdinalIgnoreCase);

            if (isExitNode)
            {
                // Goal gate enforcement: check if all goal_gate nodes have been completed
                var goalGateNodes = graph.Nodes.Values.Where(n => n.GoalGate).ToList();
                var unmetGates = goalGateNodes.Where(g => !completedNodes.Contains(g.Id)).ToList();

                if (unmetGates.Count > 0)
                {
                    // Cannot exit yet; look for a path back
                    var firstUnmet = unmetGates[0];
                    var retryNodeId = !string.IsNullOrEmpty(firstUnmet.RetryTarget) ? firstUnmet.RetryTarget
                        : !string.IsNullOrEmpty(graph.RetryTarget) ? graph.RetryTarget
                        : firstUnmet.Id;

                    if (graph.Nodes.ContainsKey(retryNodeId))
                    {
                        currentNodeId = retryNodeId;
                        continue;
                    }
                }

                // Execute exit handler
                var exitHandler = _registry.GetHandlerOrThrow(currentNode.Shape);
                var exitOutcome = await exitHandler.ExecuteAsync(currentNode, context, graph, _config.LogsRoot, ct);
                completedNodes.Add(currentNodeId);
                nodeOutcomes[currentNodeId] = exitOutcome;
                SaveCheckpoint(currentNodeId, completedNodes, context, retryCounts);

                return new PipelineResult(
                    Status: OutcomeStatus.Success,
                    CompletedNodes: completedNodes,
                    NodeOutcomes: nodeOutcomes,
                    FinalContext: context
                );
            }

            // Step 2: Execute handler with retry policy
            var handler = _registry.GetHandlerOrThrow(currentNode.Shape);
            Outcome outcome;

            try
            {
                outcome = await ExecuteWithTimeoutAsync(handler, currentNode, context, graph, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = new Outcome(OutcomeStatus.Fail, Notes: $"Handler exception: {ex.Message}");
            }

            // Step 3: Record completion, apply context updates
            context.MergeUpdates(outcome.ContextUpdates);
            nodeOutcomes[currentNodeId] = outcome;

            // Write status.json for this node
            await WriteNodeStatusAsync(currentNodeId, outcome, ct);

            // Handle retry logic
            if (outcome.Status == OutcomeStatus.Retry || outcome.Status == OutcomeStatus.Fail)
            {
                int maxRetries = currentNode.MaxRetries > 0 ? currentNode.MaxRetries : graph.DefaultMaxRetry;
                int currentRetryCount = retryCounts.GetValueOrDefault(currentNodeId, 0);

                if (currentRetryCount < maxRetries && outcome.Status == OutcomeStatus.Retry)
                {
                    retryCounts[currentNodeId] = currentRetryCount + 1;

                    // Determine retry target
                    string retryTarget = !string.IsNullOrEmpty(currentNode.RetryTarget) ? currentNode.RetryTarget
                        : !string.IsNullOrEmpty(graph.RetryTarget) ? graph.RetryTarget
                        : currentNodeId;

                    // Apply backoff delay
                    await ApplyBackoffAsync(currentRetryCount, ct);

                    SaveCheckpoint(retryTarget, completedNodes, context, retryCounts);
                    currentNodeId = retryTarget;
                    continue;
                }

                // Max retries exceeded or hard fail
                if (outcome.Status == OutcomeStatus.Fail || currentRetryCount >= maxRetries)
                {
                    // Check for fallback retry target
                    string? fallbackTarget = !string.IsNullOrEmpty(currentNode.FallbackRetryTarget) ? currentNode.FallbackRetryTarget
                        : !string.IsNullOrEmpty(graph.FallbackRetryTarget) ? graph.FallbackRetryTarget
                        : null;

                    if (fallbackTarget != null && graph.Nodes.ContainsKey(fallbackTarget))
                    {
                        retryCounts[currentNodeId] = 0; // Reset retry count
                        SaveCheckpoint(fallbackTarget, completedNodes, context, retryCounts);
                        currentNodeId = fallbackTarget;
                        continue;
                    }

                    // Allow partial success if configured
                    if (currentNode.AllowPartial && outcome.Status != OutcomeStatus.Success)
                    {
                        outcome = outcome with { Status = OutcomeStatus.PartialSuccess };
                        nodeOutcomes[currentNodeId] = outcome;
                    }
                    else if (outcome.Status == OutcomeStatus.Fail)
                    {
                        // Pipeline fails
                        completedNodes.Add(currentNodeId);
                        SaveCheckpoint(currentNodeId, completedNodes, context, retryCounts);

                        return new PipelineResult(
                            Status: OutcomeStatus.Fail,
                            CompletedNodes: completedNodes,
                            NodeOutcomes: nodeOutcomes,
                            FinalContext: context
                        );
                    }
                }
            }

            completedNodes.Add(currentNodeId);

            // Step 4: Save checkpoint
            SaveCheckpoint(currentNodeId, completedNodes, context, retryCounts);

            // Step 5: Select next edge
            var outgoingEdges = graph.OutgoingEdges(currentNodeId);
            if (outgoingEdges.Count == 0)
            {
                // No outgoing edges and not the exit node - pipeline is stuck
                return new PipelineResult(
                    Status: outcome.Status,
                    CompletedNodes: completedNodes,
                    NodeOutcomes: nodeOutcomes,
                    FinalContext: context
                );
            }

            var selectedEdge = EdgeSelector.SelectEdge(outgoingEdges, outcome, context);
            if (selectedEdge == null)
            {
                return new PipelineResult(
                    Status: OutcomeStatus.Fail,
                    CompletedNodes: completedNodes,
                    NodeOutcomes: nodeOutcomes,
                    FinalContext: context
                );
            }

            // Step 6: Handle loop_restart
            if (selectedEdge.LoopRestart)
            {
                // Reset completed nodes for the target node (allow re-execution)
                completedNodes.RemoveAll(n => n == selectedEdge.ToNode);
                retryCounts.Remove(selectedEdge.ToNode);
            }

            // Step 7: Advance to next node
            currentNodeId = selectedEdge.ToNode;
        }
    }

    private async Task<Outcome> ExecuteWithTimeoutAsync(
        INodeHandler handler, GraphNode node, PipelineContext context, Graph graph, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(node.Timeout) && TimeSpan.TryParse(node.Timeout, out var timeout))
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                return await handler.ExecuteAsync(node, context, graph, _config.LogsRoot, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new Outcome(OutcomeStatus.Retry, Notes: $"Node '{node.Id}' timed out after {timeout}.");
            }
        }

        return await handler.ExecuteAsync(node, context, graph, _config.LogsRoot, ct);
    }

    private async Task WriteNodeStatusAsync(string nodeId, Outcome outcome, CancellationToken ct)
    {
        try
        {
            string stageDir = Path.Combine(_config.LogsRoot, nodeId);
            Directory.CreateDirectory(stageDir);

            var statusData = new Dictionary<string, object?>
            {
                ["node_id"] = nodeId,
                ["status"] = outcome.Status.ToString().ToLowerInvariant(),
                ["preferred_label"] = outcome.PreferredLabel,
                ["notes"] = outcome.Notes
            };

            await File.WriteAllTextAsync(
                Path.Combine(stageDir, "status.json"),
                JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true }),
                ct
            );
        }
        catch
        {
            // Non-critical: don't fail the pipeline if status write fails
        }
    }

    private void SaveCheckpoint(string currentNodeId, List<string> completedNodes, PipelineContext context, Dictionary<string, int> retryCounts)
    {
        try
        {
            var checkpoint = new Checkpoint(
                CurrentNodeId: currentNodeId,
                CompletedNodes: new List<string>(completedNodes),
                ContextData: context.All.ToDictionary(kv => kv.Key, kv => kv.Value),
                RetryCounts: new Dictionary<string, int>(retryCounts)
            );
            checkpoint.Save(_config.LogsRoot);
        }
        catch
        {
            // Non-critical: don't fail the pipeline if checkpoint save fails
        }
    }

    private static async Task ApplyBackoffAsync(int retryCount, CancellationToken ct)
    {
        // Exponential backoff: 100ms * 2^retryCount, capped at 30 seconds
        int delayMs = Math.Min((int)(100 * Math.Pow(2, retryCount)), 30_000);
        await Task.Delay(delayMs, ct);
    }
}
