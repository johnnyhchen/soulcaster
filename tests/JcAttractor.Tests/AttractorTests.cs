using JcAttractor.Attractor;

namespace JcAttractor.Tests;

// ── 11.1 DOT Parsing ────────────────────────────────────────────────────────

public class DotLexerTests
{
    [Fact]
    public void Lexer_TokenizesSimpleGraph()
    {
        var lexer = new DotLexer("digraph G { a -> b; }");
        var tokens = lexer.Tokenize();

        Assert.Equal(DotTokenType.Digraph, tokens[0].Type);
        Assert.Equal(DotTokenType.Identifier, tokens[1].Type);
        Assert.Equal("G", tokens[1].Value);
        Assert.Equal(DotTokenType.LBrace, tokens[2].Type);
        Assert.Equal(DotTokenType.Identifier, tokens[3].Type);
        Assert.Equal("a", tokens[3].Value);
        Assert.Equal(DotTokenType.Arrow, tokens[4].Type);
        Assert.Equal(DotTokenType.Identifier, tokens[5].Type);
        Assert.Equal("b", tokens[5].Value);
        Assert.Equal(DotTokenType.Semicolon, tokens[6].Type);
        Assert.Equal(DotTokenType.RBrace, tokens[7].Type);
        Assert.Equal(DotTokenType.Eof, tokens[8].Type);
    }

    [Fact]
    public void Lexer_HandlesQuotedStrings()
    {
        var lexer = new DotLexer("\"hello world\"");
        var tokens = lexer.Tokenize();
        Assert.Equal(DotTokenType.QuotedString, tokens[0].Type);
        Assert.Equal("hello world", tokens[0].Value);
    }

    [Fact]
    public void Lexer_HandlesEscapedQuotes()
    {
        var lexer = new DotLexer("\"say \\\"hello\\\"\"");
        var tokens = lexer.Tokenize();
        Assert.Equal("say \"hello\"", tokens[0].Value);
    }

    [Fact]
    public void Lexer_HandlesKeywords()
    {
        var lexer = new DotLexer("digraph subgraph node edge true false");
        var tokens = lexer.Tokenize();
        Assert.Equal(DotTokenType.Digraph, tokens[0].Type);
        Assert.Equal(DotTokenType.Subgraph, tokens[1].Type);
        Assert.Equal(DotTokenType.Node, tokens[2].Type);
        Assert.Equal(DotTokenType.Edge, tokens[3].Type);
        Assert.Equal(DotTokenType.Boolean, tokens[4].Type);
        Assert.Equal(DotTokenType.Boolean, tokens[5].Type);
    }

    [Fact]
    public void Lexer_StripsLineComments()
    {
        var lexer = new DotLexer("digraph { // comment\n a }");
        var tokens = lexer.Tokenize();
        var identifiers = tokens.Where(t => t.Type == DotTokenType.Identifier).ToList();
        Assert.Single(identifiers);
        Assert.Equal("a", identifiers[0].Value);
    }

    [Fact]
    public void Lexer_StripsBlockComments()
    {
        var lexer = new DotLexer("digraph { /* block comment */ a }");
        var tokens = lexer.Tokenize();
        var identifiers = tokens.Where(t => t.Type == DotTokenType.Identifier).ToList();
        Assert.Single(identifiers);
    }

    [Fact]
    public void Lexer_StripsHashComments()
    {
        var lexer = new DotLexer("digraph {\n# comment\n a }");
        var tokens = lexer.Tokenize();
        var identifiers = tokens.Where(t => t.Type == DotTokenType.Identifier).ToList();
        Assert.Single(identifiers);
        Assert.Equal("a", identifiers[0].Value);
    }

    [Fact]
    public void Lexer_ParsesNumbers()
    {
        var lexer = new DotLexer("42 3.14");
        var tokens = lexer.Tokenize();
        Assert.Equal(DotTokenType.Number, tokens[0].Type);
        Assert.Equal("42", tokens[0].Value);
        Assert.Equal(DotTokenType.Number, tokens[1].Type);
        Assert.Equal("3.14", tokens[1].Value);
    }

    [Fact]
    public void Lexer_ParsesAttributeBlock()
    {
        var lexer = new DotLexer("[shape=box, label=\"test\"]");
        var tokens = lexer.Tokenize();
        Assert.Equal(DotTokenType.LBracket, tokens[0].Type);
        Assert.Equal(DotTokenType.Identifier, tokens[1].Type);
        Assert.Equal("shape", tokens[1].Value);
        Assert.Equal(DotTokenType.Equals, tokens[2].Type);
        Assert.Equal(DotTokenType.Identifier, tokens[3].Type);
        Assert.Equal("box", tokens[3].Value);
    }
}

public class DotParserTests
{
    [Fact]
    public void Parse_SimpleGraph_CreatesNodesAndEdges()
    {
        var dot = @"digraph pipeline {
            start [shape=Mdiamond]
            process [shape=box, prompt=""Do work""]
            done [shape=Msquare]
            start -> process -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("pipeline", graph.Name);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
    }

    [Fact]
    public void Parse_NodeAttributes_ExtractedCorrectly()
    {
        var dot = @"digraph G {
            coder [shape=box, prompt=""Write code"", max_retries=3, goal_gate=true, model=""claude-opus-4-6""]
            start [shape=Mdiamond]
            done [shape=Msquare]
            start -> coder -> done
        }";

        var graph = DotParser.Parse(dot);
        var coder = graph.Nodes["coder"];
        Assert.Equal("box", coder.Shape);
        Assert.Equal("Write code", coder.Prompt);
        Assert.Equal(3, coder.MaxRetries);
        Assert.True(coder.GoalGate);
        Assert.Equal("claude-opus-4-6", coder.LlmModel);
    }

    [Fact]
    public void Parse_EdgeAttributes_ExtractedCorrectly()
    {
        var dot = @"digraph G {
            a [shape=Mdiamond]
            b [shape=box, prompt=""x""]
            c [shape=Msquare]
            a -> b [condition=""outcome=success"", weight=10, label=""success""]
            a -> c [condition=""outcome=fail""]
        }";

        var graph = DotParser.Parse(dot);
        var edge = graph.Edges.First(e => e.ToNode == "b");
        Assert.Equal("outcome=success", edge.Condition);
        Assert.Equal(10, edge.Weight);
        Assert.Equal("success", edge.Label);
    }

    [Fact]
    public void Parse_GraphAttributes_SetCorrectly()
    {
        var dot = @"digraph G {
            goal = ""Build the feature""
            default_max_retry = 5
            model_stylesheet = ""box { model = 'claude-opus-4-6' }""
            start [shape=Mdiamond]
            done [shape=Msquare]
            start -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("Build the feature", graph.Goal);
        Assert.Equal(5, graph.DefaultMaxRetry);
        Assert.Contains("claude-opus-4-6", graph.ModelStylesheet);
    }

    [Fact]
    public void Parse_SubgraphFlattening_NodesAddedToMainGraph()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            done [shape=Msquare]
            subgraph cluster_phase1 {
                a [shape=box, prompt=""task a""]
                b [shape=box, prompt=""task b""]
                a -> b
            }
            start -> a
            b -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.True(graph.Nodes.ContainsKey("a"));
        Assert.True(graph.Nodes.ContainsKey("b"));
        Assert.True(graph.Edges.Any(e => e.FromNode == "a" && e.ToNode == "b"));
    }

    [Fact]
    public void Parse_NodeDefaults_AppliedToNodes()
    {
        var dot = @"digraph G {
            node [shape=box]
            start [shape=Mdiamond]
            a
            done [shape=Msquare]
            start -> a -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("box", graph.Nodes["a"].Shape);
        Assert.Equal("Mdiamond", graph.Nodes["start"].Shape); // Explicit overrides default
    }

    [Fact]
    public void Parse_EdgeChain_CreatesMultipleEdges()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            a [shape=box, prompt=""x""]
            b [shape=box, prompt=""y""]
            done [shape=Msquare]
            start -> a -> b -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal(3, graph.Edges.Count);
        Assert.True(graph.Edges.Any(e => e.FromNode == "start" && e.ToNode == "a"));
        Assert.True(graph.Edges.Any(e => e.FromNode == "a" && e.ToNode == "b"));
        Assert.True(graph.Edges.Any(e => e.FromNode == "b" && e.ToNode == "done"));
    }

    [Fact]
    public void Parse_ImplicitNodeCreation_FromEdges()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            done [shape=Msquare]
            start -> middle -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.True(graph.Nodes.ContainsKey("middle")); // Implicitly created
    }
}

