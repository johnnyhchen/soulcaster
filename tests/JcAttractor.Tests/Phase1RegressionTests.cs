using System.Text.Json;
using System.Text.Json.Nodes;
using JcAttractor.Attractor;
using JcAttractor.CodingAgent;
using JcAttractor.Runner;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.Tests;

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
                     new OpenAiProfile(),
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

    private sealed class PromptCapturingBackend : ICodergenBackend
    {
        public string? LastPrompt { get; private set; }

        public Task<CodergenResult> RunAsync(
            string prompt,
            string? model = null,
            string? provider = null,
            string? reasoningEffort = null,
            CancellationToken ct = default)
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
                "--crash-after-stage", "verify"
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
            CrashAfterStage: null);
        var workingDir = RunCommandSupport.ResolveWorkingDirectory("/repo/dotfiles/qa-smoke.dot", options);

        Assert.Equal(Path.GetFullPath("/tmp/existing-run"), workingDir);
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
            sessionFactory: (_, _, _) => new Session(
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
