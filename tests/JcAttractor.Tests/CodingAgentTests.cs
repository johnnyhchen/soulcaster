using JcAttractor.CodingAgent;
using JcAttractor.UnifiedLlm;

namespace JcAttractor.Tests;

// ── 9.1 Core Loop ────────────────────────────────────────────────────────────

public class SessionTests
{
    [Fact]
    public void Session_StartsInIdleState()
    {
        var session = CreateSession();
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public void Session_HasUniqueId()
    {
        var s1 = CreateSession();
        var s2 = CreateSession();
        Assert.NotEqual(s1.Id, s2.Id);
    }

    [Fact]
    public async Task Session_ProcessInputAsync_ReturnsAssistantTurn()
    {
        var session = CreateSession();
        var result = await session.ProcessInputAsync("hello");
        Assert.IsType<AssistantTurn>(result);
        Assert.NotEmpty(result.Content);
    }

    [Fact]
    public async Task Session_ProcessInputAsync_AddsToHistory()
    {
        var session = CreateSession();
        await session.ProcessInputAsync("hello");
        Assert.True(session.History.Count >= 2); // UserTurn + AssistantTurn
        Assert.IsType<UserTurn>(session.History[0]);
    }

    [Fact]
    public async Task Session_ReturnsToIdle_AfterProcessing()
    {
        var session = CreateSession();
        await session.ProcessInputAsync("hello");
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Session_ThrowsOnClosed()
    {
        // Can't directly close, but verify the contract
        var session = CreateSession();
        await session.ProcessInputAsync("test");
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Session_MaxTurns_StopsWhenLimitReached()
    {
        // Provider that always returns tool calls
        var loopProvider = new LoopingProvider(maxLoops: 100);
        var profile = new FakeProfile(loopProvider);
        var session = new Session(
            loopProvider,
            profile,
            new FakeExecutionEnvironment(),
            new SessionConfig(MaxTurns: 2));

        var result = await session.ProcessInputAsync("hello");
        Assert.Contains("Turn limit reached", result.Content);
    }

    [Fact]
    public async Task Session_MaxToolRounds_StopsWhenLimitReached()
    {
        var loopProvider = new LoopingProvider(maxLoops: 100);
        var profile = new FakeProfile(loopProvider);
        var session = new Session(
            loopProvider,
            profile,
            new FakeExecutionEnvironment(),
            new SessionConfig(MaxToolRoundsPerInput: 2));

        var result = await session.ProcessInputAsync("hello");
        Assert.Contains("limit reached", result.Content);
    }

    [Fact]
    public async Task Session_Cancellation_ThrowsOperationCanceled()
    {
        var slowProvider = new SlowProvider();
        var profile = new FakeProfile(slowProvider);
        var session = new Session(
            slowProvider,
            profile,
            new FakeExecutionEnvironment());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            session.ProcessInputAsync("hello", cts.Token));
    }

    [Fact]
    public async Task Session_Steering_InjectsMessage()
    {
        var session = CreateSession();
        session.Steer("Please use TypeScript");
        await session.ProcessInputAsync("hello");

        var steeringTurns = session.History.OfType<SteeringTurn>().ToList();
        Assert.Single(steeringTurns);
        Assert.Equal("Please use TypeScript", steeringTurns[0].Content);
    }

    private static Session CreateSession()
    {
        var fakeProvider = new FakeProvider("test");
        var fakeProfile = new FakeProfile(fakeProvider);
        return new Session(
            fakeProvider,
            fakeProfile,
            new FakeExecutionEnvironment());
    }
}

// ── 9.2 Provider Profiles ────────────────────────────────────────────────────

public class ProviderProfileTests
{
    [Fact]
    public void AnthropicProfile_RegistersAllTools()
    {
        var profile = new AnthropicProfile();
        var tools = profile.Tools();
        Assert.True(tools.Count >= 6);
        var toolNames = tools.Select(t => t.Name).ToList();
        Assert.Contains("read_file", toolNames);
        Assert.Contains("edit_file", toolNames);
        Assert.Contains("write_file", toolNames);
        Assert.Contains("bash", toolNames);
        Assert.Contains("glob", toolNames);
        Assert.Contains("grep", toolNames);
    }

    [Fact]
    public void AnthropicProfile_HasCorrectId()
    {
        var profile = new AnthropicProfile();
        Assert.Equal("anthropic", profile.Id);
    }

    [Fact]
    public void AnthropicProfile_SupportsReasoning()
    {
        var profile = new AnthropicProfile();
        Assert.True(profile.SupportsReasoning);
        Assert.True(profile.SupportsStreaming);
    }

    [Fact]
    public void AnthropicProfile_BuildSystemPrompt_ContainsEnvironmentInfo()
    {
        var profile = new AnthropicProfile();
        var env = new FakeExecutionEnvironment();
        var prompt = profile.BuildSystemPrompt(env);
        Assert.Contains("Working directory:", prompt);
        Assert.Contains("/fake/workdir", prompt);
    }

    [Fact]
    public void OpenAiProfile_HasCorrectId()
    {
        var profile = new OpenAiProfile();
        Assert.Equal("openai", profile.Id);
    }

    [Fact]
    public void OpenAiProfile_RegistersApplyPatchTool()
    {
        var profile = new OpenAiProfile();
        var tools = profile.Tools();
        var toolNames = tools.Select(t => t.Name).ToList();
        Assert.Contains("apply_patch", toolNames);
    }

    [Fact]
    public void GeminiProfile_HasCorrectId()
    {
        var profile = new GeminiProfile();
        Assert.Equal("gemini", profile.Id);
    }
}

// ── 9.4 Tool Execution ──────────────────────────────────────────────────────

public class ToolRegistryTests
{
    [Fact]
    public void ToolRegistry_Register_And_Get()
    {
        var registry = new ToolRegistry();
        var tool = new RegisteredTool(
            "test_tool",
            new ToolDefinition("test_tool", "A test tool", []),
            (args, env) => Task.FromResult("result"));
        registry.Register(tool);

        var retrieved = registry.Get("test_tool");
        Assert.NotNull(retrieved);
        Assert.Equal("test_tool", retrieved.Name);
    }

    [Fact]
    public void ToolRegistry_Get_ReturnsNull_WhenNotRegistered()
    {
        var registry = new ToolRegistry();
        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void ToolRegistry_GetDefinitions_ReturnsAllToolDefs()
    {
        var registry = new ToolRegistry();
        registry.Register(new RegisteredTool("a", new ToolDefinition("a", "desc", []), (_, _) => Task.FromResult("")));
        registry.Register(new RegisteredTool("b", new ToolDefinition("b", "desc", []), (_, _) => Task.FromResult("")));

        var defs = registry.GetDefinitions();
        Assert.Equal(2, defs.Count);
    }
}

// ── 9.5 Output Truncation ───────────────────────────────────────────────────

public class OutputTruncationTests
{
    [Fact]
    public void Truncate_ShortOutput_ReturnsUnchanged()
    {
        var result = OutputTruncation.Truncate("short output", "read_file");
        Assert.Equal("short output", result);
    }

    [Fact]
    public void Truncate_LongOutput_InsertsMarker()
    {
        var longOutput = new string('x', 60000);
        var result = OutputTruncation.Truncate(longOutput, "read_file");
        Assert.Contains("truncated", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Length < longOutput.Length);
    }

    [Fact]
    public void Truncate_EmptyOutput_ReturnsEmpty()
    {
        var result = OutputTruncation.Truncate("", "bash");
        Assert.Equal("", result);
    }

    [Fact]
    public void Truncate_CustomLimits_Override()
    {
        var customLimits = new Dictionary<string, int> { ["custom_tool"] = 10 };
        var output = new string('x', 100);
        var result = OutputTruncation.Truncate(output, "custom_tool", customLimits);
        Assert.Contains("truncated", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncate_LineBasedTruncation_ForBash()
    {
        var lines = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"line {i}"));
        var result = OutputTruncation.Truncate(lines, "bash");
        Assert.Contains("truncated", result, StringComparison.OrdinalIgnoreCase);
    }
}

// ── 9.6 Loop Detection ─────────────────────────────────────────────────────

public class LoopDetectionTests
{
    [Fact]
    public void DetectLoop_NoLoop_ReturnsFalse()
    {
        var history = new List<ITurn>
        {
            CreateAssistantTurnWithToolCall("read_file", "{\"path\": \"/a\"}"),
            CreateAssistantTurnWithToolCall("write_file", "{\"path\": \"/b\"}"),
            CreateAssistantTurnWithToolCall("bash", "{\"command\": \"ls\"}")
        };

        Assert.False(LoopDetection.DetectLoop(history));
    }

    [Fact]
    public void DetectLoop_SingleToolRepeated_DetectsLoop()
    {
        var history = new List<ITurn>();
        // Same tool call repeated 3 times
        for (int i = 0; i < 6; i++)
        {
            history.Add(CreateAssistantTurnWithToolCall("read_file", "{\"path\": \"/same\"}"));
        }

        Assert.True(LoopDetection.DetectLoop(history));
    }

    [Fact]
    public void DetectLoop_TwoToolPattern_DetectsLoop()
    {
        var history = new List<ITurn>();
        // A-B-A-B-A-B pattern (3 repetitions)
        for (int i = 0; i < 3; i++)
        {
            history.Add(CreateAssistantTurnWithToolCall("read_file", "{\"path\": \"/a\"}"));
            history.Add(CreateAssistantTurnWithToolCall("write_file", "{\"path\": \"/a\"}"));
        }

        Assert.True(LoopDetection.DetectLoop(history));
    }

    [Fact]
    public void ExtractToolCallSignatures_ExtractsFromHistory()
    {
        var history = new List<ITurn>
        {
            new UserTurn("hello", DateTimeOffset.UtcNow),
            CreateAssistantTurnWithToolCall("read_file", "{\"path\": \"/a\"}"),
            CreateAssistantTurnWithToolCall("bash", "{\"cmd\": \"ls\"}")
        };

        var sigs = LoopDetection.ExtractToolCallSignatures(history);
        Assert.Equal(2, sigs.Count);
        Assert.StartsWith("read_file:", sigs[0]);
        Assert.StartsWith("bash:", sigs[1]);
    }

    [Fact]
    public void DetectLoop_TooFewItems_ReturnsFalse()
    {
        var history = new List<ITurn>
        {
            CreateAssistantTurnWithToolCall("read_file", "{\"path\": \"/a\"}")
        };

        Assert.False(LoopDetection.DetectLoop(history));
    }

    private static AssistantTurn CreateAssistantTurnWithToolCall(string toolName, string args)
    {
        return new AssistantTurn(
            "",
            new List<ToolCallData>
            {
                new ToolCallData(Guid.NewGuid().ToString(), toolName, args, "function")
            },
            null,
            Usage.Empty,
            null,
            DateTimeOffset.UtcNow);
    }
}

// ── 9.10 Event System ───────────────────────────────────────────────────────

public class EventEmitterTests
{
    [Fact]
    public async Task EventEmitter_Subscribe_ReceivesEvents()
    {
        var emitter = new EventEmitter { SessionId = "test-session" };
        var received = new List<SessionEvent>();

        emitter.Subscribe(evt =>
        {
            received.Add(evt);
            return Task.CompletedTask;
        });

        await emitter.EmitAsync(EventKind.SessionStart);
        await emitter.EmitAsync(EventKind.UserInput, new Dictionary<string, object?> { ["content"] = "hello" });

        Assert.Equal(2, received.Count);
        Assert.Equal(EventKind.SessionStart, received[0].Kind);
        Assert.Equal("test-session", received[0].SessionId);
        Assert.Equal(EventKind.UserInput, received[1].Kind);
        Assert.Equal("hello", received[1].Data["content"]);
    }

    [Fact]
    public async Task EventEmitter_MultipleSubscribers_AllReceive()
    {
        var emitter = new EventEmitter();
        int count1 = 0, count2 = 0;

        emitter.Subscribe(_ => { count1++; return Task.CompletedTask; });
        emitter.Subscribe(_ => { count2++; return Task.CompletedTask; });

        await emitter.EmitAsync(EventKind.SessionStart);

        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
    }
}

// ── 9.8 Turns ───────────────────────────────────────────────────────────────

public class TurnsTests
{
    [Fact]
    public void UserTurn_HasCorrectTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var turn = new UserTurn("hello", DateTimeOffset.UtcNow);
        Assert.True(turn.Timestamp >= before);
    }

    [Fact]
    public void AssistantTurn_HasToolCalls()
    {
        var toolCalls = new List<ToolCallData>
        {
            new("id1", "read_file", "{}", "function")
        };
        var turn = new AssistantTurn("text", toolCalls, null, Usage.Empty, "resp1", DateTimeOffset.UtcNow);
        Assert.Single(turn.ToolCalls);
        Assert.Equal("text", turn.Content);
    }

    [Fact]
    public void SteeringTurn_ImplementsITurn()
    {
        var turn = new SteeringTurn("guidance", DateTimeOffset.UtcNow);
        Assert.IsAssignableFrom<ITurn>(turn);
        Assert.Equal("guidance", turn.Content);
    }
}

// ── SessionConfig ───────────────────────────────────────────────────────────

public class SessionConfigTests
{
    [Fact]
    public void SessionConfig_Defaults()
    {
        var config = new SessionConfig();
        Assert.Equal(0, config.MaxTurns);
        Assert.Equal(0, config.MaxToolRoundsPerInput);
        Assert.Equal(10000, config.DefaultCommandTimeoutMs);
        Assert.Equal(600000, config.MaxCommandTimeoutMs);
        Assert.True(config.EnableLoopDetection);
        Assert.Equal(10, config.LoopDetectionWindow);
        Assert.Equal(1, config.MaxSubagentDepth);
    }
}

// ── Test helpers ─────────────────────────────────────────────────────────────

internal class FakeProfile : IProviderProfile
{
    private readonly IProviderAdapter _adapter;
    public string Id => "fake";
    public string Model { get; set; } = "fake-model";
    public ToolRegistry ToolRegistry { get; } = new();
    public bool SupportsReasoning => false;
    public bool SupportsStreaming => false;
    public bool SupportsParallelToolCalls_Value { get; set; }
    public bool SupportsParallelToolCalls => SupportsParallelToolCalls_Value;
    public int ContextWindowSize => 8000;

    public FakeProfile(IProviderAdapter adapter)
    {
        _adapter = adapter;
        ToolRegistry.Register(new RegisteredTool(
            "test_tool",
            new ToolDefinition("test_tool", "test", []),
            (_, _) => Task.FromResult("test result")));
    }

    public string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null)
        => "You are a test assistant.";

    public IReadOnlyList<ToolDefinition> Tools() => ToolRegistry.GetDefinitions();
    public Dictionary<string, object>? ProviderOptions() => null;
}

internal class FakeExecutionEnvironment : IExecutionEnvironment
{
    public string WorkingDirectory => "/fake/workdir";

