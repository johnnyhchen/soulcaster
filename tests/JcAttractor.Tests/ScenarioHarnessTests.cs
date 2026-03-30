using JcAttractor.Attractor;

namespace JcAttractor.Tests;

[Trait("Harness", "Scenario")]
public class ScenarioHarnessTests
{
    [Fact]
    public async Task ScenarioHarness_LinearSmokePipeline_CompletesSuccessfully()
    {
        const string dot = """
        digraph scenario {
            goal = "linear smoke"
            start [shape=Mdiamond]
            build [shape=box, prompt="build"]
            done [shape=Msquare]
            start -> build -> done
        }
        """;

        var backend = new DeterministicBackend()
            .On("build", _ => DeterministicBackend.Result(
                contextUpdates: new Dictionary<string, string> { ["build.complete"] = "true" },
                notes: "linear complete"));

        using var run = await ScenarioRunner.RunDotAsync(dot, backend);

        Assert.Equal(OutcomeStatus.Success, run.Result.Status);
        ScenarioAssert.NodesExecutedInOrder(run, "start", "build");
        ScenarioAssert.NodeStatus(run, "build", OutcomeStatus.Success);
        ScenarioAssert.ContextContains(run, "build.complete", "true");
    }

    [Fact]
    public async Task ScenarioHarness_CheckpointSaveAndResume_ResumesFromSavedNode()
    {
        const string dot = """
        digraph scenario {
            goal = "checkpoint"
            start [shape=Mdiamond]
            step_a [shape=box, prompt="step a"]
            step_b [shape=box, prompt="step b"]
            done [shape=Msquare]
            start -> step_a -> step_b -> done
        }
        """;

        using var firstRun = await ScenarioRunner.RunDotAsync(
            dot,
            new DeterministicBackend()
                .Queue("step_a", DeterministicBackend.Result(notes: "a"))
                .Queue("step_b", DeterministicBackend.Result(notes: "b")));

        Assert.True(File.Exists(Path.Combine(firstRun.LogsRoot, "checkpoint.json")));

        var checkpoint = new Checkpoint(
            CurrentNodeId: "step_b",
            CompletedNodes: new List<string> { "start", "step_a" },
            ContextData: new Dictionary<string, string> { ["goal"] = "checkpoint" },
            RetryCounts: new Dictionary<string, int>());

        using var resumedRun = await ScenarioRunner.RunDotAsync(
            dot,
            new DeterministicBackend().Queue("step_b", DeterministicBackend.Result(notes: "resumed")),
            checkpoint: checkpoint);

        Assert.Equal(OutcomeStatus.Success, resumedRun.Result.Status);
        ScenarioAssert.NodesExecutedInOrder(resumedRun, "step_b");
        ScenarioAssert.ContextContains(resumedRun, "pipeline.resume_mode", "resume");
    }

    [Fact]
    public async Task ScenarioHarness_FirstResumedNode_DegradesFidelity()
    {
        const string dot = """
        digraph scenario {
            goal = "resume fidelity"
            start [shape=Mdiamond]
            coder [shape=box, prompt="work"]
            done [shape=Msquare]
            start -> coder -> done
        }
        """;

        var checkpoint = new Checkpoint(
            CurrentNodeId: "coder",
            CompletedNodes: new List<string> { "start" },
            ContextData: new Dictionary<string, string>(),
            RetryCounts: new Dictionary<string, int>());

        var backend = new DeterministicBackend()
            .On("coder", invocation =>
            {
                Assert.Contains("Runtime fidelity: summary:high", invocation.Prompt, StringComparison.Ordinal);
                return DeterministicBackend.Result(notes: "resumed");
            });

        using var run = await ScenarioRunner.RunDotAsync(dot, backend, checkpoint: checkpoint);

        Assert.Equal("summary:high", run.Graph.Nodes["coder"].Fidelity);
        ScenarioAssert.NodesExecutedInOrder(run, "coder");
    }

    [Fact]
    public async Task ScenarioHarness_ConditionalRouting_UsesPreferredLabel()
    {
        const string dot = """
        digraph scenario {
            goal = "preferred label"
            start [shape=Mdiamond]
            router [shape=box, prompt="route"]
            approved [shape=box, prompt="approved"]
            rework [shape=box, prompt="rework"]
            done [shape=Msquare]
            start -> router
            router -> approved [label="ship"]
            router -> rework [label="rework"]
            approved -> done
            rework -> done
        }
        """;

        var backend = new DeterministicBackend()
            .On("router", _ => DeterministicBackend.Result(preferredNextLabel: "ship", notes: "ship it"))
            .On("approved", _ => DeterministicBackend.Result(notes: "approved"));

        using var run = await ScenarioRunner.RunDotAsync(dot, backend);

        ScenarioAssert.NodesExecutedInOrder(run, "start", "router", "approved");
    }

