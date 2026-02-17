using JcAttractor.UnifiedLlm;
using System.Net;

namespace JcAttractor.Tests;

// ── 8.1 Core Infrastructure ──────────────────────────────────────────────────

public class ClientTests
{
    [Fact]
    public void Client_ThrowsConfigurationError_WhenNoProviders()
    {
        Assert.Throws<ConfigurationError>(() =>
            new Client(new Dictionary<string, IProviderAdapter>()));
    }

    [Fact]
    public void Client_ThrowsConfigurationError_WhenDefaultProviderNotRegistered()
    {
        var providers = new Dictionary<string, IProviderAdapter>
        {
            ["anthropic"] = new FakeProvider("anthropic")
        };
        Assert.Throws<ConfigurationError>(() =>
            new Client(providers, defaultProvider: "openai"));
    }

    [Fact]
    public void Client_ExposesRegisteredProviderNames()
    {
        var providers = new Dictionary<string, IProviderAdapter>
        {
            ["anthropic"] = new FakeProvider("anthropic"),
            ["openai"] = new FakeProvider("openai")
        };
        var client = new Client(providers);
        Assert.Contains("anthropic", client.Providers);
        Assert.Contains("openai", client.Providers);
    }

    [Fact]
    public async Task Client_InfersProviderFromModelName_Claude()
    {
        var anthropic = new FakeProvider("anthropic");
        var openai = new FakeProvider("openai");
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["anthropic"] = anthropic,
            ["openai"] = openai
        });
        await client.CompleteAsync(new Request
        {
            Model = "claude-sonnet-4-5-20250514",
            Messages = [Message.UserMsg("hi")]
        });
        Assert.True(anthropic.CallCount > 0);
        Assert.Equal(0, openai.CallCount);
    }

    [Fact]
    public async Task Client_InfersProviderFromModelName_GPT()
    {
        var anthropic = new FakeProvider("anthropic");
        var openai = new FakeProvider("openai");
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["anthropic"] = anthropic,
            ["openai"] = openai
        });
        await client.CompleteAsync(new Request
        {
            Model = "gpt-5.2",
            Messages = [Message.UserMsg("hi")]
        });
        Assert.Equal(0, anthropic.CallCount);
        Assert.True(openai.CallCount > 0);
    }

    [Fact]
    public async Task Client_InfersProviderFromModelName_Gemini()
    {
        var gemini = new FakeProvider("gemini");
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["gemini"] = gemini
        });
        await client.CompleteAsync(new Request
        {
            Model = "gemini-3.0-pro-preview",
            Messages = [Message.UserMsg("hi")]
        });
        Assert.True(gemini.CallCount > 0);
    }

    [Fact]
    public async Task Client_UsesExplicitProvider_WhenSpecified()
    {
        var anthropic = new FakeProvider("anthropic");
        var openai = new FakeProvider("openai");
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["anthropic"] = anthropic,
            ["openai"] = openai
        });
        // Model name says "claude" but Provider explicitly says "openai"
        await client.CompleteAsync(new Request
        {
            Model = "claude-opus-4-6",
            Provider = "openai",
            Messages = [Message.UserMsg("hi")]
        });
        Assert.True(openai.CallCount > 0);
        Assert.Equal(0, anthropic.CallCount);
    }

    [Fact]
    public async Task Client_CompleteAsync_RoutesToCorrectProvider()
    {
        var fakeAnthropic = new FakeProvider("anthropic");
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["anthropic"] = fakeAnthropic
        });

        var response = await client.CompleteAsync(new Request
        {
            Model = "claude-opus-4-6",
            Messages = [Message.UserMsg("hello")]
        });

        Assert.Equal("anthropic", response.Provider);
        Assert.True(fakeAnthropic.CallCount > 0);
    }

    [Fact]
    public async Task Client_Middleware_AppliedInOrder()
    {
        var log = new List<string>();
        var fakeProvider = new FakeProvider("test");
        var middleware = new List<Func<Request, Func<Request, Task<Response>>, Task<Response>>>
        {
            async (req, next) => { log.Add("mw1-before"); var r = await next(req); log.Add("mw1-after"); return r; },
            async (req, next) => { log.Add("mw2-before"); var r = await next(req); log.Add("mw2-after"); return r; },
        };

        var client = new Client(
            new Dictionary<string, IProviderAdapter> { ["test"] = fakeProvider },
            middleware: middleware);

        await client.CompleteAsync(new Request
        {
            Model = "test-model",
            Provider = "test",
            Messages = [Message.UserMsg("hi")]
        });

        Assert.Equal(["mw1-before", "mw2-before", "mw2-after", "mw1-after"], log);
    }
}