    public Task<string> ReadFileAsync(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => Task.FromResult($"Content of {path}");

    public Task WriteFileAsync(string path, string content, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string> RunCommandAsync(string command, int? timeoutMs = null, CancellationToken ct = default)
        => Task.FromResult($"Output of: {command}");

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, string? path = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new List<string> { "/fake/file1.cs", "/fake/file2.cs" });

    public Task<IReadOnlyList<string>> GrepAsync(string pattern, string? path = null, string? globFilter = null, bool caseInsensitive = false, int? maxResults = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new List<string> { "/fake/file1.cs:10:matching line" });

    public Task<string> ListDirectoryAsync(string path, CancellationToken ct = default)
        => Task.FromResult($"Directory: {path}\n  [DIR]  subdir/\n  [FILE] test.cs (100 bytes)");

    public Task<string> ReadManyFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        => Task.FromResult(string.Join("\n", paths.Select(p => $"=== {p} ===\nContent of {p}\n")));

    public bool FileExists(string path) => true;

    public Task<string> EditFileAsync(string path, string oldString, string newString, CancellationToken ct = default)
        => Task.FromResult("File edited successfully");
}

internal class LoopingProvider : IProviderAdapter
{
    public string Name => "looping";
    private int _count;
    private readonly int _maxLoops;

