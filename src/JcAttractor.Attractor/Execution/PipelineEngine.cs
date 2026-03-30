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
        GraphEdge? incomingEdge = null;
        var startTime = DateTimeOffset.UtcNow;

        // ── INITIALIZE phase ────────────────────────────────────────────
        // Mirror all graph attributes into context
        if (!string.IsNullOrEmpty(graph.Goal))
            context.Set("goal", graph.Goal);
        if (!string.IsNullOrEmpty(graph.Name))
            context.Set("graph.name", graph.Name);
        if (!string.IsNullOrEmpty(graph.ModelStylesheet))
            context.Set("graph.model_stylesheet", "true");

        // Mirror graph attributes
        foreach (var (key, value) in graph.Attributes)
            context.Set($"graph.{key}", value);

        // Explicitly create the run directory structure
        Directory.CreateDirectory(_config.LogsRoot);

        // Try to resume from checkpoint
        var checkpoint = Checkpoint.Load(_config.LogsRoot);
        var isResuming = false;
        if (checkpoint != null)
        {
            isResuming = true;
            completedNodes.AddRange(checkpoint.CompletedNodes);
            context.MergeUpdates(checkpoint.ContextData.ToDictionary(kv => kv.Key, kv => kv.Value));
            foreach (var (k, v) in checkpoint.RetryCounts)
                retryCounts[k] = v;
        }

        context.Set("pipeline.resume_mode", isResuming ? "resume" : "fresh");

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
                // Goal gate enforcement: check if all goal_gate nodes have succeeded
                var goalGateNodes = graph.Nodes.Values.Where(n => n.GoalGate).ToList();
                var unsatisfiedGates = goalGateNodes.Where(g =>
                {
                    if (!nodeOutcomes.TryGetValue(g.Id, out var gateOutcome))
                        return true; // Never executed
                    return gateOutcome.Status != OutcomeStatus.Success
                        && gateOutcome.Status != OutcomeStatus.PartialSuccess;
                }).ToList();

                if (unsatisfiedGates.Count > 0)
                {
                    // Cannot exit yet; look for a retry path
                    var firstUnsatisfied = unsatisfiedGates[0];
                    var retryNodeId = !string.IsNullOrEmpty(firstUnsatisfied.RetryTarget) ? firstUnsatisfied.RetryTarget
                        : !string.IsNullOrEmpty(firstUnsatisfied.FallbackRetryTarget) ? firstUnsatisfied.FallbackRetryTarget
                        : !string.IsNullOrEmpty(graph.RetryTarget) ? graph.RetryTarget
                        : !string.IsNullOrEmpty(graph.FallbackRetryTarget) ? graph.FallbackRetryTarget
                        : null;

                    if (retryNodeId != null && graph.Nodes.ContainsKey(retryNodeId))
                    {
                        currentNodeId = retryNodeId;
                        continue;
                    }
                    else
                    {
                        // No retry target available, pipeline fails
                        return new PipelineResult(
                            Status: OutcomeStatus.Fail,
                            CompletedNodes: completedNodes,
                            NodeOutcomes: nodeOutcomes,
                            FinalContext: context
                        );
                    }
                }

                // Execute exit handler
                var exitHandler = _registry.GetHandlerOrThrow(currentNode.Shape);
                var exitOutcome = await exitHandler.ExecuteAsync(currentNode, context, graph, _config.LogsRoot, ct);
                completedNodes.Add(currentNodeId);
                nodeOutcomes[currentNodeId] = exitOutcome;

                // ── FINALIZE phase ──────────────────────────────────────
                var duration = DateTimeOffset.UtcNow - startTime;
                context.Set("pipeline.duration_ms", ((long)duration.TotalMilliseconds).ToString());
                context.Set("pipeline.nodes_executed", completedNodes.Count.ToString());
                context.Set("pipeline.status", "success");
                SaveCheckpoint(currentNodeId, completedNodes, context, retryCounts);

                return new PipelineResult(
                    Status: OutcomeStatus.Success,
                    CompletedNodes: completedNodes,
                    NodeOutcomes: nodeOutcomes,
                    FinalContext: context
                );
            }

            // Fidelity degradation on resume: if resuming and this is the first node,
            // degrade fidelity from "full" to "summary:high"
            currentNode = ResolveRuntimeNode(currentNode, currentNodeId, incomingEdge, graph, context);

            if (isResuming)
            {
                isResuming = false; // Only degrade for the first resumed node
                if (IsFullFidelity(currentNode.Fidelity))
                {
                    graph.Nodes[currentNodeId] = currentNode with { Fidelity = "summary:high" };
                    currentNode = graph.Nodes[currentNodeId];
                    context.Set($"node.{currentNodeId}.fidelity", "summary:high");
                    context.Set("runtime.fidelity", "summary:high");
                }
            }

            var stageStartUtc = DateTimeOffset.UtcNow;
            await WriteTelemetryEventAsync(
                eventType: "stage_start",
                nodeId: currentNodeId,
                data: new Dictionary<string, object?>
                {
                    ["shape"] = currentNode.Shape,
                    ["fidelity"] = currentNode.Fidelity,
                    ["thread_id"] = currentNode.ThreadId
                },
                ct: ct);

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
            MergeParallelBranchResults(completedNodes, nodeOutcomes, outcome);
            var stageDurationMs = (long)Math.Max(0, (DateTimeOffset.UtcNow - stageStartUtc).TotalMilliseconds);
            var stageEndData = new Dictionary<string, object?>
            {
                ["status"] = StageStatusContract.ToStatusString(outcome.Status),
                ["preferred_next_label"] = outcome.PreferredLabel,
                ["notes"] = outcome.Notes,
                ["duration_ms"] = stageDurationMs
            };
            MergeTelemetry(stageEndData, outcome.Telemetry);

            await WriteTelemetryEventAsync(
                eventType: "stage_end",
                nodeId: currentNodeId,
                data: stageEndData,
                ct: ct);

            // Write status.json for this node
            await WriteNodeStatusAsync(currentNodeId, outcome, ct);

            // Handle retry logic
            if (outcome.Status == OutcomeStatus.Retry || outcome.Status == OutcomeStatus.Fail)
            {
                int maxRetries = currentNode.MaxRetries > 0 ? currentNode.MaxRetries : 0;
                int currentRetryCount = retryCounts.GetValueOrDefault(currentNodeId, 0);

                // Retry on RETRY or FAIL when max_retries allows it
                if (currentRetryCount < maxRetries &&
                    (outcome.Status == OutcomeStatus.Retry || outcome.Status == OutcomeStatus.Fail))
                {
                    retryCounts[currentNodeId] = currentRetryCount + 1;

                    // Determine retry target
                    string retryTarget = !string.IsNullOrEmpty(currentNode.RetryTarget) ? currentNode.RetryTarget
                        : !string.IsNullOrEmpty(graph.RetryTarget) ? graph.RetryTarget
                        : currentNodeId;

                    // Apply backoff delay
                    await ApplyBackoffAsync(currentRetryCount, ct);

                    await WriteTelemetryEventAsync(
                        eventType: "stage_retry",
                        nodeId: currentNodeId,
                        data: new Dictionary<string, object?>
                        {
                            ["retry_count"] = retryCounts[currentNodeId],
                            ["retry_target"] = retryTarget,
                            ["status"] = StageStatusContract.ToStatusString(outcome.Status)
                        },
                        ct: ct);

                    SaveCheckpoint(retryTarget, completedNodes, context, retryCounts);
                    currentNodeId = retryTarget;
                    incomingEdge = null;
                    continue;
                }

                // Max retries exceeded or no retries configured
                // Convert exhausted RETRY to FAIL
                if (outcome.Status == OutcomeStatus.Retry && currentRetryCount >= maxRetries && maxRetries > 0)
                {
                    if (currentNode.AllowPartial)
                    {
                        outcome = outcome with { Status = OutcomeStatus.PartialSuccess, Notes = "Retries exhausted, partial accepted" };
                    }
                    else
                    {
                        outcome = outcome with { Status = OutcomeStatus.Fail, Notes = "Max retries exceeded" };
                    }
                    nodeOutcomes[currentNodeId] = outcome;
                }

                // Check for fallback retry target
                string? fallbackTarget = !string.IsNullOrEmpty(currentNode.FallbackRetryTarget) ? currentNode.FallbackRetryTarget
                    : !string.IsNullOrEmpty(graph.FallbackRetryTarget) ? graph.FallbackRetryTarget
                    : null;

                if (fallbackTarget != null && graph.Nodes.ContainsKey(fallbackTarget))
                {
                    retryCounts[currentNodeId] = 0; // Reset retry count
                    SaveCheckpoint(fallbackTarget, completedNodes, context, retryCounts);
                    currentNodeId = fallbackTarget;
                    incomingEdge = null;
                    continue;
                }

                // Allow partial success if configured
                if (currentNode.AllowPartial && outcome.Status != OutcomeStatus.Success && outcome.Status != OutcomeStatus.PartialSuccess)
                {
                    outcome = outcome with { Status = OutcomeStatus.PartialSuccess };
                    nodeOutcomes[currentNodeId] = outcome;
                }
                // Per spec 3.7: on FAIL, failure routing order:
                // 1. Fail edge (condition="outcome=fail")
                // 2. retry_target
                // 3. fallback_retry_target
                // 4. Pipeline termination
                else if (outcome.Status == OutcomeStatus.Fail)
                {
                    // Step 1: Check for explicit fail edge
                    var hasFailEdge = graph.OutgoingEdges(currentNodeId)
                        .Any(e => !string.IsNullOrWhiteSpace(e.Condition) &&
                                  ConditionEvaluator.Evaluate(e.Condition, outcome, context));

                    if (hasFailEdge)
                    {
                        // Fail edge exists; let edge selection handle routing
                    }
                    // Step 2: Check retry_target
                    else if (!string.IsNullOrEmpty(currentNode.RetryTarget) && graph.Nodes.ContainsKey(currentNode.RetryTarget))
                    {
                        currentNodeId = currentNode.RetryTarget;
                        incomingEdge = null;
                        continue;
                    }
                    // Step 3: Check fallback_retry_target (node then graph)
                    else if (!string.IsNullOrEmpty(currentNode.FallbackRetryTarget) && graph.Nodes.ContainsKey(currentNode.FallbackRetryTarget))
                    {
                        currentNodeId = currentNode.FallbackRetryTarget;
                        incomingEdge = null;
                        continue;
                    }
                    else if (!string.IsNullOrEmpty(graph.RetryTarget) && graph.Nodes.ContainsKey(graph.RetryTarget))
                    {
                        currentNodeId = graph.RetryTarget;
                        incomingEdge = null;
                        continue;
                    }
                    else if (!string.IsNullOrEmpty(graph.FallbackRetryTarget) && graph.Nodes.ContainsKey(graph.FallbackRetryTarget))
                    {
                        currentNodeId = graph.FallbackRetryTarget;
                        incomingEdge = null;
                        continue;
                    }
                    else
                    {
                        // Step 4: Pipeline termination
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

            var directNextNode = TryResolveDirectNextNode(outcome, graph);
            if (!string.IsNullOrWhiteSpace(directNextNode))
            {
                SaveCheckpoint(directNextNode!, completedNodes, context, retryCounts);
                currentNodeId = directNextNode!;
                incomingEdge = null;
                continue;
            }

            // Step 4: Select next edge
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

            // Step 5: Handle loop_restart
            if (selectedEdge.LoopRestart)
            {
                // Reset completed nodes for the target node (allow re-execution)
                completedNodes.RemoveAll(n => n == selectedEdge.ToNode);
                retryCounts.Remove(selectedEdge.ToNode);
            }

            SaveCheckpoint(selectedEdge.ToNode, completedNodes, context, retryCounts);

            // Step 6: Advance to next node
            currentNodeId = selectedEdge.ToNode;
            incomingEdge = selectedEdge;
        }
    }

    private static GraphNode ResolveRuntimeNode(
        GraphNode currentNode,
        string currentNodeId,
        GraphEdge? incomingEdge,
        Graph graph,
        PipelineContext context)
    {
        var resolvedFidelity = ResolveFidelity(currentNode, incomingEdge, graph);
        var resolvedThread = ResolveThreadId(currentNode, currentNodeId, incomingEdge, graph);

        if (!string.Equals(resolvedFidelity, currentNode.Fidelity, StringComparison.Ordinal) ||
            !string.Equals(resolvedThread, currentNode.ThreadId, StringComparison.Ordinal))
        {
            currentNode = currentNode with
            {
                Fidelity = resolvedFidelity,
                ThreadId = resolvedThread
            };
            graph.Nodes[currentNodeId] = currentNode;
        }

        context.Set($"node.{currentNodeId}.fidelity", resolvedFidelity);
        context.Set($"node.{currentNodeId}.thread_id", resolvedThread);
        context.Set($"node.{currentNodeId}.stage_id", currentNodeId);
        context.Set("runtime.fidelity", resolvedFidelity);
        context.Set("runtime.thread_id", resolvedThread);
        context.Set("runtime.stage_id", currentNodeId);
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

        return "full";
    }

    private static string ResolveThreadId(GraphNode node, string nodeId, GraphEdge? incomingEdge, Graph graph)
    {
        if (incomingEdge is not null && !string.IsNullOrWhiteSpace(incomingEdge.ThreadId))
            return incomingEdge.ThreadId;

        if (!string.IsNullOrWhiteSpace(node.ThreadId))
            return node.ThreadId;

        if (graph.Attributes.TryGetValue("default_thread_id", out var graphThread) && !string.IsNullOrWhiteSpace(graphThread))
            return graphThread;

        if (incomingEdge is not null && !string.IsNullOrWhiteSpace(incomingEdge.FromNode))
            return incomingEdge.FromNode;

        return nodeId;
    }

    private static bool IsFullFidelity(string fidelity)
    {
        return string.IsNullOrWhiteSpace(fidelity) || fidelity.Equals("full", StringComparison.OrdinalIgnoreCase);
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
            var statusPath = Path.Combine(stageDir, "status.json");

            // If a handler already wrote a validated contract status, keep it canonical.
            if (File.Exists(statusPath))
            {
                try
                {
                    var existing = await File.ReadAllTextAsync(statusPath, ct);
                    using var doc = JsonDocument.Parse(existing);
                    if (doc.RootElement.TryGetProperty("contract_validated", out var validated) &&
                        validated.ValueKind == JsonValueKind.True)
                    {
                        return;
                    }
                }
                catch
                {
                    // Ignore malformed existing status and overwrite with fallback below.
                }
            }

            var statusData = new Dictionary<string, object?>
            {
                ["node_id"] = nodeId,
                ["status"] = StageStatusContract.ToStatusString(outcome.Status),
                ["preferred_next_label"] = outcome.PreferredLabel,
                ["notes"] = outcome.Notes
            };

            await File.WriteAllTextAsync(
                statusPath,
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

    private async Task WriteTelemetryEventAsync(string eventType, string nodeId, Dictionary<string, object?> data, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(_config.LogsRoot, "events.jsonl");
            var payload = new Dictionary<string, object?>(data)
            {
                ["event_type"] = eventType,
                ["node_id"] = nodeId,
                ["timestamp_utc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            var line = JsonSerializer.Serialize(payload);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
        }
        catch
        {
            // Non-critical: telemetry must not fail pipeline execution.
        }
    }

    private static void MergeTelemetry(Dictionary<string, object?> target, Dictionary<string, object?>? telemetry)
    {
        if (telemetry is null || telemetry.Count == 0)
            return;

        target["stage_metrics"] = telemetry;

        if (TryGetInt(telemetry, "tool_calls", out var toolCalls))
            target["tool_calls"] = toolCalls;
        if (TryGetInt(telemetry, "tool_errors", out var toolErrors))
            target["tool_errors"] = toolErrors;
        if (TryGetInt(telemetry, "touched_files_count", out var touchedFiles))
            target["touched_files_count"] = touchedFiles;

        if (telemetry.TryGetValue("token_usage", out var tokenUsage) &&
            tokenUsage is Dictionary<string, object?> tokenUsageDict)
        {
            if (TryGetInt(tokenUsageDict, "input_tokens", out var inputTokens))
                target["input_tokens"] = inputTokens;
            if (TryGetInt(tokenUsageDict, "output_tokens", out var outputTokens))
                target["output_tokens"] = outputTokens;
            if (TryGetInt(tokenUsageDict, "total_tokens", out var totalTokens))
                target["total_tokens"] = totalTokens;
        }
    }

    private static bool TryGetInt(Dictionary<string, object?> source, string key, out long value)
    {
        value = 0;
        if (!source.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case float f:
                value = (long)f;
                return true;
            case double d:
                value = (long)d;
                return true;
            case decimal m:
                value = (long)m;
                return true;
            case string s when long.TryParse(s, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static string? TryResolveDirectNextNode(Outcome outcome, Graph graph)
    {
        if (outcome.ContextUpdates is null)
            return null;

        if (!outcome.ContextUpdates.TryGetValue("parallel.next_node", out var nextNodeId) ||
            string.IsNullOrWhiteSpace(nextNodeId))
        {
            return null;
        }

        return graph.Nodes.ContainsKey(nextNodeId) ? nextNodeId : null;
    }

    private static void MergeParallelBranchResults(
        List<string> completedNodes,
        Dictionary<string, Outcome> nodeOutcomes,
        Outcome outcome)
    {
        if (outcome.ContextUpdates is null ||
            !outcome.ContextUpdates.TryGetValue("parallel.results", out var resultsJson) ||
            string.IsNullOrWhiteSpace(resultsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(resultsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var branchResult in document.RootElement.EnumerateArray())
            {
                var branchStatus = ParseOutcomeStatus(branchResult.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null);
                var branchNotes = branchResult.TryGetProperty("notes", out var notesElement) ? notesElement.GetString() ?? string.Empty : string.Empty;

                JsonElement completedNodesElement;
                if (branchResult.TryGetProperty("completed_stage_nodes", out var completedStageNodesElement) &&
                    completedStageNodesElement.ValueKind == JsonValueKind.Array)
                {
                    completedNodesElement = completedStageNodesElement;
                }
                else if (branchResult.TryGetProperty("completed_nodes", out var logicalCompletedNodesElement) &&
                    logicalCompletedNodesElement.ValueKind == JsonValueKind.Array)
                {
                    completedNodesElement = logicalCompletedNodesElement;
                }
                else
                {
                    continue;
                }

                foreach (var nodeElement in completedNodesElement.EnumerateArray())
                {
                    var branchNodeId = nodeElement.GetString();
                    if (string.IsNullOrWhiteSpace(branchNodeId))
                        continue;

                    if (!completedNodes.Any(existing => string.Equals(existing, branchNodeId, StringComparison.Ordinal)))
                        completedNodes.Add(branchNodeId);

                    if (!nodeOutcomes.ContainsKey(branchNodeId))
                    {
                        nodeOutcomes[branchNodeId] = new Outcome(
                            Status: branchStatus,
                            Notes: branchNotes);
                    }
                }
            }
        }
        catch
        {
            // Treat malformed branch results as absent.
        }
    }

    private static OutcomeStatus ParseOutcomeStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "success" => OutcomeStatus.Success,
            "partial_success" => OutcomeStatus.PartialSuccess,
            "retry" => OutcomeStatus.Retry,
            "fail" => OutcomeStatus.Fail,
            _ => OutcomeStatus.Fail
        };
    }
}