// ── 8.3 Message & Content Model ──────────────────────────────────────────────

public class MessageAndContentTests
{
    [Fact]
    public void ContentPart_TextPart_SetsCorrectKind()
    {
        var part = ContentPart.TextPart("hello");
        Assert.Equal(ContentKind.Text, part.Kind);
        Assert.Equal("hello", part.Text);
    }

    [Fact]
    public void ContentPart_ImagePart_SetsCorrectKind()
    {
        var part = ContentPart.ImagePart(new ImageData("http://img", null, "image/png", null));
        Assert.Equal(ContentKind.Image, part.Kind);
        Assert.NotNull(part.Image);
    }

    [Fact]
    public void ContentPart_ToolCallPart_SetsCorrectKind()
    {
        var tc = new ToolCallData("id1", "tool_name", "{}", "function");
        var part = ContentPart.ToolCallPart(tc);
        Assert.Equal(ContentKind.ToolCall, part.Kind);
        Assert.Equal("id1", part.ToolCall!.Id);
    }

    [Fact]
    public void ContentPart_ThinkingPart_SetsThinkingKind()
    {
        var part = ContentPart.ThinkingPart(new ThinkingData("I think...", null, false));
        Assert.Equal(ContentKind.Thinking, part.Kind);
        Assert.Equal("I think...", part.Thinking!.Text);
    }

    [Fact]
    public void ContentPart_ThinkingPart_SetsRedactedKind_WhenRedacted()
    {
        var part = ContentPart.ThinkingPart(new ThinkingData("redacted", "sig", true));
        Assert.Equal(ContentKind.RedactedThinking, part.Kind);
    }

    [Fact]
    public void Message_SystemMsg_CorrectRole()
    {
        var msg = Message.SystemMsg("You are helpful");
        Assert.Equal(Role.System, msg.Role);
        Assert.Equal("You are helpful", msg.Text);
    }

    [Fact]
    public void Message_UserMsg_CorrectRole()
    {
        var msg = Message.UserMsg("Hello!");
        Assert.Equal(Role.User, msg.Role);
        Assert.Equal("Hello!", msg.Text);
    }

    [Fact]
    public void Message_AssistantMsg_CorrectRole()
    {
        var msg = Message.AssistantMsg("Hi there!");
        Assert.Equal(Role.Assistant, msg.Role);
    }

    [Fact]
    public void Message_ToolResultMsg_CorrectFields()
    {
        var msg = Message.ToolResultMsg("tc1", "result text", true);
        Assert.Equal(Role.Tool, msg.Role);
        Assert.Equal("tc1", msg.ToolCallId);
        var part = msg.Content[0];
        Assert.Equal(ContentKind.ToolResult, part.Kind);
        Assert.True(part.ToolResult!.IsError);
    }

    [Fact]
    public void Message_Text_ConcatenatesMultipleTextParts()
    {
        var msg = new Message(Role.Assistant, new List<ContentPart>
        {
            ContentPart.TextPart("Hello "),
            ContentPart.TextPart("World"),
            ContentPart.ToolCallPart(new ToolCallData("id", "tool", "{}")),
            ContentPart.TextPart("!")
        });
        Assert.Equal("Hello World!", msg.Text);
    }
}

// ── 8.4 & 8.5 Response Model ────────────────────────────────────────────────

public class ResponseTests
{
    [Fact]
    public void Response_Text_ReturnsMessageText()
    {
        var response = CreateResponse("Hello World");
        Assert.Equal("Hello World", response.Text);
    }

    [Fact]
    public void Response_ToolCalls_ExtractsToolCallParts()
    {
        var tc1 = new ToolCallData("id1", "read_file", "{\"path\":\"/tmp\"}", "function");
        var tc2 = new ToolCallData("id2", "write_file", "{\"path\":\"/tmp\"}", "function");

        var msg = new Message(Role.Assistant, new List<ContentPart>
        {
            ContentPart.TextPart("Let me help"),
            ContentPart.ToolCallPart(tc1),
            ContentPart.ToolCallPart(tc2)
        });

        var response = new Response("id", "model", "provider", msg, FinishReason.ToolCalls, Usage.Empty);
        Assert.Equal(2, response.ToolCalls.Count);
        Assert.Equal("read_file", response.ToolCalls[0].Name);
    }