    public LoopingProvider(int maxLoops = 3)
    {
        _maxLoops = maxLoops;
    }

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        _count++;
        if (_count > _maxLoops)
        {
            return Task.FromResult(new Response(
                "id", "model", "test",
                Message.AssistantMsg("Done"),
                FinishReason.Stop, Usage.Empty));
        }

        var msg = new Message(Role.Assistant, new List<ContentPart>
        {
            ContentPart.TextPart("calling tool"),
            ContentPart.ToolCallPart(new ToolCallData($"tc-{_count}", "test_tool", "{}", "function"))
        });

        return Task.FromResult(new Response(
            $"id-{_count}", "model", "test",
            msg, FinishReason.ToolCalls, Usage.Empty));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = "test" };
        await Task.CompletedTask;
    }
}

// ── Phase 1 Fixes ───────────────────────────────────────────────────────────

public class SessionAbortTests
{
    [Fact]
    public async Task Session_TransitionsToClosed_OnCancellation()
    {
        var cts = new CancellationTokenSource();
        var provider = new SlowProvider();
        var session = new Session(
            provider,
            new FakeProfile(provider),
            new FakeExecutionEnvironment());

        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ProcessInputAsync("do something", cts.Token));

        Assert.Equal(SessionState.Closed, session.State);
    }
}

