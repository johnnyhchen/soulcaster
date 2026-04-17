using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using Soulcaster.Attractor;
using Soulcaster.CodingAgent;
using Soulcaster.Runner;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.Tests;

public class GlobRegressionTests
{
    [Fact]
    public async Task GlobAsync_PathOverride_RemainsAuthoritative_WhenPatternHasFixedSegments()
    {
        var tempDir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            Directory.CreateDirectory(Path.Combine(tempDir, "subtree", "src"));
            File.WriteAllText(Path.Combine(tempDir, "src", "root.cs"), "// root");
            File.WriteAllText(Path.Combine(tempDir, "subtree", "src", "child.cs"), "// child");

            var env = new LocalExecutionEnvironment(tempDir);
            var results = await env.GlobAsync("src/**/*.cs", path: "subtree");

            Assert.Single(results);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "subtree", "src", "child.cs")),
                results[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GlobAsync_PathOverride_ReturnsNestedMatches()
    {
        var tempDir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "sub", "nested"));
            File.WriteAllText(Path.Combine(tempDir, "sub", "root.txt"), "root");
            File.WriteAllText(Path.Combine(tempDir, "sub", "nested", "child.txt"), "child");

            var env = new LocalExecutionEnvironment(tempDir);
            var results = await env.GlobAsync("**/*.txt", path: "sub");

            Assert.Equal(2, results.Count);
            Assert.Equal(
                new[]
                {
                    Path.GetFullPath(Path.Combine(tempDir, "sub", "nested", "child.txt")),
                    Path.GetFullPath(Path.Combine(tempDir, "sub", "root.txt"))
                },
                results);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GlobAsync_MaxResults_ReturnsSortedSubset()
    {
        var tempDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "c.txt"), "c");
            File.WriteAllText(Path.Combine(tempDir, "a.txt"), "a");
            File.WriteAllText(Path.Combine(tempDir, "b.txt"), "b");

            var env = new LocalExecutionEnvironment(tempDir);
            var results = await env.GlobAsync("*.txt", maxResults: 2);

            Assert.Equal(
                new[]
                {
                    Path.GetFullPath(Path.Combine(tempDir, "a.txt")),
                    Path.GetFullPath(Path.Combine(tempDir, "b.txt"))
                },
                results);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AllProfiles_GlobExposePath_AndMaxResults()
    {
        foreach (var profile in new IProviderProfile[]
                 {
                     new AnthropicProfile(),
                     new OpenAIProfile(),
                     new GeminiProfile()
                 })
        {
            var globTool = profile.Tools().First(t => t.Name == "glob");
            Assert.Contains(globTool.Parameters, p => p.Name == "path");
            Assert.Contains(globTool.Parameters, p => p.Name == "max_results");
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jc_glob_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public class AnthropicAdapterRegressionTests
{
    [Fact]
    public void AnthropicAdapter_UsesHaikuSafeReasoningTokenBudget()
    {
        var adapter = new AnthropicAdapter("test-key");
        var request = new Request
        {
            Model = "claude-haiku-4-5",
            Messages = new List<Message> { Message.UserMsg("test") },
            ReasoningEffort = "medium"
        };

        var method = typeof(AnthropicAdapter).GetMethod(
            "BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = (JsonObject?)method!.Invoke(adapter, new object[] { request, false });
        Assert.NotNull(body);
        Assert.Equal(64000, body!["max_tokens"]!.GetValue<int>());
    }
}

public class FanInRegressionTests
{
    [Fact]
    public async Task FanInHandler_ReturnsFail_WhenBestResultIsRetry()
    {
        var handler = new FanInHandler();
        var context = new PipelineContext();
        var node = new GraphNode { Id = "fan_in", Shape = "tripleoctagon" };
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_fanin_{Guid.NewGuid():N}");

        context.Set("parallel.results", JsonSerializer.Serialize(new List<Dictionary<string, object?>>
        {
            new() { ["node_id"] = "branch_a", ["status"] = "retry", ["notes"] = "try later" },
            new() { ["node_id"] = "branch_b", ["status"] = "fail", ["notes"] = "failed" }
        }));

        try
        {
            var outcome = await handler.ExecuteAsync(node, context, new Graph(), tempDir);

            Assert.Equal(OutcomeStatus.Fail, outcome.Status);
            Assert.True(context.Has("fan_in.ranked_results"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}

public class PipelineEngineRegressionTests
{
    [Fact]
    public async Task PipelineEngine_ExecutesParallelMultiHop_ThroughFanIn_AndContinuesDownstream()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_parallel_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var graph = new Graph { Goal = "multi-hop" };
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["parallel"] = new GraphNode { Id = "parallel", Shape = "component" };
            graph.Nodes["branch_a"] = new GraphNode { Id = "branch_a", Shape = "box", Prompt = "a" };
            graph.Nodes["branch_a2"] = new GraphNode { Id = "branch_a2", Shape = "box", Prompt = "a2" };
            graph.Nodes["branch_b"] = new GraphNode { Id = "branch_b", Shape = "box", Prompt = "b" };
            graph.Nodes["merge"] = new GraphNode { Id = "merge", Shape = "tripleoctagon" };
            graph.Nodes["verify"] = new GraphNode { Id = "verify", Shape = "box", Prompt = "verify" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "parallel" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "branch_a" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "branch_b" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_a", ToNode = "branch_a2" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_a2", ToNode = "merge" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_b", ToNode = "merge" });
            graph.Edges.Add(new GraphEdge { FromNode = "merge", ToNode = "verify" });
            graph.Edges.Add(new GraphEdge { FromNode = "verify", ToNode = "done" });

            var engine = new PipelineEngine(new PipelineConfig(LogsRoot: tempDir, Backend: backend));
            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("parallel", result.CompletedNodes);
            Assert.Contains("merge", result.CompletedNodes);
            Assert.Contains("verify", result.CompletedNodes);
            Assert.True(result.FinalContext.Has("fan_in.ranked_results"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PipelineEngine_ImplicitFork_DrainsSiblingBranches_BeforeJoin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_implicitfork_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new Soulcaster.Tests.Helpers.DeterministicBackend()
                .On("fork", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(
                    contextUpdates: new Dictionary<string, string> { ["outcome"] = "needs_dod" },
                    notes: "fan out"))
                .On("branch_gpt", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "gpt"))
                .On("branch_opus", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "opus"))
                .On("branch_gemini", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "gemini"))
                .On("join", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "join"));

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["fork"] = new GraphNode { Id = "fork", Shape = "box", Prompt = "fork" };
            graph.Nodes["branch_gpt"] = new GraphNode { Id = "branch_gpt", Shape = "box", Prompt = "gpt" };
            graph.Nodes["branch_opus"] = new GraphNode { Id = "branch_opus", Shape = "box", Prompt = "opus" };
            graph.Nodes["branch_gemini"] = new GraphNode { Id = "branch_gemini", Shape = "box", Prompt = "gemini" };
            graph.Nodes["join"] = new GraphNode { Id = "join", Shape = "box", Prompt = "join" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "fork" });
            graph.Edges.Add(new GraphEdge { FromNode = "fork", ToNode = "branch_gpt", Condition = "outcome=needs_dod" });
            graph.Edges.Add(new GraphEdge { FromNode = "fork", ToNode = "branch_opus", Condition = "outcome=needs_dod" });
            graph.Edges.Add(new GraphEdge { FromNode = "fork", ToNode = "branch_gemini", Condition = "outcome=needs_dod" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_gpt", ToNode = "join" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_opus", ToNode = "join" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_gemini", ToNode = "join" });
            graph.Edges.Add(new GraphEdge { FromNode = "join", ToNode = "done" });

            var engine = new PipelineEngine(new PipelineConfig(LogsRoot: tempDir, Backend: backend));
            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("branch_gpt", result.CompletedNodes);
            Assert.Contains("branch_opus", result.CompletedNodes);
            Assert.Contains("branch_gemini", result.CompletedNodes);

            var joinIndex = result.CompletedNodes.IndexOf("join");
            Assert.True(joinIndex > result.CompletedNodes.IndexOf("branch_gpt"));
            Assert.True(joinIndex > result.CompletedNodes.IndexOf("branch_opus"));
            Assert.True(joinIndex > result.CompletedNodes.IndexOf("branch_gemini"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PipelineEngine_ImplicitFork_IgnoresUnsupportedRouteHints()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_implicitforkhints_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new Soulcaster.Tests.Helpers.DeterministicBackend()
                .On("fork", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(
                    preferredNextLabel: "not-a-real-label",
                    suggestedNextIds: ["missing-node"],
                    contextUpdates: new Dictionary<string, string> { ["outcome"] = "needs_dod" },
                    notes: "fan out despite bad hints"))
                .On("branch_a", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "a"))
                .On("branch_b", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "b"))
                .On("join", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "join"));

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["fork"] = new GraphNode { Id = "fork", Shape = "box", Prompt = "fork" };
            graph.Nodes["branch_a"] = new GraphNode { Id = "branch_a", Shape = "box", Prompt = "a" };
            graph.Nodes["branch_b"] = new GraphNode { Id = "branch_b", Shape = "box", Prompt = "b" };
            graph.Nodes["join"] = new GraphNode { Id = "join", Shape = "box", Prompt = "join" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "fork" });
            graph.Edges.Add(new GraphEdge { FromNode = "fork", ToNode = "branch_a", Condition = "outcome=needs_dod" });
            graph.Edges.Add(new GraphEdge { FromNode = "fork", ToNode = "branch_b", Condition = "outcome=needs_dod" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_a", ToNode = "join" });
            graph.Edges.Add(new GraphEdge { FromNode = "branch_b", ToNode = "join" });
            graph.Edges.Add(new GraphEdge { FromNode = "join", ToNode = "done" });

            var engine = new PipelineEngine(new PipelineConfig(LogsRoot: tempDir, Backend: backend));
            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("branch_a", result.CompletedNodes);
            Assert.Contains("branch_b", result.CompletedNodes);
            Assert.Contains("join", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PipelineEngine_ExitNode_WritesTelemetryAndStatusArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_exittelemetry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new Soulcaster.Tests.Helpers.DeterministicBackend()
                .On("step", _ => Soulcaster.Tests.Helpers.DeterministicBackend.Result(notes: "step complete"));

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["step"] = new GraphNode { Id = "step", Shape = "box", Prompt = "step" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "step" });
            graph.Edges.Add(new GraphEdge { FromNode = "step", ToNode = "done" });

            var engine = new PipelineEngine(new PipelineConfig(LogsRoot: tempDir, Backend: backend));
            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);

            var doneStatusPath = Path.Combine(tempDir, "done", "status.json");
            Assert.True(File.Exists(doneStatusPath));

            using var doneStatus = JsonDocument.Parse(File.ReadAllText(doneStatusPath));
            Assert.Equal("success", doneStatus.RootElement.GetProperty("status").GetString());

            var events = Soulcaster.Tests.Helpers.ProcessRunHarness.ReadEvents(Path.Combine(tempDir, "events.jsonl"));
            Assert.Contains(events, evt => Equals(evt["node_id"], "done") && Equals(evt["event_type"], "stage_start"));
            Assert.Contains(events, evt => Equals(evt["node_id"], "done") && Equals(evt["event_type"], "stage_end"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}

public class CodergenPromptPathRegressionTests
{
    [Fact]
    public async Task CodergenHandler_RewritesArtifactShorthand_ToAbsolutePipelinePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_promptpaths_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new PromptCapturingBackend();
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Goal = "artifact paths" };
            var node = new GraphNode
            {
                Id = "writer",
                Shape = "box",
                Prompt = "Write to logs/checkpoint_test/STEP-A.md and inspect gates/review-gate.json."
            };
            graph.Nodes[node.Id] = node;

            await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            var normalizedPrompt = backend.LastPrompt!.Replace('\\', '/');
            Assert.Contains($"{tempDir.Replace('\\', '/')}/checkpoint_test/STEP-A.md", normalizedPrompt);
            Assert.Contains($"{Path.Combine(Path.GetDirectoryName(tempDir)!, "gates").Replace('\\', '/')}/review-gate.json", normalizedPrompt);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CodergenHandler_DefaultFidelity_FallsBackToCompact()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_promptfidelity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var backend = new PromptCapturingBackend();
            var handler = new CodergenHandler(backend);
            var graph = new Graph { Goal = "compact fidelity" };
            var node = new GraphNode
            {
                Id = "writer",
                Shape = "box",
                Prompt = "Write the artifact."
            };
            graph.Nodes[node.Id] = node;

            var outcome = await handler.ExecuteAsync(node, new PipelineContext(), graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Contains("Runtime fidelity: compact", backend.LastPrompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private sealed class PromptCapturingBackend : ICodergenBackend
    {
        public string? LastPrompt { get; private set; }

        public Task<CodergenResult> RunAsync(
            string prompt,
            string? model = null,
            string? provider = null,
            string? reasoningEffort = null,
            CancellationToken ct = default,
            CodergenExecutionOptions? options = null)
        {
            LastPrompt = prompt;
            return Task.FromResult(new CodergenResult(
                Response: "ok",
                Status: OutcomeStatus.Success,
                StageStatus: new StageStatusContract(
                    Status: OutcomeStatus.Success,
                    PreferredNextLabel: string.Empty,
                    SuggestedNextIds: new List<string>(),
                    ContextUpdates: new Dictionary<string, string>(),
                    Notes: "ok")));
        }
    }
}

public class RunCommandSupportTests
{
    [Fact]
    public void RunOptions_Parse_RecognizesResumeStartAtSteerAndBackendFlags()
    {
        var options = RunOptions.Parse(
            [
                "qa-smoke.dot",
                "--resume-from", "/tmp/existing-run",
                "--start-at", "verify",
                "--steer-text", "Focus on tests",
                "--autoresume-policy", "always",
                "--steer-file", "/tmp/steer.txt",
                "--backend", "scripted",
                "--backend-script", "/tmp/backend.json",
                "--crash-after-stage", "verify",
                "--var", "task=Ship reporter",
                "--var", "definition_of_done=CLI plus tests"
            ]);

        Assert.Equal("qa-smoke.dot", options.DotFilePath);
        Assert.Equal("/tmp/existing-run", options.ResumeFrom);
        Assert.Equal("verify", options.StartAt);
        Assert.Equal("Focus on tests", options.SteerText);
        Assert.Equal(AutoResumePolicy.Always, options.AutoResumePolicy);
        Assert.Equal("/tmp/steer.txt", options.SteerFilePath);
        Assert.Equal("scripted", options.BackendMode);
        Assert.Equal("/tmp/backend.json", options.BackendScriptPath);
        Assert.Equal("verify", options.CrashAfterStage);
        Assert.Equal("Ship reporter", options.Variables!["task"]);
        Assert.Equal("CLI plus tests", options.Variables!["definition_of_done"]);
    }

    [Fact]
    public void ResolveWorkingDirectory_UsesResumeFromDirectory()
    {
        var options = new RunOptions(
            "qa-smoke.dot",
            Resume: false,
            AutoResumePolicy: AutoResumePolicy.On,
            ResumeFrom: "/tmp/existing-run",
            StartAt: null,
            SteerText: null,
            SteerFilePath: null,
            BackendMode: "live",
            BackendScriptPath: null,
            CrashAfterStage: null,
            Variables: null);
        var workingDir = RunCommandSupport.ResolveWorkingDirectory("/repo/dotfiles/qa-smoke.dot", options);

        Assert.Equal(Path.GetFullPath("/tmp/existing-run"), workingDir);
    }

    [Fact]
    public void ResolveProjectRoot_UsesParentOfDotfilesDirectory()
    {
        var projectRoot = RunCommandSupport.ResolveProjectRoot("/repo/project/dotfiles/sample.dot");

        Assert.Equal(Path.GetFullPath("/repo/project"), projectRoot);
    }

    [Fact]
    public void ResolveProjectRoot_UsesDotfileDirectoryWhenNotNestedUnderDotfiles()
    {
        var projectRoot = RunCommandSupport.ResolveProjectRoot("/repo/tmp/simple.dot");

        Assert.Equal(Path.GetFullPath("/repo/tmp"), projectRoot);
    }

    [Fact]
    public void TryApplyStartAt_OverridesCheckpointCurrentNode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_startat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["verify"] = new GraphNode { Id = "verify", Shape = "box", Prompt = "verify" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            var checkpoint = new Checkpoint(
                CurrentNodeId: "start",
                CompletedNodes: new List<string> { "start" },
                ContextData: new Dictionary<string, string> { ["progress"] = "saved" },
                RetryCounts: new Dictionary<string, int> { ["verify"] = 1 });
            checkpoint.Save(tempDir);

            var applied = RunCommandSupport.TryApplyStartAt(graph, tempDir, "verify", out var error);
            var updated = Checkpoint.Load(tempDir);

            Assert.True(applied, error);
            Assert.NotNull(updated);
            Assert.Equal("verify", updated!.CurrentNodeId);
            Assert.Contains("start", updated.CompletedNodes);
            Assert.Equal("saved", updated.ContextData["progress"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

public class SteerTextRegressionTests
{
    [Fact]
    public async Task AgentCodergenBackend_InjectsSteerText_OnlyOnFirstSessionRun()
    {
        var provider = new SteeringCaptureProvider();
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            initialSteerText: "Keep the changes minimal.",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new FakeProfile(provider),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxTurns: 4)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: runner-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Return stage status JSON.
""";

        var first = await backend.RunAsync(prompt, model: "gpt-5.2", provider: "openai");
        var second = await backend.RunAsync(prompt, model: "gpt-5.2", provider: "openai");

        Assert.Equal(OutcomeStatus.Success, first.Status);
        Assert.Equal(OutcomeStatus.Success, second.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(
            provider.Requests[0].Messages,
            message => message.Text.Contains("[System Guidance]: Keep the changes minimal.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            provider.Requests[1].Messages,
            message => message.Text.Contains("[System Guidance]: Keep the changes minimal.", StringComparison.Ordinal));
    }

    private sealed class SteeringCaptureProvider : IProviderAdapter
    {
        public string Name => "capture";
        public List<Request> Requests { get; } = new();

        public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var statusJson = """
            {
              "status": "success",
              "preferred_next_label": "",
              "suggested_next_ids": [],
              "context_updates": {},
              "notes": "ok"
            }
            """;

            return Task.FromResult(new Response(
                Id: Guid.NewGuid().ToString("N"),
                Model: request.Model,
                Provider: Name,
                Message: Message.AssistantMsg(statusJson),
                FinishReason: FinishReason.Stop,
                Usage: Usage.Empty));
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Request request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

public class CodergenLoopTerminationRegressionTests
{
    [Fact]
    public async Task RunnerBackendFactory_ScriptedBackend_UsesSessionConfigDefaultProviderTimeout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_runnerdefaulttimeout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var planPath = Path.Combine(tempDir, "backend-plan.json");
            var plan = new ScriptedBackendPlan
            {
                DefaultResponse = new ScriptedResponsePlan
                {
                    AssistantText = """
                        {
                          "status": "success",
                          "preferred_next_label": "",
                          "suggested_next_ids": [],
                          "context_updates": {},
                          "notes": "ok"
                        }
                        """
                }
            };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

            var options = new RunOptions(
                DotFilePath: Path.Combine(tempDir, "dummy.dot"),
                Resume: false,
                AutoResumePolicy: AutoResumePolicy.Off,
                ResumeFrom: null,
                StartAt: null,
                SteerText: null,
                SteerFilePath: null,
                BackendMode: "scripted",
                BackendScriptPath: planPath,
                CrashAfterStage: null,
                Variables: null);

            using var backend = RunnerBackendFactory.Create(tempDir, tempDir, options);

            const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: timeout-default-test
Resume mode: fresh
[/PIPELINE CONTEXT]

You are executing node "plan".

Return stage status JSON.
""";

            var result = await backend.RunAsync(prompt, model: "gpt-5.2", provider: "openai");

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Equal(
                new SessionConfig().MaxProviderResponseMs,
                Convert.ToInt32(result.Telemetry!["provider_timeout_ms"], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AgentCodergenBackend_ExplorationStall_ReturnsRetryWithoutExtraStatusReminders()
    {
        var provider = new ExplorationLoopProvider();
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new OpenAIProfile(),
                new FakeExecutionEnvironment(),
                new SessionConfig(
                    MaxToolRoundsPerInput: 50,
                    MaxConsecutiveExplorationRounds: 3)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: loop-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Retry, result.Status);
        Assert.Contains("Exploration stall detected", result.RawAssistantResponse);
        Assert.Equal(4, provider.RequestCount);
    }

    [Fact]
    public async Task AgentCodergenBackend_ModelTimeout_ReturnsFail()
    {
        var provider = new SlowProvider();
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new FakeProfile(provider),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 50)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: timeout-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Fail, result.Status);
        Assert.Contains("Model response timeout reached", result.RawAssistantResponse);
    }

    [Fact]
    public async Task AgentCodergenBackend_RateLimitError_ReturnsRetryWithTypedTelemetry()
    {
        var provider = new ThrowingProvider(new RateLimitError("rate limited"));
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new FakeProfile(provider),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: rate-limit-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Retry, result.Status);
        Assert.Equal("rate_limited", result.Telemetry!["provider_state"]);
        Assert.Equal("provider_rate_limit", result.Telemetry["failure_kind"]);
        Assert.Equal(429L, Convert.ToInt64(result.Telemetry["provider_status_code"]));
        Assert.Equal(true, result.Telemetry["provider_retryable"]);
    }

    [Fact]
    public async Task AgentCodergenBackend_PlainTextProvider503_IsClassifiedAsRetryable()
    {
        const string providerMessage = "[Error: Gemini upstream 503: This model is currently experiencing high demand.]";
        var provider = new LiteralResponseProvider(providerMessage);
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new GeminiProfile(),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: plain-text-503-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gemini-3-flash-preview", provider: "gemini");

        Assert.Equal(OutcomeStatus.Retry, result.Status);
        Assert.Equal("rate_limited", result.Telemetry!["provider_state"]);
        Assert.Equal("provider_rate_limit", result.Telemetry["failure_kind"]);
        Assert.Equal(503L, Convert.ToInt64(result.Telemetry["provider_status_code"]));
        Assert.Equal(true, result.Telemetry["provider_retryable"]);
        Assert.Equal("Gemini upstream 503: This model is currently experiencing high demand.", result.Telemetry["provider_error_message"]);
    }

    [Fact]
    public async Task AgentCodergenBackend_ConfigurationError_ReturnsFailWithTypedTelemetry()
    {
        var provider = new ThrowingProvider(new ConfigurationError("misconfigured provider"));
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new FakeProfile(provider),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: config-error-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Fail, result.Status);
        Assert.Equal("config_error", result.Telemetry!["provider_state"]);
        Assert.Equal("configuration_error", result.Telemetry["failure_kind"]);
    }

    [Fact]
    public async Task AgentCodergenBackend_ModelDoesNotExistMessage_IsClassifiedAsNotFound()
    {
        var provider = new ThrowingProvider(new ProviderError(
            "The requested model 'codex-does-not-exist' does not exist.",
            HttpStatusCode.BadRequest,
            retryable: false,
            providerName: "openai"));
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new FakeProfile(provider),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: model-not-found-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Fail, result.Status);
        Assert.Equal("not_found", result.Telemetry!["provider_state"]);
        Assert.Equal("provider_not_found", result.Telemetry["failure_kind"]);
    }

    [Fact]
    public async Task AgentCodergenBackend_EditFileAndWrappedVerification_AreCountedInTelemetry()
    {
        var provider = new EditAndVerifyProvider();
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new GeminiProfile(),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: verification-telemetry-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gemini-3-flash-preview", provider: "gemini");

        Assert.Equal(OutcomeStatus.Success, result.Status);
        Assert.Equal(1L, Convert.ToInt64(result.Telemetry!["touched_files_count"]));
        Assert.Equal("passed", result.Telemetry["verification_state"]);
        var verificationCommands = Assert.IsAssignableFrom<IEnumerable<object?>>(result.Telemetry["verification_commands"]);
        Assert.Contains(
            verificationCommands.Select(item => item?.ToString()),
            command => string.Equals(command, "uv run pytest tests/unit -q", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentCodergenBackend_QueueValidationCheck_IsCapturedInTelemetry()
    {
        var provider = new EditAndQueueValidationProvider();
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new OpenAIProfile(),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: queued-validation-telemetry-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Success, result.Status);
        Assert.Equal(1L, Convert.ToInt64(result.Telemetry!["queued_validation_check_count"]));
        var queuedChecks = Assert.IsAssignableFrom<IEnumerable<RuntimeValidationCheckRegistration>>(result.Telemetry["queued_validation_checks"]);
        var queuedCheck = Assert.Single(queuedChecks);
        Assert.Equal("command", queuedCheck.Kind);
        Assert.Equal("unit-tests", queuedCheck.Name);
        Assert.Equal("uv run pytest tests/unit -q", queuedCheck.Command);
        Assert.True(queuedCheck.Required);
    }

    [Fact]
    public async Task AgentCodergenBackend_QueueStructuredValidationCheck_IsCapturedInTelemetry()
    {
        var provider = new EditAndQueueStructuredValidationProvider();
        var backend = new AgentCodergenBackend(
            workingDir: "/tmp/run",
            projectRoot: "/tmp/project",
            sessionFactory: (_, _, _, _) => new Session(
                provider,
                new OpenAIProfile(),
                new FakeExecutionEnvironment(),
                new SessionConfig(MaxProviderResponseMs: 1_000)));

        const string prompt = """
[PIPELINE CONTEXT]
Runtime fidelity: truncate
Runtime thread: queued-structured-validation-telemetry-test
Resume mode: fresh
[/PIPELINE CONTEXT]

Implement the requested change and return stage status JSON.
""";

        var result = await backend.RunAsync(prompt, model: "gpt-5.4", provider: "openai");

        Assert.Equal(OutcomeStatus.Success, result.Status);
        Assert.Equal(1L, Convert.ToInt64(result.Telemetry!["queued_validation_check_count"]));
        var queuedChecks = Assert.IsAssignableFrom<IEnumerable<RuntimeValidationCheckRegistration>>(result.Telemetry["queued_validation_checks"]);
        var queuedCheck = Assert.Single(queuedChecks);
        Assert.Equal("file_content", queuedCheck.Kind);
        Assert.Equal("output-status", queuedCheck.Name);
        Assert.Equal("/tmp/project/out/report.json", queuedCheck.Path);
        Assert.Equal("status", queuedCheck.JsonPath);
        Assert.Equal("\"ok\"", queuedCheck.ExpectedValueJson);
        Assert.True(queuedCheck.Required);
    }

    private sealed class ThrowingProvider : IProviderAdapter
    {
        private readonly Exception _exception;

        public ThrowingProvider(Exception exception)
        {
            _exception = exception;
        }

        public string Name => "throwing";

        public Task<Response> CompleteAsync(Request request, CancellationToken ct = default) =>
            Task.FromException<Response>(_exception);

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Request request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EditAndVerifyProvider : IProviderAdapter
    {
        public string Name => "edit-and-verify";
        private int _callCount;

        public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                var parts = new List<ContentPart>
                {
                    ContentPart.ToolCallPart(new ToolCallData(
                        "edit-1",
                        "edit_file",
                        """{"file_path":"/fake/workdir/src/app.cs","old_string":"before","new_string":"after"}""")),
                    ContentPart.ToolCallPart(new ToolCallData(
                        "verify-1",
                        "shell",
                        """{"command":"uv run pytest tests/unit -q"}"""))
                };

                return Task.FromResult(new Response(
                    Id: "resp-1",
                    Model: request.Model,
                    Provider: Name,
                    Message: new Message(Role.Assistant, parts),
                    FinishReason: FinishReason.ToolCalls,
                    Usage: Usage.Empty));
            }

            var statusJson = """
            {
              "status": "success",
              "preferred_next_label": "",
              "suggested_next_ids": [],
              "context_updates": {},
              "notes": "implemented and verified"
            }
            """;

            return Task.FromResult(new Response(
                Id: "resp-2",
                Model: request.Model,
                Provider: Name,
                Message: Message.AssistantMsg(statusJson),
                FinishReason: FinishReason.Stop,
                Usage: Usage.Empty));
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Request request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class LiteralResponseProvider : IProviderAdapter
    {
        private readonly string _responseText;

        public LiteralResponseProvider(string responseText)
        {
            _responseText = responseText;
        }

        public string Name => "literal-response";

        public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
        {
            return Task.FromResult(new Response(
                Id: "resp-literal",
                Model: request.Model,
                Provider: Name,
                Message: Message.AssistantMsg(_responseText),
                FinishReason: FinishReason.Stop,
                Usage: Usage.Empty));
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Request request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EditAndQueueValidationProvider : IProviderAdapter
    {
        public string Name => "edit-and-queue-validation";
        private int _callCount;

        public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                var parts = new List<ContentPart>
                {
                    ContentPart.ToolCallPart(new ToolCallData(
                        "edit-1",
                        "edit_file",
                        """{"file_path":"/fake/workdir/src/app.cs","old_string":"before","new_string":"after"}""")),
                    ContentPart.ToolCallPart(new ToolCallData(
                        "queue-1",
                        "queue_validation_check",
                        """{"kind":"command","name":"unit-tests","command":"uv run pytest tests/unit -q","required":true}"""))
                };

                return Task.FromResult(new Response(
                    Id: "resp-1",
                    Model: request.Model,
                    Provider: Name,
                    Message: new Message(Role.Assistant, parts),
                    FinishReason: FinishReason.ToolCalls,
                    Usage: Usage.Empty));
            }

            var statusJson = """
            {
              "status": "success",
              "preferred_next_label": "",
              "suggested_next_ids": [],
              "context_updates": {},
              "notes": "implemented and queued validation"
            }
            """;

            return Task.FromResult(new Response(
                Id: "resp-2",
                Model: request.Model,
                Provider: Name,
                Message: Message.AssistantMsg(statusJson),
                FinishReason: FinishReason.Stop,
                Usage: Usage.Empty));
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Request request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class EditAndQueueStructuredValidationProvider : IProviderAdapter
    {
        public string Name => "edit-and-queue-structured-validation";
        private int _callCount;

        public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                var parts = new List<ContentPart>
                {
                    ContentPart.ToolCallPart(new ToolCallData(
                        "edit-1",
                        "edit_file",
                        """{"file_path":"/fake/workdir/src/app.cs","old_string":"before","new_string":"after"}""")),
                    ContentPart.ToolCallPart(new ToolCallData(
                        "queue-1",
                        "queue_validation_check",
                        """{"kind":"file_content","name":"output-status","path":"/tmp/project/out/report.json","json_path":"status","expected_value_json":"\"ok\"","required":true}"""))
                };

                return Task.FromResult(new Response(
                    Id: "resp-1",
                    Model: request.Model,
                    Provider: Name,
                    Message: new Message(Role.Assistant, parts),
                    FinishReason: FinishReason.ToolCalls,
                    Usage: Usage.Empty));
            }

            var statusJson = """
            {
              "status": "success",
              "preferred_next_label": "",
              "suggested_next_ids": [],
              "context_updates": {},
              "notes": "implemented and queued structured validation"
            }
            """;

            return Task.FromResult(new Response(
                Id: "resp-2",
                Model: request.Model,
                Provider: Name,
                Message: Message.AssistantMsg(statusJson),
                FinishReason: FinishReason.Stop,
                Usage: Usage.Empty));
        }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            Request request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