    [Fact]
    public void Response_Reasoning_ConcatenatesThinkingParts()
    {
        var msg = new Message(Role.Assistant, new List<ContentPart>
        {
            ContentPart.ThinkingPart(new ThinkingData("First thought. ", null, false)),
            ContentPart.TextPart("The answer is 42"),
            ContentPart.ThinkingPart(new ThinkingData("Second thought.", null, false))
        });

        var response = new Response("id", "model", "provider", msg, FinishReason.Stop, Usage.Empty);
        Assert.Equal("First thought. Second thought.", response.Reasoning);
    }

    [Fact]
    public void Response_Reasoning_ReturnsNull_WhenNoThinkingParts()
    {
        var response = CreateResponse("no thinking");
        Assert.Null(response.Reasoning);
    }

    [Fact]
    public void FinishReason_StaticInstances_HaveCorrectReasons()
    {
        Assert.Equal("stop", FinishReason.Stop.Reason);
        Assert.Equal("length", FinishReason.Length.Reason);
        Assert.Equal("tool_calls", FinishReason.ToolCalls.Reason);
        Assert.Equal("content_filter", FinishReason.ContentFilter.Reason);
        Assert.Equal("error", FinishReason.Error.Reason);
    }

    [Fact]
    public void Usage_Addition_SumsCorrectly()
    {
        var a = new Usage(10, 20, 30, ReasoningTokens: 5, CacheReadTokens: 2);
        var b = new Usage(15, 25, 40, ReasoningTokens: 3, CacheWriteTokens: 4);
        var sum = a + b;

        Assert.Equal(25, sum.InputTokens);
        Assert.Equal(45, sum.OutputTokens);
        Assert.Equal(70, sum.TotalTokens);
        Assert.Equal(8, sum.ReasoningTokens);
        Assert.Equal(2, sum.CacheReadTokens);
        Assert.Equal(4, sum.CacheWriteTokens);
    }

    [Fact]
    public void Usage_Addition_NullsRemainsNull_WhenBothZero()
    {
        var a = new Usage(10, 20, 30);
        var b = new Usage(5, 10, 15);
        var sum = a + b;

        Assert.Null(sum.ReasoningTokens);
        Assert.Null(sum.CacheReadTokens);
    }

    private static Response CreateResponse(string text)
    {
        return new Response("id", "model", "provider",
            Message.AssistantMsg(text), FinishReason.Stop, Usage.Empty);
    }
}

// ── 8.6 Tool Definitions ────────────────────────────────────────────────────

public class ToolDefinitionTests
{
    [Fact]
    public void ToolChoice_StaticInstances_HaveCorrectModes()
    {
        Assert.Equal(ToolChoiceMode.Auto, ToolChoice.Auto.Mode);
        Assert.Equal(ToolChoiceMode.None, ToolChoice.NoneChoice.Mode);
        Assert.Equal(ToolChoiceMode.Required, ToolChoice.Required.Mode);
    }

    [Fact]
    public void ToolChoice_Named_SetsToolName()
    {
        var choice = ToolChoice.Named("read_file");
        Assert.Equal(ToolChoiceMode.Named, choice.Mode);
        Assert.Equal("read_file", choice.ToolName);
    }

    [Fact]
    public void ResponseFormat_StaticInstances()
    {
        Assert.Equal("text", ResponseFormat.TextFormat.Type);
        Assert.Equal("json_object", ResponseFormat.JsonFormat.Type);
    }

    [Fact]
    public void ResponseFormat_JsonSchemaFormat_SetsProperties()
    {
        var schema = new Dictionary<string, object> { ["type"] = "object" };
        var format = ResponseFormat.JsonSchemaFormat(schema);
        Assert.Equal("json_schema", format.Type);
        Assert.True(format.Strict);
        Assert.NotNull(format.JsonSchema);
    }
}

// ── 8.7 Model Catalog ───────────────────────────────────────────────────────

public class ModelCatalogTests
{
    [Fact]
    public void GetModelInfo_FindsByExactId()
    {
        var model = ModelCatalog.GetModelInfo("claude-opus-4-6");
        Assert.NotNull(model);
        Assert.Equal("anthropic", model.Provider);
        Assert.Equal("Claude Opus 4.6", model.DisplayName);
    }

    [Fact]
    public void GetModelInfo_FindsByAlias()
    {
        var model = ModelCatalog.GetModelInfo("claude-opus-4-6-20250617");
        Assert.NotNull(model);
        Assert.Equal("claude-opus-4-6", model.Id);
    }

    [Fact]
    public void GetModelInfo_ReturnsNull_ForUnknownModel()
    {
        Assert.Null(ModelCatalog.GetModelInfo("unknown-model-xyz"));
    }

    [Fact]
    public void ListModels_ReturnsAllModels_WhenNoFilter()
    {
        var all = ModelCatalog.ListModels();
        Assert.True(all.Count >= 7); // We have at least 7 models
    }