// ── Subagent Tests ──────────────────────────────────────────────────────────

public class SubAgentTests
{
    [Fact]
    public void SpawnSubagent_IncreasesDepth()
    {
        var provider = new FakeProvider("test");
        var session = new Session(
            provider,
            new FakeProfile(provider),
            new FakeExecutionEnvironment());

        var subagent = session.SpawnSubagent();

        Assert.NotNull(subagent);
        Assert.Equal(1, subagent.Depth);
        Assert.Single(session.Subagents);
    }

    [Fact]
    public void SpawnSubagent_ThrowsAtMaxDepth()
    {
        var provider = new FakeProvider("test");
        var session = new Session(
            provider,
            new FakeProfile(provider),
            new FakeExecutionEnvironment())
        { Depth = SubAgent.DefaultMaxDepth };

        Assert.Throws<InvalidOperationException>(() => session.SpawnSubagent());
    }

    [Fact]
    public void CloseSubagent_RemovesFromList()
    {
        var provider = new FakeProvider("test");
        var session = new Session(
            provider,
            new FakeProfile(provider),
            new FakeExecutionEnvironment());

        var subagent = session.SpawnSubagent();
        Assert.Single(session.Subagents);

        session.CloseSubagent(subagent.Id);
        Assert.Empty(session.Subagents);
    }