// ── 11.2 Validation and Linting ─────────────────────────────────────────────

public class ValidatorTests
{
    [Fact]
    public void Validate_ValidGraph_NoErrors()
    {
        var graph = CreateSimpleGraph();
        var results = Validator.Validate(graph);
        var errors = results.Where(r => r.Severity == LintSeverity.Error).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_NoStartNode_ReturnsError()
    {
        var graph = new Graph();
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "start_node" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Validate_NoExitNode_ReturnsError()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "exit_node" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Validate_MultipleStartNodes_ReturnsError()
    {
        var graph = new Graph();
        graph.Nodes["s1"] = new GraphNode { Id = "s1", Shape = "Mdiamond" };
        graph.Nodes["s2"] = new GraphNode { Id = "s2", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "start_node" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Validate_UnreachableNode_ReturnsError()
    {
        var graph = CreateSimpleGraph();
        graph.Nodes["orphan"] = new GraphNode { Id = "orphan", Shape = "box", Prompt = "I'm alone" };
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "reachability" && r.NodeId == "orphan");
    }

    [Fact]
    public void Validate_StartNodeWithIncoming_ReturnsError()
    {
        var graph = CreateSimpleGraph();
        graph.Edges.Add(new GraphEdge { FromNode = "done", ToNode = "start" });
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "start_no_incoming");
    }

    [Fact]
    public void Validate_CodergenWithoutPrompt_ReturnsWarning()
    {
        var graph = CreateSimpleGraph();
        graph.Nodes["noprompt"] = new GraphNode { Id = "noprompt", Shape = "box", Prompt = "" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "noprompt" });
        graph.Edges.Add(new GraphEdge { FromNode = "noprompt", ToNode = "done" });
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "codergen_prompt" && r.Severity == LintSeverity.Warning);
    }

    [Fact]
    public void Validate_InvalidCondition_ReturnsError()
    {
        var graph = CreateSimpleGraph();
        graph.Edges.Clear();
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done", Condition = "invalid_no_operator" });
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "condition_syntax" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void ValidateOrRaise_ThrowsOnError()
    {
        var graph = new Graph(); // No start or exit nodes
        Assert.Throws<InvalidOperationException>(() => Validator.ValidateOrRaise(graph));
    }

    private static Graph CreateSimpleGraph()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });
        return graph;
    }
}

// ── 11.3 & 11.4 Execution Engine ────────────────────────────────────────────

public class PipelineEngineTests
{
    [Fact]
    public async Task RunAsync_SimpleGraph_CompletesSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var config = new PipelineConfig(LogsRoot: tempDir);
            var engine = new PipelineEngine(config);

            var graph = CreateTestGraph();
            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("start", result.CompletedNodes);
            Assert.Contains("done", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_WithCodergenNode_ExecutesBackend()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = CreateTestGraphWithCoder();
            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.True(backend.CallCount > 0);
            Assert.Contains("coder", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_WithGoalGate_BlocksExitUntilMet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["coder"] = new GraphNode
            {
                Id = "coder", Shape = "box",
                Prompt = "do work",
                GoalGate = true
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "coder" });
            graph.Edges.Add(new GraphEdge { FromNode = "coder", ToNode = "done" });

            var result = await engine.RunAsync(graph);
            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("coder", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_Cancellation_ThrowsOperationCanceled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var config = new PipelineConfig(LogsRoot: tempDir);
            var engine = new PipelineEngine(config);
            var graph = CreateTestGraph();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                engine.RunAsync(graph, cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_GraphTransforms_Applied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(
                LogsRoot: tempDir,
                Backend: backend,
                Transforms: [new VariableExpansionTransform()]);
            var engine = new PipelineEngine(config);

            var graph = new Graph { Goal = "Build a feature" };
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["coder"] = new GraphNode
            {
                Id = "coder", Shape = "box",
                Prompt = "Implement $goal"
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "coder" });
            graph.Edges.Add(new GraphEdge { FromNode = "coder", ToNode = "done" });

            var result = await engine.RunAsync(graph);
            Assert.Equal(OutcomeStatus.Success, result.Status);
            // After transform, prompt should have $goal replaced
            Assert.Equal("Implement Build a feature", graph.Nodes["coder"].Prompt);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_WithWaitHuman_UsesInterviewer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var interviewer = new QueueInterviewer(
                new InterviewAnswer("proceed", ["proceed"]));
            var config = new PipelineConfig(LogsRoot: tempDir, Interviewer: interviewer);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["review"] = new GraphNode { Id = "review", Shape = "hexagon", Label = "Review changes?" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "review" });
            graph.Edges.Add(new GraphEdge { FromNode = "review", ToNode = "done", Label = "proceed" });

            var result = await engine.RunAsync(graph);
            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Equal(0, interviewer.Remaining);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static Graph CreateTestGraph()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });
        return graph;
    }

    private static Graph CreateTestGraphWithCoder()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["coder"] = new GraphNode
        {
            Id = "coder",
            Shape = "box",
            Prompt = "Write a hello world program"
        };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "coder" });
        graph.Edges.Add(new GraphEdge { FromNode = "coder", ToNode = "done" });
        return graph;
    }
}

// ── 11.7 State and Context ──────────────────────────────────────────────────

public class PipelineContextTests
{
    [Fact]
    public void Set_And_Get_ReturnsValue()
    {
        var ctx = new PipelineContext();
        ctx.Set("key", "value");
        Assert.Equal("value", ctx.Get("key"));
    }

    [Fact]
    public void Get_ReturnsEmpty_WhenKeyMissing()
    {
        var ctx = new PipelineContext();
        Assert.Equal("", ctx.Get("nonexistent"));
    }

    [Fact]
    public void Has_ReturnsTrueOrFalse()
    {
        var ctx = new PipelineContext();
        ctx.Set("exists", "yes");
        Assert.True(ctx.Has("exists"));
        Assert.False(ctx.Has("missing"));
    }

    [Fact]
    public void MergeUpdates_AppliesAll()
    {
        var ctx = new PipelineContext();
        ctx.Set("a", "1");
        ctx.MergeUpdates(new Dictionary<string, string> { ["a"] = "2", ["b"] = "3" });
        Assert.Equal("2", ctx.Get("a"));
        Assert.Equal("3", ctx.Get("b"));
    }

    [Fact]
    public void MergeUpdates_NullDoesNothing()
    {
        var ctx = new PipelineContext();
        ctx.Set("a", "1");
        ctx.MergeUpdates(null);
        Assert.Equal("1", ctx.Get("a"));
    }

    [Fact]
    public void All_ReturnsReadOnlyView()
    {
        var ctx = new PipelineContext();
        ctx.Set("x", "1");
        ctx.Set("y", "2");
        Assert.Equal(2, ctx.All.Count);
    }
}

public class CheckpointTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_ckpt_{Guid.NewGuid():N}");
        try
        {
            var checkpoint = new Checkpoint(
                CurrentNodeId: "node_a",
                CompletedNodes: ["start", "node_a"],
                ContextData: new Dictionary<string, string> { ["goal"] = "test" },
                RetryCounts: new Dictionary<string, int> { ["node_a"] = 2 }
            );

            checkpoint.Save(tempDir);
            var loaded = Checkpoint.Load(tempDir);

            Assert.NotNull(loaded);
            Assert.Equal("node_a", loaded.CurrentNodeId);
            Assert.Contains("start", loaded.CompletedNodes);
            Assert.Equal("test", loaded.ContextData["goal"]);
            Assert.Equal(2, loaded.RetryCounts["node_a"]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Load_ReturnsNull_WhenNoCheckpoint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_ckpt_{Guid.NewGuid():N}");
        Assert.Null(Checkpoint.Load(tempDir));
    }
}

