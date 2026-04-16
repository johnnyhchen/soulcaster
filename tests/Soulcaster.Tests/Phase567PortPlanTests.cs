using System.Text.Json;
using Soulcaster.Attractor;
using Soulcaster.Runner;

namespace Soulcaster.Tests;

public class QueueParallelismTests
{
    [Fact]
    public async Task ParallelHandler_EnumeratesDirectoryQueueItems()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_queue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var queueDir = Path.Combine(tempDir, "queue");
        Directory.CreateDirectory(queueDir);
        File.WriteAllText(Path.Combine(queueDir, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(queueDir, "beta.txt"), "beta");

        try
        {
            var backend = new QueueAwareBackend();
            var registry = new HandlerRegistry(backend);
            var graph = CreateQueueGraph(queueDir);

            var outcome = await registry.GetHandlerOrThrow("component").ExecuteAsync(
                graph.Nodes["parallel"],
                new PipelineContext(),
                graph,
                tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.NotNull(outcome.ContextUpdates);
            Assert.Equal("2", outcome.ContextUpdates!["parallel.queue.count"]);
            Assert.Equal("merge", outcome.ContextUpdates["parallel.next_node"]);

            var results = ParseParallelResults(outcome.ContextUpdates["parallel.results"]);
            Assert.Equal(2, results.Count);
            Assert.All(results, result =>
            {
                Assert.True(result.TryGetValue("queue_item", out var queueItemValue));
                var queueItem = (JsonElement)queueItemValue!;
                Assert.Equal(JsonValueKind.Object, queueItem.ValueKind);
                Assert.True(queueItem.TryGetProperty("path", out var path));
                Assert.EndsWith(".txt", path.GetString(), StringComparison.Ordinal);
            });
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ParallelHandler_EnumeratesManifestQueueItems()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_queue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var manifestDir = Path.Combine(tempDir, "manifest");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "task-two.txt"), "task two");
        var manifestPath = Path.Combine(manifestDir, "items.json");
        File.WriteAllText(
            manifestPath,
            """
            {
              "items": [
                { "id": "task-one", "value": "alpha" },
                { "id": "task-two", "path": "task-two.txt" }
              ]
            }
            """);

        try
        {
            var backend = new QueueAwareBackend();
            var registry = new HandlerRegistry(backend);
            var graph = CreateQueueGraph(manifestPath);

            var outcome = await registry.GetHandlerOrThrow("component").ExecuteAsync(
                graph.Nodes["parallel"],
                new PipelineContext(),
                graph,
                tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);

            var results = ParseParallelResults(outcome.ContextUpdates!["parallel.results"]);
            Assert.Equal(2, results.Count);
            Assert.Contains(results, result => result["queue_item_id"]?.ToString() == "task-one");
            Assert.Contains(results, result => result["queue_item_id"]?.ToString() == "task-two");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ParallelHandler_EmptyQueue_SucceedsWithoutBranches()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_queue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var queueDir = Path.Combine(tempDir, "queue");
        Directory.CreateDirectory(queueDir);

        try
        {
            var registry = new HandlerRegistry(new QueueAwareBackend());
            var graph = CreateQueueGraph(queueDir);

            var outcome = await registry.GetHandlerOrThrow("component").ExecuteAsync(
                graph.Nodes["parallel"],
                new PipelineContext(),
                graph,
                tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Equal("0", outcome.ContextUpdates!["parallel.queue.count"]);
            Assert.Equal("[]", outcome.ContextUpdates["parallel.results"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ParallelHandler_MissingQueueSource_Fails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_queue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var registry = new HandlerRegistry(new QueueAwareBackend());
            var graph = CreateQueueGraph(Path.Combine(tempDir, "missing-items.json"));

            var outcome = await registry.GetHandlerOrThrow("component").ExecuteAsync(
                graph.Nodes["parallel"],
                new PipelineContext(),
                graph,
                tempDir);

            Assert.Equal(OutcomeStatus.Fail, outcome.Status);
            Assert.Contains("could not load queue source", outcome.Notes, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ParallelHandler_AggregatesPartialQueueFailures()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_queue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, "items.txt");
        File.WriteAllLines(manifestPath, ["good-item", "bad-item"]);

        try
        {
            var backend = new QueueAwareBackend();
            var registry = new HandlerRegistry(backend);
            var graph = CreateQueueGraph(manifestPath);

            var outcome = await registry.GetHandlerOrThrow("component").ExecuteAsync(
                graph.Nodes["parallel"],
                new PipelineContext(),
                graph,
                tempDir);

            Assert.Equal(OutcomeStatus.Fail, outcome.Status);

            var results = ParseParallelResults(outcome.ContextUpdates!["parallel.results"]);
            Assert.Contains(results, result => result["status"]?.ToString() == "success");
            Assert.Contains(results, result => result["status"]?.ToString() == "fail");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ParallelHandler_QueueItemsUseScopedThreadsAndStageArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_queue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var queueDir = Path.Combine(tempDir, "queue");
        Directory.CreateDirectory(queueDir);
        File.WriteAllText(Path.Combine(queueDir, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(queueDir, "beta.txt"), "beta");

        try
        {
            var backend = new DeterministicBackend();
            var registry = new HandlerRegistry(backend);
            var graph = CreateQueueGraph(queueDir);

            var outcome = await registry.GetHandlerOrThrow("component").ExecuteAsync(
                graph.Nodes["parallel"],
                new PipelineContext(),
                graph,
                tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);

            var alphaStage = Path.Combine(tempDir, "worker[alpha.txt]");
            var betaStage = Path.Combine(tempDir, "worker[beta.txt]");

            Assert.True(File.Exists(Path.Combine(alphaStage, "prompt.md")));
            Assert.True(File.Exists(Path.Combine(betaStage, "prompt.md")));
            Assert.Contains("Runtime thread: worker[alpha.txt]", File.ReadAllText(Path.Combine(alphaStage, "prompt.md")));
            Assert.Contains("Runtime thread: worker[beta.txt]", File.ReadAllText(Path.Combine(betaStage, "prompt.md")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Validator_QueueParallelRequiresExactlyOneWorkerEdge()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["parallel"] = new GraphNode
        {
            Id = "parallel",
            Shape = "component",
            RawAttributes = new Dictionary<string, string> { ["queue_source"] = "items.json" }
        };
        graph.Nodes["worker_a"] = new GraphNode { Id = "worker_a", Shape = "box", Prompt = "a" };
        graph.Nodes["worker_b"] = new GraphNode { Id = "worker_b", Shape = "box", Prompt = "b" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "parallel" });
        graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "worker_a" });
        graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "worker_b" });
        graph.Edges.Add(new GraphEdge { FromNode = "worker_a", ToNode = "done" });
        graph.Edges.Add(new GraphEdge { FromNode = "worker_b", ToNode = "done" });

        var results = Validator.Validate(graph);

        Assert.Contains(results, result => result.Rule == "queue_parallel_worker" && result.Severity == LintSeverity.Error);
    }

    [Fact]
    public async Task PipelineEngine_ParallelBranchesExecuteOnceAndAdvanceToFanIn()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_parallel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new CountingNodeBackend();
            var engine = new PipelineEngine(new PipelineConfig(LogsRoot: tempDir, Backend: backend));
            var graph = new Graph { Goal = "parallel" };
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["parallel"] = new GraphNode { Id = "parallel", Shape = "component" };
            graph.Nodes["branch_a"] = new GraphNode { Id = "branch_a", Shape = "box", Prompt = "branch a" };
            graph.Nodes["branch_b"] = new GraphNode { Id = "branch_b", Shape = "box", Prompt = "branch b" };
            graph.Nodes["merge"] = new GraphNode { Id = "merge", Shape = "tripleoctagon" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "parallel" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "branch_a" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "branch_b" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_a", ToNode = "merge" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_b", ToNode = "merge" });
            graph.Edges.Add(new GraphEdge { FromNode = "merge", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Equal(1, backend.CallCounts.GetValueOrDefault("branch_a"));
            Assert.Equal(1, backend.CallCounts.GetValueOrDefault("branch_b"));
            Assert.Contains("branch_a", result.CompletedNodes);
            Assert.Contains("branch_b", result.CompletedNodes);
            Assert.Contains("merge", result.CompletedNodes);
            Assert.True(result.NodeOutcomes.ContainsKey("branch_a"));
            Assert.True(result.NodeOutcomes.ContainsKey("branch_b"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PipelineEngine_QueueParallelReportsPerItemCompletedStages()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_parallel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var queueDir = Path.Combine(tempDir, "queue");
        Directory.CreateDirectory(queueDir);
        File.WriteAllText(Path.Combine(queueDir, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(queueDir, "beta.txt"), "beta");

        try
        {
            var backend = new DeterministicBackend();
            var engine = new PipelineEngine(new PipelineConfig(LogsRoot: tempDir, Backend: backend));
            var graph = new Graph { Goal = "queue" };
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["parallel"] = new GraphNode
            {
                Id = "parallel",
                Shape = "component",
                RawAttributes = new Dictionary<string, string>
                {
                    ["queue_source"] = queueDir,
                    ["max_parallel"] = "2"
                }
            };
            graph.Nodes["worker"] = new GraphNode
            {
                Id = "worker",
                Shape = "box",
                Prompt = "Process ${context.queue.item.id}"
            };
            graph.Nodes["merge"] = new GraphNode { Id = "merge", Shape = "tripleoctagon" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "parallel" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "worker" });
            graph.Edges.Add(new GraphEdge { FromNode = "worker", ToNode = "merge" });
            graph.Edges.Add(new GraphEdge { FromNode = "merge", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("worker[alpha.txt]", result.CompletedNodes);
            Assert.Contains("worker[beta.txt]", result.CompletedNodes);
            Assert.True(result.NodeOutcomes.ContainsKey("worker[alpha.txt]"));
            Assert.True(result.NodeOutcomes.ContainsKey("worker[beta.txt]"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static Graph CreateQueueGraph(string queueSource)
    {
        var graph = new Graph { Goal = "queue" };
        graph.Nodes["parallel"] = new GraphNode
        {
            Id = "parallel",
            Shape = "component",
            RawAttributes = new Dictionary<string, string>
            {
                ["queue_source"] = queueSource,
                ["max_parallel"] = "2"
            }
        };
        graph.Nodes["worker"] = new GraphNode
        {
            Id = "worker",
            Shape = "box",
            Prompt = "Process ${context.queue.item.id} from ${context.queue.item.value}."
        };
        graph.Nodes["merge"] = new GraphNode { Id = "merge", Shape = "tripleoctagon" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "worker" });
        graph.Edges.Add(new GraphEdge { FromNode = "worker", ToNode = "merge" });
        graph.Edges.Add(new GraphEdge { FromNode = "merge", ToNode = "done" });
        return graph;
    }

    private static List<Dictionary<string, object?>> ParseParallelResults(string json)
    {
        return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json)!;
    }

    private sealed class QueueAwareBackend : ICodergenBackend
    {
        public Task<CodergenResult> RunAsync(
            string prompt,
            string? model = null,
            string? provider = null,
            string? reasoningEffort = null,
            CancellationToken ct = default,
            CodergenExecutionOptions? options = null)
        {
            var status = prompt.Contains("bad-item", StringComparison.OrdinalIgnoreCase)
                ? OutcomeStatus.Fail
                : OutcomeStatus.Success;

            return Task.FromResult(DeterministicBackend.Result(
                status: status,
                notes: $"queue item completed with {status}"));
        }
    }

    private sealed class CountingNodeBackend : ICodergenBackend
    {
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, int> CallCounts => _callCounts;

        public Task<CodergenResult> RunAsync(
            string prompt,
            string? model = null,
            string? provider = null,
            string? reasoningEffort = null,
            CancellationToken ct = default,
            CodergenExecutionOptions? options = null)
        {
            var nodeId = ExtractNodeId(prompt);
            _callCounts[nodeId] = _callCounts.GetValueOrDefault(nodeId) + 1;
            return Task.FromResult(DeterministicBackend.Result(notes: $"counted {nodeId}"));
        }

        private static string ExtractNodeId(string prompt)
        {
            const string marker = "executing node \"";
            var index = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return "unknown";

            index += marker.Length;
            var end = prompt.IndexOf('"', index);
            return end < 0 ? "unknown" : prompt[index..end];
        }
    }
}

public class TelemetryDrivenSupervisorTests
{
    [Fact]
    public async Task ManagerLoopHandler_TelemetryProgress_DoesNotFalseStall()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_supervisor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var telemetryPath = Path.Combine(tempDir, "events.jsonl");
        WriteTelemetryEvents(
            telemetryPath,
            new
            {
                event_type = "stage_end",
                node_id = "worker_a",
                status = "success",
                tool_calls = 3,
                touched_files_count = 1,
                total_tokens = 1200
            },
            new
            {
                event_type = "stage_end",
                node_id = "worker_b",
                status = "success",
                tool_calls = 2,
                touched_files_count = 1,
                total_tokens = 800
            });

        try
        {
            var backend = new SupervisorBackend();
            var handler = new ManagerLoopHandler(backend);
            var context = new PipelineContext();
            var node = new GraphNode
            {
                Id = "manager",
                Shape = "house",
                Prompt = "Supervise the worker",
                RawAttributes = new Dictionary<string, string>
                {
                    ["telemetry_source"] = telemetryPath,
                    ["max_cycles"] = "3",
                    ["stall_threshold"] = "1",
                    ["escalation_threshold"] = "2",
                    ["stop_condition"] = "context.manager.stage_end_count=2",
                    ["poll_interval_ms"] = "0",
                    ["steer_cooldown"] = "0"
                }
            };

            var outcome = await handler.ExecuteAsync(node, context, new Graph(), tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Equal(0, backend.CallCount);
            Assert.Equal("progressing", context.Get("manager.stall_status"));
            Assert.Equal("2", context.Get("manager.stage_end_count"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ManagerLoopHandler_WarnsWhenTelemetryStalls()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_supervisor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var telemetryPath = Path.Combine(tempDir, "events.jsonl");
        File.WriteAllText(telemetryPath, string.Empty);

        try
        {
            var backend = new SupervisorBackend(new Dictionary<string, string> { ["done"] = "true" });
            var handler = new ManagerLoopHandler(backend);
            var context = new PipelineContext();
            var node = new GraphNode
            {
                Id = "manager",
                Shape = "house",
                Prompt = "Supervise the worker",
                RawAttributes = new Dictionary<string, string>
                {
                    ["telemetry_source"] = telemetryPath,
                    ["max_cycles"] = "3",
                    ["stall_threshold"] = "1",
                    ["escalation_threshold"] = "3",
                    ["stop_condition"] = "context.done=true",
                    ["poll_interval_ms"] = "0",
                    ["steer_cooldown"] = "0"
                }
            };

            var outcome = await handler.ExecuteAsync(node, context, new Graph(), tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Equal(1, backend.CallCount);
            Assert.Equal("1", outcome.ContextUpdates!["manager.steering_count"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ManagerLoopHandler_EscalatesAfterSustainedNoProgress()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_supervisor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var telemetryPath = Path.Combine(tempDir, "missing-events.jsonl");

        try
        {
            var backend = new SupervisorBackend();
            var handler = new ManagerLoopHandler(backend);
            var context = new PipelineContext();
            var node = new GraphNode
            {
                Id = "manager",
                Shape = "house",
                Prompt = "Supervise the worker",
                RawAttributes = new Dictionary<string, string>
                {
                    ["telemetry_source"] = telemetryPath,
                    ["max_cycles"] = "4",
                    ["stall_threshold"] = "1",
                    ["escalation_threshold"] = "2",
                    ["poll_interval_ms"] = "0",
                    ["steer_cooldown"] = "100000"
                }
            };

            var outcome = await handler.ExecuteAsync(node, context, new Graph(), tempDir);

            Assert.Equal(OutcomeStatus.Retry, outcome.Status);
            Assert.Equal(1, backend.CallCount);
            Assert.Equal("true", outcome.ContextUpdates!["manager.escalated"]);
            Assert.Equal("escalated", outcome.ContextUpdates["manager.stall_status"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ManagerLoopHandler_RespectsSteeringCooldown()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_supervisor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var telemetryPath = Path.Combine(tempDir, "events.jsonl");
        File.WriteAllText(telemetryPath, string.Empty);

        try
        {
            var backend = new SupervisorBackend();
            var handler = new ManagerLoopHandler(backend);
            var context = new PipelineContext();
            var node = new GraphNode
            {
                Id = "manager",
                Shape = "house",
                Prompt = "Supervise the worker",
                RawAttributes = new Dictionary<string, string>
                {
                    ["telemetry_source"] = telemetryPath,
                    ["max_cycles"] = "3",
                    ["stall_threshold"] = "1",
                    ["escalation_threshold"] = "5",
                    ["poll_interval_ms"] = "0",
                    ["steer_cooldown"] = "100000"
                }
            };

            var outcome = await handler.ExecuteAsync(node, context, new Graph(), tempDir);

            Assert.Equal(OutcomeStatus.PartialSuccess, outcome.Status);
            Assert.Equal(1, backend.CallCount);
            Assert.Equal("1", outcome.ContextUpdates!["manager.steering_count"]);
            Assert.Equal("false", outcome.ContextUpdates["manager.escalated"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ManagerLoopHandler_SingleSteeringPassCanSucceed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_supervisor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var telemetryPath = Path.Combine(tempDir, "events.jsonl");
        File.WriteAllText(telemetryPath, string.Empty);

        try
        {
            var backend = new SupervisorBackend();
            var handler = new ManagerLoopHandler(backend);
            var context = new PipelineContext();
            var node = new GraphNode
            {
                Id = "manager",
                Shape = "house",
                Prompt = "Supervise the worker",
                RawAttributes = new Dictionary<string, string>
                {
                    ["telemetry_source"] = telemetryPath,
                    ["max_cycles"] = "1",
                    ["stall_threshold"] = "1",
                    ["escalation_threshold"] = "5",
                    ["poll_interval_ms"] = "0",
                    ["steer_cooldown"] = "0"
                }
            };

            var outcome = await handler.ExecuteAsync(node, context, new Graph(), tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Equal(1, backend.CallCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static void WriteTelemetryEvents(string path, params object[] events)
    {
        var lines = events.Select(item => JsonSerializer.Serialize(item));
        File.WriteAllLines(path, lines);
    }

    private sealed class SupervisorBackend : ICodergenBackend
    {
        private readonly Dictionary<string, string>? _contextUpdates;

        public SupervisorBackend(Dictionary<string, string>? contextUpdates = null)
        {
            _contextUpdates = contextUpdates;
        }

        public int CallCount { get; private set; }

        public Task<CodergenResult> RunAsync(
            string prompt,
            string? model = null,
            string? provider = null,
            string? reasoningEffort = null,
            CancellationToken ct = default,
            CodergenExecutionOptions? options = null)
        {
            CallCount++;
            return Task.FromResult(new CodergenResult(
                Response: prompt,
                Status: OutcomeStatus.Success,
                ContextUpdates: _contextUpdates));
        }
    }
}

public class BuilderEditorWorkflowTests
{
    [Fact]
    public void BuilderCommandSupport_InitCreatesSkeletonGraph()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_builder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dotFilePath = Path.Combine(tempDir, "workflow.dot");

        try
        {
            var graph = BuilderCommandSupport.InitializeGraph("workflow", "Ship the builder");
            BuilderCommandSupport.Save(dotFilePath, graph);

            Assert.True(File.Exists(dotFilePath));
            var loaded = DotParser.Parse(File.ReadAllText(dotFilePath));
            Assert.Equal("workflow", loaded.Name);
            Assert.Equal("Ship the builder", loaded.Goal);
            Assert.Contains("start", loaded.Nodes.Keys);
            Assert.Contains("done", loaded.Nodes.Keys);
            Assert.Contains(loaded.Edges, edge => edge.FromNode == "start" && edge.ToNode == "done");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuilderCommandSupport_NodeAndEdgeEditingProducesParseableDot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_builder_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dotFilePath = Path.Combine(tempDir, "workflow.dot");

        try
        {
            var graph = BuilderCommandSupport.InitializeGraph("workflow", "Ship the builder");
            BuilderCommandSupport.UpsertNode(graph, "write", new Dictionary<string, string>
            {
                ["shape"] = "box",
                ["label"] = "Write Step",
                ["prompt"] = "Write hello.txt"
            });
            BuilderCommandSupport.UpsertEdge(graph, "start", "write", new Dictionary<string, string>());
            BuilderCommandSupport.UpsertEdge(graph, "write", "done", new Dictionary<string, string>());
            BuilderCommandSupport.Save(dotFilePath, graph);

            var loaded = DotParser.Parse(File.ReadAllText(dotFilePath));
            Assert.Contains("write", loaded.Nodes.Keys);
            Assert.Equal("box", loaded.Nodes["write"].Shape);
            Assert.Equal("Write hello.txt", loaded.Nodes["write"].Prompt);
            Assert.DoesNotContain(loaded.Edges, edge => edge.FromNode == "start" && edge.ToNode == "done");
            Assert.Contains(loaded.Edges, edge => edge.FromNode == "start" && edge.ToNode == "write");
            Assert.Contains(loaded.Edges, edge => edge.FromNode == "write" && edge.ToNode == "done");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuilderCommandSupport_DescribeSummarizesGraph()
    {
        var graph = BuilderCommandSupport.InitializeGraph("workflow", "Ship the builder");
        BuilderCommandSupport.UpsertNode(graph, "review", new Dictionary<string, string>
        {
            ["shape"] = "box",
            ["label"] = "Review"
        });
        BuilderCommandSupport.UpsertEdge(graph, "start", "review", new Dictionary<string, string>());
        BuilderCommandSupport.UpsertEdge(graph, "review", "done", new Dictionary<string, string>());

        var summary = BuilderCommandSupport.Describe(graph);

        Assert.Contains("Graph: workflow", summary);
        Assert.Contains("review [box]", summary);
        Assert.Contains("start -> review", summary);
    }
}