    [Fact]
    public void GetSubagent_FindsById()
    {
        var provider = new FakeProvider("test");
        var session = new Session(
            provider,
            new FakeProfile(provider),
            new FakeExecutionEnvironment());

        var subagent = session.SpawnSubagent();
        var found = session.GetSubagent(subagent.Id);

        Assert.NotNull(found);
        Assert.Equal(subagent.Id, found!.Id);
    }

    [Fact]
    public void GetSubagent_ReturnsNull_ForUnknownId()
    {
        var provider = new FakeProvider("test");
        var session = new Session(
            provider,
            new FakeProfile(provider),
            new FakeExecutionEnvironment());

        Assert.Null(session.GetSubagent("nonexistent"));
    }
}

// ── Tool Parameter Tests ────────────────────────────────────────────────────

public class ToolParameterTests
{
    [Fact]
    public void AnthropicProfile_GrepHasGlobFilter()
    {
        var profile = new AnthropicProfile();
        var grepTool = profile.Tools().First(t => t.Name == "grep");
        Assert.Contains(grepTool.Parameters, p => p.Name == "glob_filter");
        Assert.Contains(grepTool.Parameters, p => p.Name == "case_insensitive");
        Assert.Contains(grepTool.Parameters, p => p.Name == "max_results");
    }

    [Fact]
    public void AllProfiles_GlobHasPathParam()
    {
        var anthropic = new AnthropicProfile();
        var openai = new OpenAiProfile();
        var gemini = new GeminiProfile();

        foreach (var profile in new IProviderProfile[] { anthropic, openai, gemini })
        {
            var globTool = profile.Tools().First(t => t.Name == "glob");
            Assert.Contains(globTool.Parameters, p => p.Name == "path");
        }
    }

    [Fact]
    public void GeminiProfile_HasListDir()
    {
        var profile = new GeminiProfile();
        Assert.Contains(profile.Tools(), t => t.Name == "list_dir");
    }

    [Fact]
    public void GeminiProfile_HasReadManyFiles()
    {
        var profile = new GeminiProfile();
        Assert.Contains(profile.Tools(), t => t.Name == "read_many_files");
    }
}

// ── ProjectDocs Tests ───────────────────────────────────────────────────────

public class ProjectDocsTests
{
    [Fact]
    public void Discover_ReturnsEmpty_ForFakePath()
    {
        var docs = ProjectDocs.Discover("/nonexistent/path");
        Assert.Empty(docs);
    }