    [Fact]
    public async Task ScenarioHarness_ConditionalRouting_UsesSuggestedNextIds()
    {
        const string dot = """
        digraph scenario {
            goal = "suggested next ids"
            start [shape=Mdiamond]
            router [shape=box, prompt="route"]
            branch_a [shape=box, prompt="a"]
            branch_b [shape=box, prompt="b"]
            done [shape=Msquare]
            start -> router
            router -> branch_a
            router -> branch_b
            branch_a -> done
            branch_b -> done
        }
        """;

        var backend = new DeterministicBackend()
            .On("router", _ => DeterministicBackend.Result(
                suggestedNextIds: new[] { "branch_b" },
                notes: "pick b"))
            .On("branch_b", _ => DeterministicBackend.Result(notes: "branch b"));

        using var run = await ScenarioRunner.RunDotAsync(dot, backend);

        ScenarioAssert.NodesExecutedInOrder(run, "start", "router", "branch_b");
    }

    [Fact]
    public async Task ScenarioHarness_GoalGateRetryLoop_ReachesSuccess()
    {
        const string dot = """
        digraph scenario {
            goal = "goal gate retry loop"
            start [shape=Mdiamond]
            work [shape=box, prompt="work"]
            validate [shape=box, prompt="validate", goal_gate=true, retry_target="work"]
            done [shape=Msquare]
            start -> work -> validate -> done
        }
        """;

        var backend = new DeterministicBackend()
            .On("work", _ => DeterministicBackend.Result(
                contextUpdates: new Dictionary<string, string> { ["work.ran"] = "true" },
                notes: "work"))
            .Queue(
                "validate",
                DeterministicBackend.Result(OutcomeStatus.Fail, notes: "needs retry", failureReason: "not ready"),
                DeterministicBackend.Result(OutcomeStatus.Success, notes: "validated"));

        using var run = await ScenarioRunner.RunDotAsync(dot, backend);

        Assert.Equal(OutcomeStatus.Success, run.Result.Status);
        ScenarioAssert.NodesExecutedInOrder(run, "start", "work", "validate", "work", "validate");
        ScenarioAssert.NodeStatus(run, "validate", OutcomeStatus.Success);
    }

    [Fact]
    public async Task ScenarioHarness_ParallelFanOutAndFanIn_ContinuesDownstream()
    {
        const string dot = """
        digraph scenario {
            goal = "parallel fan out and fan in"
            start [shape=Mdiamond]
            parallel [shape=component]
            branch_a [shape=box, prompt="a"]
            branch_a2 [shape=box, prompt="a2"]
            branch_b [shape=box, prompt="b"]
            fan_in [shape=tripleoctagon]
            verify [shape=box, prompt="verify"]
            done [shape=Msquare]
            start -> parallel
            parallel -> branch_a
            parallel -> branch_b
            branch_a -> branch_a2 -> fan_in
            branch_b -> fan_in
            fan_in -> verify -> done
        }
        """;

        var backend = new DeterministicBackend()
            .Queue("branch_a", DeterministicBackend.Result(notes: "a"))
            .Queue("branch_a2", DeterministicBackend.Result(notes: "a2"))
            .Queue("branch_b", DeterministicBackend.Result(notes: "b"))
            .Queue("verify", DeterministicBackend.Result(notes: "verified"));

        using var run = await ScenarioRunner.RunDotAsync(dot, backend);

        Assert.Equal(OutcomeStatus.Success, run.Result.Status);
        ScenarioAssert.AppearsBefore(run, "fan_in", "verify");
        ScenarioAssert.NodeStatus(run, "verify", OutcomeStatus.Success);
        Assert.True(run.Result.FinalContext.Has("fan_in.ranked_results"));

        var invocationOrder = run.BackendInvocations.Select(invocation => invocation.NodeId).ToList();
        Assert.Contains("branch_a", invocationOrder);
        Assert.Contains("branch_a2", invocationOrder);
        Assert.Contains("branch_b", invocationOrder);
        Assert.Contains("verify", invocationOrder);
        Assert.True(invocationOrder.IndexOf("branch_a2") < invocationOrder.IndexOf("verify"));
        Assert.True(invocationOrder.IndexOf("branch_b") < invocationOrder.IndexOf("verify"));
    }
}