    [Fact]
    public void ListModels_FiltersbyProvider()
    {
        var anthropicModels = ModelCatalog.ListModels("anthropic");
        Assert.All(anthropicModels, m => Assert.Equal("anthropic", m.Provider));
        Assert.True(anthropicModels.Count >= 2);
    }

    [Fact]
    public void GetLatestModel_ReturnsFirstModelForProvider()
    {
        var latest = ModelCatalog.GetLatestModel("openai");
        Assert.NotNull(latest);
        Assert.Equal("openai", latest.Provider);
    }

    [Fact]
    public void GetLatestModel_FiltersByCapability()
    {
        var model = ModelCatalog.GetLatestModel("anthropic", "reasoning");
        Assert.NotNull(model);
        Assert.True(model.SupportsReasoning);
    }

    [Fact]
    public void ModelInfo_HasCostInfo()
    {
        var model = ModelCatalog.GetModelInfo("claude-opus-4-6");
        Assert.NotNull(model);
        Assert.True(model.InputCostPerMillion > 0);
        Assert.True(model.OutputCostPerMillion > 0);
    }
}

// ── 8.8 Error Handling ──────────────────────────────────────────────────────

public class ErrorHandlingTests
{
    [Fact]
    public void SdkException_IsBaseException()
    {
        var ex = new SdkException("test error");
        Assert.Equal("test error", ex.Message);
        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void ConfigurationError_InheritsSdkException()
    {
        var ex = new ConfigurationError("missing key");
        Assert.IsAssignableFrom<SdkException>(ex);
    }

    [Fact]
    public void ProviderError_HasHttpStatusAndRetryInfo()
    {
        var ex = new ProviderError("server error", HttpStatusCode.InternalServerError,
            retryable: true, retryAfter: TimeSpan.FromSeconds(5), providerName: "openai");
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.True(ex.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Equal("openai", ex.ProviderName);
    }

    [Fact]
    public void AuthenticationError_IsNotRetryable()
    {
        var ex = new AuthenticationError("unauthorized", HttpStatusCode.Unauthorized, "anthropic");
        Assert.False(ex.Retryable);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public void RateLimitError_IsRetryable_With429()
    {
        var ex = new RateLimitError("rate limited", TimeSpan.FromSeconds(30), "openai");
        Assert.True(ex.Retryable);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public void NotFoundError_IsNotRetryable()
    {
        var ex = new NotFoundError("model not found", "gemini");
        Assert.False(ex.Retryable);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public void ContentFilterError_IsNotRetryable()
    {
        var ex = new ContentFilterError("content blocked");
        Assert.False(ex.Retryable);
    }

    [Fact]
    public void NoObjectGeneratedError_StoresRawOutput()
    {
        var ex = new NoObjectGeneratedError("failed to generate", "raw json here");
        Assert.Equal("raw json here", ex.RawOutput);
    }
}

// ── Request Model ───────────────────────────────────────────────────────────

public class RequestTests
{
    [Fact]
    public void Request_RequiredFields_ModelAndMessages()
    {
        var req = new Request
        {
            Model = "claude-opus-4-6",
            Messages = [Message.UserMsg("hello")]
        };
        Assert.Equal("claude-opus-4-6", req.Model);
        Assert.Single(req.Messages);
    }

    [Fact]
    public void Request_OptionalFields_DefaultToNull()
    {
        var req = new Request { Model = "test", Messages = [] };
        Assert.Null(req.Provider);
        Assert.Null(req.Tools);
        Assert.Null(req.ToolChoice);
        Assert.Null(req.Temperature);
        Assert.Null(req.MaxTokens);
        Assert.Null(req.ReasoningEffort);
    }
}

// ── Fake Provider for testing ────────────────────────────────────────────────

internal class FakeProvider : IProviderAdapter
{
    public string Name { get; }
    public int CallCount { get; private set; }

    public FakeProvider(string name)
    {
        Name = name;
    }

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        CallCount++;
        var msg = Message.AssistantMsg("Fake response");
        return Task.FromResult(new Response(
            Id: Guid.NewGuid().ToString(),
            Model: request.Model,
            Provider: Name,
            Message: msg,
            FinishReason: FinishReason.Stop,
            Usage: new Usage(10, 20, 30)));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new StreamEvent
        {
            Type = StreamEventType.TextDelta,
            Delta = "Fake"
        };
        yield return new StreamEvent
        {
            Type = StreamEventType.Finish,
            FinishReason = FinishReason.Stop
        };
        await Task.CompletedTask;
    }
}