    [Fact]
    public void Discover_ReturnsEmpty_ForTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var docs = ProjectDocs.Discover(tempDir);
            Assert.Empty(docs);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discover_FindsClaudeMd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        // Create a .git dir so it stops walking up
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        File.WriteAllText(Path.Combine(tempDir, "CLAUDE.md"), "# Project Instructions\nTest content");
        try
        {
            var docs = ProjectDocs.Discover(tempDir);
            Assert.Single(docs);
            Assert.Contains("Test content", docs[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

// ── QA Plan Tests T1-T4, T17-T19 ────────────────────────────────────────────

public class T1_GrepExecutionTests
{
    [Fact]
    public async Task GrepAsync_GlobFilter_ExcludesNonMatchingFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "match.txt"), "hello world");
        File.WriteAllText(Path.Combine(tempDir, "skip.cs"), "hello world");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var results = await env.GrepAsync("hello", globFilter: "*.txt");
            Assert.Single(results);
            Assert.Contains("match.txt", results[0]);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task GrepAsync_CaseInsensitive_MatchesUpperCase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.txt"), "HELLO WORLD");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var sensitive = await env.GrepAsync("hello");
            var insensitive = await env.GrepAsync("hello", caseInsensitive: true);
            Assert.Empty(sensitive);
            Assert.Single(insensitive);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task GrepAsync_MaxResults_LimitsOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "test.txt"), "match1\nmatch2\nmatch3\nmatch4\nmatch5");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var all = await env.GrepAsync("match");
            var limited = await env.GrepAsync("match", maxResults: 2);
            Assert.Equal(5, all.Count);
            Assert.Equal(2, limited.Count);
        }
        finally { Directory.Delete(tempDir, true); }
    }
}

public class T2_GlobPathParamTests
{
    [Fact]
    public async Task GlobAsync_PathParam_SearchesOnlySubdir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        var subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(tempDir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(subDir, "child.txt"), "child");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var subOnly = await env.GlobAsync("*.txt", path: subDir);
            Assert.Single(subOnly);
            Assert.Contains("child.txt", subOnly[0]);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task GlobAsync_NoPath_DefaultsToWorkingDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "file.txt"), "data");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var results = await env.GlobAsync("*.txt");
            Assert.Single(results);
        }
        finally { Directory.Delete(tempDir, true); }
    }
}

public class T3_ListDirTests
{
    [Fact]
    public async Task ListDirectoryAsync_ShowsFilesAndDirs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "subdir"));
        File.WriteAllText(Path.Combine(tempDir, "file.txt"), "content");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var output = await env.ListDirectoryAsync(tempDir);
            Assert.Contains("[DIR]", output);
            Assert.Contains("subdir", output);
            Assert.Contains("[FILE]", output);
            Assert.Contains("file.txt", output);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task ListDirectoryAsync_NonExistentPath_ReturnsError()
    {
        var env = new LocalExecutionEnvironment("/tmp");
        var output = await env.ListDirectoryAsync("/nonexistent/path/xyz");
        Assert.Contains("Error", output);
    }
}

public class T4_ReadManyFilesTests
{
    [Fact]
    public async Task ReadManyFilesAsync_ConcatenatesContents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var f1 = Path.Combine(tempDir, "a.txt");
        var f2 = Path.Combine(tempDir, "b.txt");
        var f3 = Path.Combine(tempDir, "c.txt");
        File.WriteAllText(f1, "content_a");
        File.WriteAllText(f2, "content_b");
        File.WriteAllText(f3, "content_c");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var output = await env.ReadManyFilesAsync(new List<string> { f1, f2, f3 });
            Assert.Contains("content_a", output);
            Assert.Contains("content_b", output);
            Assert.Contains("content_c", output);
            Assert.Contains("===", output); // Separator headers
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task ReadManyFilesAsync_HandlesNonExistentFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var f1 = Path.Combine(tempDir, "exists.txt");
        File.WriteAllText(f1, "real_content");
        try
        {
            var env = new LocalExecutionEnvironment(tempDir);
            var output = await env.ReadManyFilesAsync(new List<string> { f1, "/nonexistent.txt" });
            Assert.Contains("real_content", output);
            Assert.Contains("File not found", output);
        }
        finally { Directory.Delete(tempDir, true); }
    }
}

public class T17_SessionParallelToolExecutionTests
{
    [Fact]
    public async Task Session_ExecutesMultipleToolCalls()
    {
        var provider = new MultiToolProvider();
        var profile = new FakeProfile(provider);
        profile.SupportsParallelToolCalls_Value = true;

        // Register a test tool
        profile.ToolRegistry.Register(new RegisteredTool(
            "test_tool",
            new ToolDefinition("test_tool", "A test tool",
                new List<ToolParameter>()),
            async (args, env) => "tool_result"));

        var session = new Session(provider, profile, new FakeExecutionEnvironment());
        var result = await session.ProcessInputAsync("do work");

        // The provider returns 3 tool calls, then a final response
        Assert.Equal("all done", result.Content);
        // History should contain tool results for all 3 calls
        var toolResultTurns = session.History.OfType<ToolResultsTurn>().ToList();
        Assert.Single(toolResultTurns);
        Assert.Equal(3, toolResultTurns[0].Results.Count);
    }
}