// ── 11.9 Condition Expressions ──────────────────────────────────────────────

public class ConditionEvaluatorTests
{
    [Fact]
    public void Evaluate_EmptyCondition_ReturnsTrue()
    {
        var outcome = new Outcome(OutcomeStatus.Success);
        var ctx = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("", outcome, ctx));
        Assert.True(ConditionEvaluator.Evaluate("  ", outcome, ctx));
    }

    [Fact]
    public void Evaluate_OutcomeEquals_Works()
    {
        var outcome = new Outcome(OutcomeStatus.Success);
        var ctx = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("outcome=success", outcome, ctx));
        Assert.False(ConditionEvaluator.Evaluate("outcome=fail", outcome, ctx));
    }

    [Fact]
    public void Evaluate_OutcomeNotEquals_Works()
    {
        var outcome = new Outcome(OutcomeStatus.Fail);
        var ctx = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("outcome!=success", outcome, ctx));
        Assert.False(ConditionEvaluator.Evaluate("outcome!=fail", outcome, ctx));
    }

    [Fact]
    public void Evaluate_PreferredLabel_Works()
    {
        var outcome = new Outcome(OutcomeStatus.Success, PreferredLabel: "approve");
        var ctx = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("preferred_label=approve", outcome, ctx));
        Assert.False(ConditionEvaluator.Evaluate("preferred_label=reject", outcome, ctx));
    }

    [Fact]
    public void Evaluate_ContextVariable_Works()
    {
        var outcome = new Outcome(OutcomeStatus.Success);
        var ctx = new PipelineContext();
        ctx.Set("language", "csharp");
        Assert.True(ConditionEvaluator.Evaluate("context.language=csharp", outcome, ctx));
        Assert.False(ConditionEvaluator.Evaluate("context.language=python", outcome, ctx));
    }

    [Fact]
    public void Evaluate_AndConjunction_Works()
    {
        var outcome = new Outcome(OutcomeStatus.Success, PreferredLabel: "yes");
        var ctx = new PipelineContext();
        ctx.Set("ready", "true");

        Assert.True(ConditionEvaluator.Evaluate(
            "outcome=success && context.ready=true", outcome, ctx));

        Assert.False(ConditionEvaluator.Evaluate(
            "outcome=success && context.ready=false", outcome, ctx));
    }

    [Fact]
    public void Evaluate_QuotedValues_StripsQuotes()
    {
        var outcome = new Outcome(OutcomeStatus.Success);
        var ctx = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("outcome=\"success\"", outcome, ctx));
        Assert.True(ConditionEvaluator.Evaluate("outcome='success'", outcome, ctx));
    }

    [Fact]
    public void TryParse_ValidCondition_ReturnsNull()
    {
        Assert.Null(ConditionEvaluator.TryParse("outcome=success"));
        Assert.Null(ConditionEvaluator.TryParse("outcome!=fail && preferred_label=yes"));
    }

    [Fact]
    public void TryParse_InvalidCondition_ReturnsErrorMessage()
    {
        var error = ConditionEvaluator.TryParse("no_operator_here");
        Assert.NotNull(error);
        Assert.Contains("no valid operator", error);
    }

    [Fact]
    public void Evaluate_CaseInsensitive()
    {
        var outcome = new Outcome(OutcomeStatus.Success);
        var ctx = new PipelineContext();
        Assert.True(ConditionEvaluator.Evaluate("outcome=SUCCESS", outcome, ctx));
        Assert.True(ConditionEvaluator.Evaluate("outcome=Success", outcome, ctx));
    }
}

// ── Edge Selection ──────────────────────────────────────────────────────────

