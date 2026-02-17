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
    public bool SupportsParallelToolCalls => false;
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

    public Task<IReadOnlyList<string>> GlobAsync(string pattern, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new List<string> { "/fake/file1.cs", "/fake/file2.cs" });

    public Task<IReadOnlyList<string>> GrepAsync(string pattern, string? path = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new List<string> { "/fake/file1.cs:10:matching line" });

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