public class T18_SessionProviderOptionsTests
{
    [Fact]
    public async Task Session_AttachesProviderOptions_ToRequest()
    {
        var provider = new CapturingProvider();
        var profile = new OptionsProfile(provider);
        var session = new Session(provider, profile, new FakeExecutionEnvironment());

        await session.ProcessInputAsync("test");

        Assert.NotNull(provider.LastRequest);
        Assert.NotNull(provider.LastRequest!.ProviderOptions);
        Assert.True(provider.LastRequest.ProviderOptions!.ContainsKey("custom_option"));
        Assert.Equal("test_value", provider.LastRequest.ProviderOptions["custom_option"]);
    }
}

public class T19_SubagentToolRegistrationTests
{
    [Fact]
    public void AllProfiles_HaveSubagentTools_AfterRegistration()
    {
        var anthropic = new AnthropicProfile();
        anthropic.RegisterSubagentTools();
        var openai = new OpenAiProfile();
        openai.RegisterSubagentTools();
        var gemini = new GeminiProfile();
        gemini.RegisterSubagentTools();

        foreach (var profile in new IProviderProfile[] { anthropic, openai, gemini })
        {
            var tools = profile.Tools();
            Assert.Contains(tools, t => t.Name == "spawn_agent");
            Assert.Contains(tools, t => t.Name == "send_input");
            Assert.Contains(tools, t => t.Name == "wait_agent");
            Assert.Contains(tools, t => t.Name == "close_agent");
        }
    }
}

// ── T17-T18 test helpers ────────────────────────────────────────────────────

internal class MultiToolProvider : IProviderAdapter
{
    public string Name => "multi";
    private int _callCount;

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        _callCount++;
        if (_callCount == 1)
        {
            // Return 3 tool calls
            var msg = new Message(Role.Assistant, new List<ContentPart>
            {
                ContentPart.TextPart("calling tools"),
                ContentPart.ToolCallPart(new ToolCallData("tc1", "test_tool", "{}", "function")),
                ContentPart.ToolCallPart(new ToolCallData("tc2", "test_tool", "{}", "function")),
                ContentPart.ToolCallPart(new ToolCallData("tc3", "test_tool", "{}", "function"))
            });
            return Task.FromResult(new Response("id1", "model", "multi", msg, FinishReason.ToolCalls, Usage.Empty));
        }
        // Final response
        return Task.FromResult(new Response("id2", "model", "multi",
            Message.AssistantMsg("all done"), FinishReason.Stop, Usage.Empty));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = "test" };
        await Task.CompletedTask;
    }
}

internal class CapturingProvider : IProviderAdapter
{
    public string Name => "capturing";
    public Request? LastRequest { get; private set; }

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new Response("id", "model", "capturing",
            Message.AssistantMsg("captured"), FinishReason.Stop, Usage.Empty));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = "test" };
        await Task.CompletedTask;
    }
}

internal class OptionsProfile : IProviderProfile
{
    private readonly IProviderAdapter _adapter;
    public string Id => "options";
    public string Model { get; set; } = "test-model";
    public ToolRegistry ToolRegistry { get; } = new();
    public bool SupportsReasoning => false;
    public bool SupportsStreaming => false;
    public bool SupportsParallelToolCalls => false;
    public int ContextWindowSize => 8000;

    public OptionsProfile(IProviderAdapter adapter) { _adapter = adapter; }

    public string BuildSystemPrompt(IExecutionEnvironment env, IReadOnlyList<string>? projectDocs = null) => "test";
    public IReadOnlyList<ToolDefinition> Tools() => ToolRegistry.GetDefinitions();
    public Dictionary<string, object>? ProviderOptions() => new() { ["custom_option"] = "test_value" };
}

internal class SlowProvider : IProviderAdapter
{
    public string Name => "slow";

    public async Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), ct);
        return new Response("id", "model", "slow",
            Message.AssistantMsg("done"), FinishReason.Stop, Usage.Empty);
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), ct);
        yield break;
    }
}