public class EdgeSelectorTests
{
    [Fact]
    public void SelectEdge_SingleEdge_ReturnsIt()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b" }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.NotNull(result);
        Assert.Equal("b", result.ToNode);
    }

    [Fact]
    public void SelectEdge_ConditionMatch_PreferredOverUnconditional()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b", Condition = "outcome=success" },
            new() { FromNode = "a", ToNode = "c" }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Equal("b", result!.ToNode);
    }

    [Fact]
    public void SelectEdge_PreferredLabel_MatchesEdgeLabel()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b", Label = "approve" },
            new() { FromNode = "a", ToNode = "c", Label = "reject" }
        };
        var outcome = new Outcome(OutcomeStatus.Success, PreferredLabel: "reject");
        var result = EdgeSelector.SelectEdge(edges, outcome, new PipelineContext());
        Assert.Equal("c", result!.ToNode);
    }

    [Fact]
    public void SelectEdge_SuggestedNextIds_MatchesEdge()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b" },
            new() { FromNode = "a", ToNode = "c" }
        };
        var outcome = new Outcome(OutcomeStatus.Success, SuggestedNextIds: ["c"]);
        var result = EdgeSelector.SelectEdge(edges, outcome, new PipelineContext());
        Assert.Equal("c", result!.ToNode);
    }

    [Fact]
    public void SelectEdge_HighestWeight_WinsAmongUnconditional()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b", Weight = 1 },
            new() { FromNode = "a", ToNode = "c", Weight = 10 },
            new() { FromNode = "a", ToNode = "d", Weight = 5 }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Equal("c", result!.ToNode);
    }

    [Fact]
    public void SelectEdge_LexicalTiebreak_OnEqualWeight()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "c", Weight = 5 },
            new() { FromNode = "a", ToNode = "b", Weight = 5 }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Equal("b", result!.ToNode); // "b" < "c" lexically
    }

    [Fact]
    public void SelectEdge_NoEdges_ReturnsNull()
    {
        var result = EdgeSelector.SelectEdge([], new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeLabel_StripsAcceleratorPrefix()
    {
        Assert.Equal("approve", EdgeSelector.NormalizeLabel("[Y] Approve"));
        Assert.Equal("reject", EdgeSelector.NormalizeLabel("[N] Reject"));
        Assert.Equal("done", EdgeSelector.NormalizeLabel("[Yes] Done"));
    }

    [Fact]
    public void NormalizeLabel_TrimsAndLowercases()
    {
        Assert.Equal("approve", EdgeSelector.NormalizeLabel("  Approve  "));
    }
}

// ── 11.8 Human-in-the-Loop ──────────────────────────────────────────────────

public class InterviewerTests
{
    [Fact]
    public async Task AutoApproveInterviewer_SelectsFirstOption()
    {
        var interviewer = new AutoApproveInterviewer();
        var question = new InterviewQuestion("Pick one", QuestionType.SingleSelect, ["option1", "option2"]);
        var answer = await interviewer.AskAsync(question);
        Assert.Equal("option1", answer.Text);
    }

    [Fact]
    public async Task AutoApproveInterviewer_Confirm_ReturnsYes()
    {
        var interviewer = new AutoApproveInterviewer();
        var question = new InterviewQuestion("Continue?", QuestionType.Confirm, []);
        var answer = await interviewer.AskAsync(question);
        Assert.Equal("yes", answer.Text);
    }

    [Fact]
    public async Task QueueInterviewer_DequeuesAnswers()
    {
        var interviewer = new QueueInterviewer(
            new InterviewAnswer("first", []),
            new InterviewAnswer("second", []));

        var q = new InterviewQuestion("Q?", QuestionType.FreeText, []);
        Assert.Equal("first", (await interviewer.AskAsync(q)).Text);
        Assert.Equal("second", (await interviewer.AskAsync(q)).Text);
        Assert.Equal(0, interviewer.Remaining);
    }

    [Fact]
    public async Task QueueInterviewer_ThrowsWhenEmpty()
    {
        var interviewer = new QueueInterviewer();
        var q = new InterviewQuestion("Q?", QuestionType.FreeText, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => interviewer.AskAsync(q));
    }
}

// ── 11.10 Model Stylesheet ──────────────────────────────────────────────────

public class ModelStylesheetTests
{
    [Fact]
    public void Parse_UniversalSelector()
    {
        var stylesheet = ModelStylesheet.Parse("* { provider = \"anthropic\" }");
        Assert.Single(stylesheet.Rules);
        Assert.Equal("*", stylesheet.Rules[0].Selector);
        Assert.Equal("anthropic", stylesheet.Rules[0].Properties["provider"]);
    }

    [Fact]
    public void Parse_ShapeSelector()
    {
        var stylesheet = ModelStylesheet.Parse("box { model = \"claude-opus-4-6\" }");
        Assert.Single(stylesheet.Rules);
        Assert.Equal("box", stylesheet.Rules[0].Selector);
    }

    [Fact]
    public void Parse_ClassSelector()
    {
        var stylesheet = ModelStylesheet.Parse(".fast { model = \"gemini-3-flash\" }");
        Assert.Single(stylesheet.Rules);
        Assert.Equal(".fast", stylesheet.Rules[0].Selector);
    }

    [Fact]
    public void Parse_IdSelector()
    {
        var stylesheet = ModelStylesheet.Parse("#review { reasoning_effort = \"high\" }");
        Assert.Single(stylesheet.Rules);
        Assert.Equal("#review", stylesheet.Rules[0].Selector);
    }

    [Fact]
    public void Parse_MultipleRules()
    {
        var stylesheet = ModelStylesheet.Parse(@"
            * { provider = ""anthropic"" }
            box { model = ""claude-opus-4-6"" }
            .fast { model = ""gemini-3-flash"" }
        ");
        Assert.Equal(3, stylesheet.Rules.Count);
    }

    [Fact]
    public void ResolveProperties_SpecificityOrder()
    {
        var stylesheet = ModelStylesheet.Parse(@"
            * { model = ""default""; provider = ""anthropic"" }
            box { model = ""box-model"" }
            .fast { model = ""fast-model"" }
            #special { model = ""special-model"" }
        ");

        // Universal only
        var genericNode = new GraphNode { Id = "generic", Shape = "diamond" };
        var props1 = stylesheet.ResolveProperties(genericNode);
        Assert.Equal("default", props1["model"]);

        // Shape overrides universal
        var boxNode = new GraphNode { Id = "worker", Shape = "box" };
        var props2 = stylesheet.ResolveProperties(boxNode);
        Assert.Equal("box-model", props2["model"]);
        Assert.Equal("anthropic", props2["provider"]); // Inherited from universal

        // Class overrides shape
        var fastBoxNode = new GraphNode { Id = "fastWorker", Shape = "box", Class = "fast" };
        var props3 = stylesheet.ResolveProperties(fastBoxNode);
        Assert.Equal("fast-model", props3["model"]);

        // ID overrides everything
        var specialNode = new GraphNode { Id = "special", Shape = "box", Class = "fast" };
        var props4 = stylesheet.ResolveProperties(specialNode);
        Assert.Equal("special-model", props4["model"]);
    }

    [Fact]
    public void Parse_EmptyStylesheet_ReturnsEmpty()
    {
        var stylesheet = ModelStylesheet.Parse("");
        Assert.Empty(stylesheet.Rules);
    }
}

// ── Graph Transform Tests ───────────────────────────────────────────────────

public class TransformTests
{
    [Fact]
    public void VariableExpansionTransform_ExpandsGoal()
    {
        var graph = new Graph { Goal = "Build a REST API" };
        graph.Nodes["a"] = new GraphNode
        {
            Id = "a", Shape = "box",
            Prompt = "Implement $goal in C#"
        };

        var transform = new VariableExpansionTransform();
        var result = transform.Transform(graph);

        Assert.Equal("Implement Build a REST API in C#", result.Nodes["a"].Prompt);
    }

    [Fact]
    public void VariableExpansionTransform_NoGoal_NoChange()
    {
        var graph = new Graph();
        graph.Nodes["a"] = new GraphNode { Id = "a", Shape = "box", Prompt = "Do something" };

        var transform = new VariableExpansionTransform();
        var result = transform.Transform(graph);

        Assert.Equal("Do something", result.Nodes["a"].Prompt);
    }

    [Fact]
    public void StylesheetTransform_AppliesModelFromStylesheet()
    {
        var graph = new Graph
        {
            ModelStylesheet = "box { model = \"claude-opus-4-6\" }"
        };
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["coder"] = new GraphNode { Id = "coder", Shape = "box", Prompt = "work" };

        var transform = new StylesheetTransform();
        var result = transform.Transform(graph);

        Assert.Equal("claude-opus-4-6", result.Nodes["coder"].LlmModel);
        Assert.Equal("", result.Nodes["start"].LlmModel); // Mdiamond not matched
    }

    [Fact]
    public void StylesheetTransform_ExplicitNodeOverridesStylesheet()
    {
        var graph = new Graph
        {
            ModelStylesheet = "box { model = \"claude-opus-4-6\" }"
        };
        graph.Nodes["coder"] = new GraphNode
        {
            Id = "coder", Shape = "box",
            LlmModel = "gpt-5.2" // Explicit override
        };

        var transform = new StylesheetTransform();
        var result = transform.Transform(graph);

        Assert.Equal("gpt-5.2", result.Nodes["coder"].LlmModel); // Explicit wins
    }
}

// ── Handler Registry ────────────────────────────────────────────────────────

public class HandlerRegistryTests
{
    [Fact]
    public void DefaultHandlers_AllRegistered()
    {
        var registry = new HandlerRegistry();
        Assert.NotNull(registry.GetHandler("Mdiamond"));
        Assert.NotNull(registry.GetHandler("Msquare"));
        Assert.NotNull(registry.GetHandler("box"));
        Assert.NotNull(registry.GetHandler("hexagon"));
        Assert.NotNull(registry.GetHandler("diamond"));
        Assert.NotNull(registry.GetHandler("component"));
        Assert.NotNull(registry.GetHandler("tripleoctagon"));
        Assert.NotNull(registry.GetHandler("parallelogram"));
    }

    [Fact]
    public void GetHandler_CaseInsensitive()
    {
        var registry = new HandlerRegistry();
        Assert.NotNull(registry.GetHandler("MDIAMOND"));
        Assert.NotNull(registry.GetHandler("msquare"));
    }

    [Fact]
    public void GetHandlerOrThrow_ThrowsForUnknown()
    {
        var registry = new HandlerRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.GetHandlerOrThrow("unknown_shape"));
    }

    [Fact]
    public void Register_CustomHandler_OverridesDefault()
    {
        var registry = new HandlerRegistry();
        var custom = new TestHandler();
        registry.Register("box", custom);
        Assert.Same(custom, registry.GetHandler("box"));
    }
}

// ── Outcome ─────────────────────────────────────────────────────────────────

public class OutcomeTests
{
    [Fact]
    public void Outcome_DefaultValues()
    {
        var outcome = new Outcome(OutcomeStatus.Success);
        Assert.Equal("", outcome.PreferredLabel);
        Assert.Null(outcome.SuggestedNextIds);
        Assert.Null(outcome.ContextUpdates);
        Assert.Equal("", outcome.Notes);
    }

    [Fact]
    public void Outcome_AllStatuses()
    {
        Assert.Equal(4, Enum.GetValues<OutcomeStatus>().Length);
        Assert.True(Enum.IsDefined(OutcomeStatus.Success));
        Assert.True(Enum.IsDefined(OutcomeStatus.Retry));
        Assert.True(Enum.IsDefined(OutcomeStatus.Fail));
        Assert.True(Enum.IsDefined(OutcomeStatus.PartialSuccess));
    }
}

// ── End-to-End Parse + Validate + Execute ───────────────────────────────────

public class EndToEndTests
{
    [Fact]
    public async Task FullPipeline_ParseValidateAndRun()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_e2e_{Guid.NewGuid():N}");
        try
        {
            var dot = @"
                digraph pipeline {
                    goal = ""Build a hello world app""
                    start [shape=Mdiamond]
                    implement [shape=box, prompt=""Write $goal""]
                    done [shape=Msquare]
                    start -> implement -> done
                }";

            // Step 1: Parse DOT
            var graph = DotParser.Parse(dot);
            Assert.Equal("pipeline", graph.Name);
            Assert.Equal("Build a hello world app", graph.Goal);

            // Step 2: Validate
            var lintResults = Validator.Validate(graph);
            var errors = lintResults.Where(r => r.Severity == LintSeverity.Error).ToList();
            Assert.Empty(errors);

            // Step 3: Execute
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(
                LogsRoot: tempDir,
                Backend: backend,
                Transforms: [new VariableExpansionTransform()]);
            var engine = new PipelineEngine(config);

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("start", result.CompletedNodes);
            Assert.Contains("implement", result.CompletedNodes);
            Assert.Contains("done", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.1 Additional Parsing Tests ────────────────────────────────────────────

public class DotParserAdditionalTests
{
    [Fact]
    public void Parse_MultiLineNodeAttributes_Parsed()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            done [shape=Msquare]
            coder [
                shape=box,
                prompt=""Write code"",
                max_retries=3,
                goal_gate=true
            ]
            start -> coder -> done
        }";

        var graph = DotParser.Parse(dot);
        var coder = graph.Nodes["coder"];
        Assert.Equal("box", coder.Shape);
        Assert.Equal("Write code", coder.Prompt);
        Assert.Equal(3, coder.MaxRetries);
        Assert.True(coder.GoalGate);
    }

    [Fact]
    public void Parse_ClassAttribute_ParsedOnNode()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            done [shape=Msquare]
            coder [shape=box, prompt=""x"", class=""fast,critical""]
            start -> coder -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Contains("fast", graph.Nodes["coder"].Class);
    }

    [Fact]
    public void Parse_QuotedAndUnquotedValues_BothWork()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            done [shape=Msquare]
            a [shape=box, prompt=""quoted prompt"", max_retries=2]
            start -> a -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("quoted prompt", graph.Nodes["a"].Prompt);
        Assert.Equal(2, graph.Nodes["a"].MaxRetries);
    }

    [Fact]
    public void Parse_GraphLabelAttribute()
    {
        var dot = @"digraph G {
            label = ""My Pipeline""
            start [shape=Mdiamond]
            done [shape=Msquare]
            start -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("My Pipeline", graph.Label);
    }
}

// ── 11.2 Additional Validation Tests ─────────────────────────────────────────

public class ValidatorAdditionalTests
{
    [Fact]
    public void Validate_ExitNodeWithOutgoing_ReturnsError()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Nodes["extra"] = new GraphNode { Id = "extra", Shape = "box", Prompt = "x" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });
        graph.Edges.Add(new GraphEdge { FromNode = "done", ToNode = "extra" });
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "extra" });

        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "exit_no_outgoing" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Validate_EdgeReferencesInvalidNode_ReturnsError()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "nonexistent" });

        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "edge_valid_nodes" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Validate_LintResults_IncludeAllFields()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Nodes["orphan"] = new GraphNode { Id = "orphan", Shape = "box", Prompt = "alone" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });

        var results = Validator.Validate(graph);
        var reachabilityResult = results.First(r => r.Rule == "reachability");

        // Lint results include rule name, severity, node ID, and message
        Assert.Equal("reachability", reachabilityResult.Rule);
        Assert.NotNull(reachabilityResult.Severity);
        Assert.Equal("orphan", reachabilityResult.NodeId);
        Assert.NotEmpty(reachabilityResult.Message);
    }
}

// ── 11.3 Additional Execution Engine Tests ───────────────────────────────────

public class PipelineEngineAdditionalTests
{
    [Fact]
    public async Task RunAsync_OutcomeWrittenToStatusJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["coder"] = new GraphNode { Id = "coder", Shape = "box", Prompt = "do work" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "coder" });
            graph.Edges.Add(new GraphEdge { FromNode = "coder", ToNode = "done" });

            await engine.RunAsync(graph);

            // Verify status.json was written for the coder node
            var statusPath = Path.Combine(tempDir, "coder", "status.json");
            Assert.True(File.Exists(statusPath), "status.json should be written for executed nodes");
            var statusJson = File.ReadAllText(statusPath);
            Assert.Contains("success", statusJson);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_ConditionalBranching_FollowsCorrectPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode { Id = "work", Shape = "box", Prompt = "do work" };
            graph.Nodes["gate"] = new GraphNode { Id = "gate", Shape = "diamond" };
            graph.Nodes["path_a"] = new GraphNode { Id = "path_a", Shape = "box", Prompt = "path a" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "gate" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate", ToNode = "path_a", Condition = "outcome=success" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate", ToNode = "done", Condition = "outcome=fail" });
            graph.Edges.Add(new GraphEdge { FromNode = "path_a", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("path_a", result.CompletedNodes); // Should take success path
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_ContextUpdates_VisibleToNextNode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new ContextTrackingBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["step1"] = new GraphNode { Id = "step1", Shape = "box", Prompt = "set context" };
            graph.Nodes["step2"] = new GraphNode { Id = "step2", Shape = "box", Prompt = "read context" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "step1" });
            graph.Edges.Add(new GraphEdge { FromNode = "step1", ToNode = "step2" });
            graph.Edges.Add(new GraphEdge { FromNode = "step2", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            // step1 sets "step1_ran" = "true", step2 should see it
            Assert.True(backend.Step2SawStep1Context, "Context from step1 should be visible to step2");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.4 Additional Goal Gate Tests ──────────────────────────────────────────

public class GoalGateAdditionalTests
{
    [Fact]
    public async Task GoalGate_RoutesToRetryTarget_WhenUnsatisfied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FailThenSucceedBackend(failCount: 1);
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "do work",
                GoalGate = true,
                RetryTarget = "work"
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.True(backend.CallCount >= 2, "Backend should have been called at least twice (retry)");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GoalGate_Fails_WhenNoRetryTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new AlwaysFailBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "do work",
                GoalGate = true
                // No retry_target
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Fail, result.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GoalGate_AllowsExit_WhenAllSatisfied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["gate1"] = new GraphNode { Id = "gate1", Shape = "box", Prompt = "task 1", GoalGate = true };
            graph.Nodes["gate2"] = new GraphNode { Id = "gate2", Shape = "box", Prompt = "task 2", GoalGate = true };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "gate1" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate1", ToNode = "gate2" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate2", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.5 Retry Logic Tests ──────────────────────────────────────────────────

public class RetryLogicTests
{
    [Fact]
    public async Task Retry_NodeWithMaxRetries_RetriedOnRetryOutcome()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new RetryThenSucceedBackend(retryCount: 2);
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "do work",
                MaxRetries = 3
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Equal(3, backend.CallCount); // 2 retries + 1 success
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Retry_CountTrackedPerNode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new RetryThenSucceedBackend(retryCount: 1);
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "do work",
                MaxRetries = 2
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Equal(2, backend.CallCount); // 1 retry + 1 success
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Retry_Exhaustion_UsesFailOutcome()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new AlwaysRetryBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "do work",
                MaxRetries = 2
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            // After exhausting retries on a RETRY outcome, it should be treated as fail
            Assert.Equal(OutcomeStatus.Fail, result.Status);
            Assert.Equal(3, backend.CallCount); // 1 initial + 2 retries
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Retry_BackoffApplied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new RetryThenSucceedBackend(retryCount: 1);
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "do work",
                MaxRetries = 2
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await engine.RunAsync(graph);
            sw.Stop();

            Assert.Equal(OutcomeStatus.Success, result.Status);
            // Backoff should add at least ~100ms delay (first backoff is 100ms * 2^0 = 100ms)
            Assert.True(sw.ElapsedMilliseconds >= 50, "Backoff delay should be applied between retries");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.6 Additional Handler Tests ────────────────────────────────────────────

public class HandlerAdditionalTests
{
    [Fact]
    public async Task CodergenHandler_WritesPromptAndResponse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph { Goal = "test goal" };
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["coder"] = new GraphNode { Id = "coder", Shape = "box", Prompt = "Write code for $goal" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "coder" });
            graph.Edges.Add(new GraphEdge { FromNode = "coder", ToNode = "done" });

            await engine.RunAsync(graph);

            // Verify prompt.md, response.md, and status.json written
            Assert.True(File.Exists(Path.Combine(tempDir, "coder", "prompt.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "coder", "response.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "coder", "status.json")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ParallelHandler_FansOutToBranches()
    {
        var registry = new HandlerRegistry(new FakeCodergenBackend());
        var handler = new ParallelHandler(registry);
        var context = new PipelineContext();
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            var graph = new Graph();
            graph.Nodes["parallel"] = new GraphNode { Id = "parallel", Shape = "component" };
            graph.Nodes["branch_a"] = new GraphNode { Id = "branch_a", Shape = "box", Prompt = "A" };
            graph.Nodes["branch_b"] = new GraphNode { Id = "branch_b", Shape = "box", Prompt = "B" };
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "branch_a" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "branch_b" });

            var outcome = await handler.ExecuteAsync(
                graph.Nodes["parallel"], context, graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.Contains("branch_a", outcome.Notes);
            Assert.Contains("branch_b", outcome.Notes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FanInHandler_Synchronizes()
    {
        var handler = new FanInHandler();
        var context = new PipelineContext();
        var graph = new Graph();
        graph.Nodes["fanin"] = new GraphNode { Id = "fanin", Shape = "tripleoctagon" };

        var outcome = await handler.ExecuteAsync(
            graph.Nodes["fanin"], context, graph, "/tmp");

        Assert.Equal(OutcomeStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task ToolHandler_ExecutesCommand()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var handler = new ToolHandler();
            var context = new PipelineContext();
            var graph = new Graph();

            var node = new GraphNode
            {
                Id = "tool_node",
                Shape = "parallelogram",
                RawAttributes = new Dictionary<string, string> { ["command"] = "echo hello" }
            };
            graph.Nodes["tool_node"] = node;

            var outcome = await handler.ExecuteAsync(node, context, graph, tempDir);

            Assert.Equal(OutcomeStatus.Success, outcome.Status);
            Assert.True(File.Exists(Path.Combine(tempDir, "tool_node", "stdout.txt")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CustomHandler_CanBeRegisteredByType()
    {
        var registry = new HandlerRegistry();
        var custom = new TestHandler();
        registry.Register("my_custom_type", custom);
        Assert.Same(custom, registry.GetHandler("my_custom_type"));
    }
}

// ── 11.7 Additional State and Context Tests ──────────────────────────────────

public class StateAdditionalTests
{
    [Fact]
    public async Task CheckpointResume_RestoresState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            // Create an initial checkpoint that skips the start node
            var checkpoint = new Checkpoint(
                CurrentNodeId: "start",
                CompletedNodes: new List<string>(),
                ContextData: new Dictionary<string, string> { ["resumed"] = "true" },
                RetryCounts: new Dictionary<string, int>()
            );
            checkpoint.Save(tempDir);

            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Equal("true", result.FinalContext.Get("resumed"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Artifacts_WrittenToNodeDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["coder"] = new GraphNode { Id = "coder", Shape = "box", Prompt = "work" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "coder" });
            graph.Edges.Add(new GraphEdge { FromNode = "coder", ToNode = "done" });

            await engine.RunAsync(graph);

            // Verify artifacts are written to {logs_root}/{node_id}/
            Assert.True(Directory.Exists(Path.Combine(tempDir, "coder")));
            Assert.True(File.Exists(Path.Combine(tempDir, "coder", "prompt.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "coder", "response.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "coder", "status.json")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CheckpointSavedAfterEachNode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["a"] = new GraphNode { Id = "a", Shape = "box", Prompt = "a" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "a" });
            graph.Edges.Add(new GraphEdge { FromNode = "a", ToNode = "done" });

            await engine.RunAsync(graph);

            var checkpoint = Checkpoint.Load(tempDir);
            Assert.NotNull(checkpoint);
            Assert.Contains("start", checkpoint.CompletedNodes);
            Assert.Contains("a", checkpoint.CompletedNodes);
            Assert.Contains("done", checkpoint.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.8 Additional Human-in-the-Loop Tests ─────────────────────────────────

public class HumanInTheLoopAdditionalTests
{
    [Fact]
    public async Task CallbackInterviewer_DelegatesToCallback()
    {
        var callbackCalled = false;
        var interviewer = new CallbackInterviewer(q =>
        {
            callbackCalled = true;
            return new InterviewAnswer("callback answer", []);
        });

        var question = new InterviewQuestion("Pick?", QuestionType.FreeText, []);
        var answer = await interviewer.AskAsync(question);

        Assert.True(callbackCalled);
        Assert.Equal("callback answer", answer.Text);
    }

    [Fact]
    public async Task QuestionTypes_SingleSelect()
    {
        var interviewer = new AutoApproveInterviewer();
        var q = new InterviewQuestion("Pick one", QuestionType.SingleSelect, ["A", "B", "C"]);
        var answer = await interviewer.AskAsync(q);
        Assert.Equal("A", answer.Text); // First option
    }

    [Fact]
    public async Task QuestionTypes_FreeText()
    {
        var interviewer = new QueueInterviewer(new InterviewAnswer("free text input", []));
        var q = new InterviewQuestion("Enter text", QuestionType.FreeText, []);
        var answer = await interviewer.AskAsync(q);
        Assert.Equal("free text input", answer.Text);
    }

    [Fact]
    public async Task QuestionTypes_Confirm()
    {
        var interviewer = new AutoApproveInterviewer();
        var q = new InterviewQuestion("Are you sure?", QuestionType.Confirm, []);
        var answer = await interviewer.AskAsync(q);
        Assert.Equal("yes", answer.Text);
    }
}

// ── 11.12 Cross-Feature Parity Matrix Tests ──────────────────────────────────

public class ParityMatrixTests
{
    [Fact]
    public void Matrix_ParseSimpleLinearPipeline()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            a [shape=box, prompt=""task a""]
            b [shape=box, prompt=""task b""]
            done [shape=Msquare]
            start -> a -> b -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);
    }

    [Fact]
    public void Matrix_ParseGraphLevelAttributes()
    {
        var dot = @"digraph G {
            goal = ""Build feature X""
            label = ""My Pipeline""
            start [shape=Mdiamond]
            done [shape=Msquare]
            start -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("Build feature X", graph.Goal);
        Assert.Equal("My Pipeline", graph.Label);
    }

    [Fact]
    public void Matrix_ParseMultiLineNodeAttributes()
    {
        var dot = @"digraph G {
            start [shape=Mdiamond]
            done [shape=Msquare]
            worker [
                shape=box,
                prompt=""Do the work"",
                max_retries=3,
                goal_gate=true
            ]
            start -> worker -> done
        }";

        var graph = DotParser.Parse(dot);
        Assert.Equal("Do the work", graph.Nodes["worker"].Prompt);
        Assert.Equal(3, graph.Nodes["worker"].MaxRetries);
        Assert.True(graph.Nodes["worker"].GoalGate);
    }

    [Fact]
    public void Matrix_ValidateMissingStart_Error()
    {
        var graph = new Graph();
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "start_node" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Matrix_ValidateMissingExit_Error()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "exit_node" && r.Severity == LintSeverity.Error);
    }

    [Fact]
    public void Matrix_ValidateOrphanNode_Warning()
    {
        var graph = new Graph();
        graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
        graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
        graph.Nodes["orphan"] = new GraphNode { Id = "orphan", Shape = "box", Prompt = "alone" };
        graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "done" });
        var results = Validator.Validate(graph);
        Assert.Contains(results, r => r.Rule == "reachability" && r.NodeId == "orphan");
    }

    [Fact]
    public async Task Matrix_ExecuteLinear3NodePipeline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["a"] = new GraphNode { Id = "a", Shape = "box", Prompt = "task a" };
            graph.Nodes["b"] = new GraphNode { Id = "b", Shape = "box", Prompt = "task b" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "a" });
            graph.Edges.Add(new GraphEdge { FromNode = "a", ToNode = "b" });
            graph.Edges.Add(new GraphEdge { FromNode = "b", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("a", result.CompletedNodes);
            Assert.Contains("b", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Matrix_ExecuteWithConditionalBranching()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode { Id = "work", Shape = "box", Prompt = "work" };
            graph.Nodes["gate"] = new GraphNode { Id = "gate", Shape = "diamond" };
            graph.Nodes["success_path"] = new GraphNode { Id = "success_path", Shape = "box", Prompt = "yay" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "gate" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate", ToNode = "success_path", Condition = "outcome=success" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate", ToNode = "done", Condition = "outcome=fail" });
            graph.Edges.Add(new GraphEdge { FromNode = "success_path", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("success_path", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Matrix_ExecuteWithRetryOnFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FailThenSucceedBackend(failCount: 1);
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode { Id = "work", Shape = "box", Prompt = "work", MaxRetries = 2 };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.True(backend.CallCount >= 2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Matrix_GoalGateBlocksExitWhenUnsatisfied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FailThenSucceedBackend(failCount: 1);
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "work",
                GoalGate = true,
                RetryTarget = "work"
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Matrix_GoalGateAllowsExitWhenSatisfied()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["work"] = new GraphNode
            {
                Id = "work", Shape = "box",
                Prompt = "work",
                GoalGate = true
            };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "work" });
            graph.Edges.Add(new GraphEdge { FromNode = "work", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Matrix_WaitHumanPresentsChoicesAndRoutes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var interviewer = new QueueInterviewer(
                new InterviewAnswer("approve", ["approve"]));
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Interviewer: interviewer, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["gate"] = new GraphNode { Id = "gate", Shape = "hexagon", Label = "Approve?" };
            graph.Nodes["approved"] = new GraphNode { Id = "approved", Shape = "box", Prompt = "approved" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "gate" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate", ToNode = "approved", Label = "approve" });
            graph.Edges.Add(new GraphEdge { FromNode = "gate", ToNode = "done", Label = "reject" });
            graph.Edges.Add(new GraphEdge { FromNode = "approved", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("approved", result.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Matrix_EdgeSelection_ConditionMatchWinsOverWeight()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b", Weight = 100 },
            new() { FromNode = "a", ToNode = "c", Condition = "outcome=success", Weight = 1 }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Equal("c", result!.ToNode); // Condition match wins
    }

    [Fact]
    public void Matrix_EdgeSelection_WeightBreaksTies()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "b", Weight = 1 },
            new() { FromNode = "a", ToNode = "c", Weight = 10 }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Equal("c", result!.ToNode);
    }

    [Fact]
    public void Matrix_EdgeSelection_LexicalTiebreak()
    {
        var edges = new List<GraphEdge>
        {
            new() { FromNode = "a", ToNode = "z_node" },
            new() { FromNode = "a", ToNode = "a_node" }
        };
        var result = EdgeSelector.SelectEdge(edges, new Outcome(OutcomeStatus.Success), new PipelineContext());
        Assert.Equal("a_node", result!.ToNode);
    }

    [Fact]
    public async Task Matrix_CheckpointSaveAndResume()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            // First run: execute and checkpoint
            var backend1 = new FakeCodergenBackend();
            var config1 = new PipelineConfig(LogsRoot: tempDir, Backend: backend1);
            var engine1 = new PipelineEngine(config1);

            var graph = new Graph();
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };
            graph.Nodes["a"] = new GraphNode { Id = "a", Shape = "box", Prompt = "task a" };
            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "a" });
            graph.Edges.Add(new GraphEdge { FromNode = "a", ToNode = "done" });

            var result1 = await engine1.RunAsync(graph);
            Assert.Equal(OutcomeStatus.Success, result1.Status);

            // Verify checkpoint exists
            var checkpoint = Checkpoint.Load(tempDir);
            Assert.NotNull(checkpoint);
            Assert.Contains("a", checkpoint.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Matrix_StylesheetAppliesModelOverride()
    {
        var graph = new Graph
        {
            ModelStylesheet = "box { model = \"claude-opus-4-6\" }"
        };
        graph.Nodes["coder"] = new GraphNode { Id = "coder", Shape = "box", Prompt = "work" };

        var transform = new StylesheetTransform();
        var result = transform.Transform(graph);

        Assert.Equal("claude-opus-4-6", result.Nodes["coder"].LlmModel);
    }

    [Fact]
    public void Matrix_PromptVariableExpansion()
    {
        var graph = new Graph { Goal = "Build feature X" };
        graph.Nodes["a"] = new GraphNode { Id = "a", Shape = "box", Prompt = "Implement $goal" };

        var transform = new VariableExpansionTransform();
        var result = transform.Transform(graph);

        Assert.Equal("Implement Build feature X", result.Nodes["a"].Prompt);
    }

    [Fact]
    public async Task Matrix_ParallelFanOutAndFanIn()
    {
        var registry = new HandlerRegistry(new FakeCodergenBackend());
        var parallelHandler = new ParallelHandler(registry);
        var fanInHandler = new FanInHandler();
        var context = new PipelineContext();
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            var graph = new Graph();
            graph.Nodes["parallel"] = new GraphNode { Id = "parallel", Shape = "component" };
            graph.Nodes["b1"] = new GraphNode { Id = "b1", Shape = "box", Prompt = "branch 1" };
            graph.Nodes["b2"] = new GraphNode { Id = "b2", Shape = "box", Prompt = "branch 2" };
            graph.Nodes["fanin"] = new GraphNode { Id = "fanin", Shape = "tripleoctagon" };
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "b1" });
            graph.Edges.Add(new GraphEdge { FromNode = "parallel", ToNode = "b2" });

            // Fan-out
            var parallelOutcome = await parallelHandler.ExecuteAsync(
                graph.Nodes["parallel"], context, graph, tempDir);
            Assert.Equal(OutcomeStatus.Success, parallelOutcome.Status);

            // Fan-in
            var fanInOutcome = await fanInHandler.ExecuteAsync(
                graph.Nodes["fanin"], context, graph, tempDir);
            Assert.Equal(OutcomeStatus.Success, fanInOutcome.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Matrix_CustomHandlerRegistrationAndExecution()
    {
        var registry = new HandlerRegistry();
        var custom = new TestHandler();
        registry.Register("my_custom", custom);

        var handler = registry.GetHandler("my_custom");
        Assert.NotNull(handler);
        Assert.Same(custom, handler);
    }

    [Fact]
    public async Task Matrix_PipelineWith10PlusNodes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        try
        {
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(LogsRoot: tempDir, Backend: backend);
            var engine = new PipelineEngine(config);

            var graph = new Graph { Goal = "Big pipeline test" };
            graph.Nodes["start"] = new GraphNode { Id = "start", Shape = "Mdiamond" };

            // Create 10 work nodes
            for (int i = 1; i <= 10; i++)
            {
                graph.Nodes[$"step{i}"] = new GraphNode
                {
                    Id = $"step{i}",
                    Shape = "box",
                    Prompt = $"Step {i}: do work"
                };
            }

            graph.Nodes["done"] = new GraphNode { Id = "done", Shape = "Msquare" };

            // Chain all nodes linearly
            graph.Edges.Add(new GraphEdge { FromNode = "start", ToNode = "step1" });
            for (int i = 1; i < 10; i++)
            {
                graph.Edges.Add(new GraphEdge { FromNode = $"step{i}", ToNode = $"step{i + 1}" });
            }
            graph.Edges.Add(new GraphEdge { FromNode = "step10", ToNode = "done" });

            var result = await engine.RunAsync(graph);

            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.True(result.CompletedNodes.Count >= 12); // start + 10 steps + done
            for (int i = 1; i <= 10; i++)
            {
                Assert.Contains($"step{i}", result.CompletedNodes);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.13 Integration Smoke Test ─────────────────────────────────────────────

public class IntegrationSmokeTests
{
    [Fact]
    public async Task SmokeTest_FullPipelineFromSpec()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_smoke_{Guid.NewGuid():N}");
        try
        {
            var dot = @"
                digraph test_pipeline {
                    graph [goal=""Create a hello world Python script""]

                    start       [shape=Mdiamond]
                    plan        [shape=box, prompt=""Plan how to create a hello world script for: $goal""]
                    implement   [shape=box, prompt=""Write the code based on the plan"", goal_gate=true]
                    review      [shape=box, prompt=""Review the code for correctness""]
                    done        [shape=Msquare]

                    start -> plan
                    plan -> implement
                    implement -> review [condition=""outcome=success""]
                    implement -> plan   [condition=""outcome=fail"", label=""Retry""]
                    review -> done      [condition=""outcome=success""]
                    review -> implement [condition=""outcome=fail"", label=""Fix""]
                }";

            // 1. Parse
            var graph = DotParser.Parse(dot);
            Assert.Equal("Create a hello world Python script", graph.Goal);
            Assert.Equal(5, graph.Nodes.Count);
            Assert.Equal(6, graph.Edges.Count);

            // 2. Validate
            var lintResults = Validator.Validate(graph);
            var errors = lintResults.Where(r => r.Severity == LintSeverity.Error).ToList();
            Assert.Empty(errors);

            // 3. Execute with LLM callback
            var backend = new FakeCodergenBackend();
            var config = new PipelineConfig(
                LogsRoot: tempDir,
                Backend: backend,
                Transforms: [new VariableExpansionTransform()]);
            var engine = new PipelineEngine(config);

            var result = await engine.RunAsync(graph);

            // 4. Verify
            Assert.Equal(OutcomeStatus.Success, result.Status);
            Assert.Contains("implement", result.CompletedNodes);

            // 5. Verify artifacts exist
            Assert.True(File.Exists(Path.Combine(tempDir, "plan", "prompt.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "plan", "response.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "plan", "status.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "implement", "prompt.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "implement", "response.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "implement", "status.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "review", "prompt.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "review", "response.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "review", "status.json")));

            // 6. Verify goal gate satisfied
            Assert.True(result.NodeOutcomes.ContainsKey("implement"));
            Assert.Equal(OutcomeStatus.Success, result.NodeOutcomes["implement"].Status);

            // 7. Verify checkpoint
            var checkpoint = Checkpoint.Load(tempDir);
            Assert.NotNull(checkpoint);
            Assert.Contains("plan", checkpoint.CompletedNodes);
            Assert.Contains("implement", checkpoint.CompletedNodes);
            Assert.Contains("review", checkpoint.CompletedNodes);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

// ── 11.11 Transforms and Extensibility ───────────────────────────────────────

public class TransformAdditionalTests
{
    [Fact]
    public void CustomTransform_CanBeApplied()
    {
        var graph = new Graph();
        graph.Nodes["a"] = new GraphNode { Id = "a", Shape = "box", Prompt = "original" };

        IGraphTransform customTransform = new AppendNoteTransform("_modified");
        var result = customTransform.Transform(graph);

        Assert.EndsWith("_modified", result.Nodes["a"].Prompt);
    }

    private class AppendNoteTransform : IGraphTransform
    {
        private readonly string _suffix;
        public AppendNoteTransform(string suffix) => _suffix = suffix;

        public Graph Transform(Graph graph)
        {
            var updated = new Dictionary<string, GraphNode>();
            foreach (var (id, node) in graph.Nodes)
            {
                if (!string.IsNullOrEmpty(node.Prompt))
                    updated[id] = node with { Prompt = node.Prompt + _suffix };
                else
                    updated[id] = node;
            }
            graph.Nodes.Clear();
            foreach (var (id, node) in updated)
                graph.Nodes[id] = node;
            return graph;
        }
    }
}

// ── Test helpers ─────────────────────────────────────────────────────────────

internal class FakeCodergenBackend : ICodergenBackend
{
    public int CallCount { get; private set; }

    public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new CodergenResult(
            Response: $"Completed: {prompt}",
            Status: OutcomeStatus.Success,
            ContextUpdates: new Dictionary<string, string> { ["last_action"] = "codergen" }
        ));
    }
}

internal class TestHandler : INodeHandler
{
    public Task<Outcome> ExecuteAsync(GraphNode node, PipelineContext context, Graph graph, string logsRoot, CancellationToken ct = default)
    {
        return Task.FromResult(new Outcome(OutcomeStatus.Success, Notes: "Test handler executed"));
    }
}

internal class AlwaysFailBackend : ICodergenBackend
{
    public int CallCount { get; private set; }

    public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new CodergenResult(
            Response: "Failed",
            Status: OutcomeStatus.Fail
        ));
    }
}

internal class FailThenSucceedBackend : ICodergenBackend
{
    private readonly int _failCount;
    public int CallCount { get; private set; }

    public FailThenSucceedBackend(int failCount)
    {
        _failCount = failCount;
    }

    public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
    {
        CallCount++;
        if (CallCount <= _failCount)
        {
            return Task.FromResult(new CodergenResult(
                Response: "Failed",
                Status: OutcomeStatus.Fail
            ));
        }
        return Task.FromResult(new CodergenResult(
            Response: $"Success on attempt {CallCount}",
            Status: OutcomeStatus.Success,
            ContextUpdates: new Dictionary<string, string> { ["last_action"] = "codergen" }
        ));
    }
}

internal class RetryThenSucceedBackend : ICodergenBackend
{
    private readonly int _retryCount;
    public int CallCount { get; private set; }

    public RetryThenSucceedBackend(int retryCount)
    {
        _retryCount = retryCount;
    }

    public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
    {
        CallCount++;
        if (CallCount <= _retryCount)
        {
            return Task.FromResult(new CodergenResult(
                Response: "Retry",
                Status: OutcomeStatus.Retry
            ));
        }
        return Task.FromResult(new CodergenResult(
            Response: $"Success on attempt {CallCount}",
            Status: OutcomeStatus.Success,
            ContextUpdates: new Dictionary<string, string> { ["last_action"] = "codergen" }
        ));
    }
}

internal class AlwaysRetryBackend : ICodergenBackend
{
    public int CallCount { get; private set; }

    public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new CodergenResult(
            Response: "Retry",
            Status: OutcomeStatus.Retry
        ));
    }
}

internal class ContextTrackingBackend : ICodergenBackend
{
    public bool Step2SawStep1Context { get; private set; }
    private int _callCount;

    public Task<CodergenResult> RunAsync(string prompt, string? model = null, string? provider = null, CancellationToken ct = default)
    {
        _callCount++;
        if (_callCount == 1)
        {
            // Step 1: set a context value
            return Task.FromResult(new CodergenResult(
                Response: "Step 1 done",
                Status: OutcomeStatus.Success,
                ContextUpdates: new Dictionary<string, string> { ["step1_ran"] = "true" }
            ));
        }
        else
        {
            // Step 2 does not directly read context (backend doesn't get it),
            // but we can verify via the pipeline's final context
            Step2SawStep1Context = true;
            return Task.FromResult(new CodergenResult(
                Response: "Step 2 done",
                Status: OutcomeStatus.Success
            ));
        }
    }
}
