using Soulcaster.UnifiedLlm;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Soulcaster.Tests;

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

    [Fact]
    public async Task Client_CompleteAsync_NormalizesModelAlias_BeforeCallingProvider()
    {
        var capturingProvider = new ModelCapturingProvider();
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["openai"] = capturingProvider
        });

        await client.CompleteAsync(new Request
        {
            Model = "codex-5.2",
            Messages = [Message.UserMsg("hi")]
        });

        Assert.Equal("gpt-5.2-codex", capturingProvider.LastReceivedModel);
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
    public void ImageData_FromFile_LoadsBytesAndInfersMediaType()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jc_image_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "sample.png");
        File.WriteAllBytes(path, Convert.FromBase64String(UnifiedLlmTestAssets.TestImageBase64));

        try
        {
            var image = ImageData.FromFile(path);
            Assert.NotNull(image.Data);
            Assert.Equal("image/png", image.MediaType);
            Assert.Null(image.Url);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
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
    public void Message_UserMsg_ContentPartsOverload_CorrectRole()
    {
        var msg = Message.UserMsg(
            ContentPart.TextPart("Hello!"),
            ContentPart.ImagePart(ImageData.FromBytes(Convert.FromBase64String(UnifiedLlmTestAssets.TestImageBase64))));

        Assert.Equal(Role.User, msg.Role);
        Assert.Equal("Hello!", msg.Text);
        Assert.Single(msg.Content.Where(part => part.Kind == ContentKind.Image));
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
    public void Response_Images_ExtractsImageParts()
    {
        var image = ImageData.FromBytes(Convert.FromBase64String(UnifiedLlmTestAssets.TestImageBase64), "image/png");
        var msg = new Message(Role.Assistant, new List<ContentPart>
        {
            ContentPart.TextPart("caption"),
            ContentPart.ImagePart(image)
        });

        var response = new Response("id", "model", "provider", msg, FinishReason.Stop, Usage.Empty);

        Assert.Single(response.Images);
        Assert.Equal("image/png", response.Images[0].MediaType);
        Assert.NotNull(response.Images[0].Data);
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
    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

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
        Assert.True(all.Count >= 10); // We have at least 10 models
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
    public void GetLatestModel_FiltersByAudioInputCapability()
    {
        var model = ModelCatalog.GetLatestModel("openai", "audio_input");
        Assert.NotNull(model);
        Assert.Equal("gpt-audio", model.Id);
        Assert.True(model.SupportsAudioInput);
    }

    [Fact]
    public void GetLatestModel_FiltersByAnthropicDocumentInputCapability()
    {
        var model = ModelCatalog.GetLatestModel("anthropic", "document_input");
        Assert.NotNull(model);
        Assert.Equal("anthropic", model.Provider);
        Assert.True(model.SupportsDocumentInput);
    }

    [Fact]
    public void ModelInfo_HasCostInfo()
    {
        var model = ModelCatalog.GetModelInfo("claude-opus-4-6");
        Assert.NotNull(model);
        Assert.True(model.InputCostPerMillion > 0);
        Assert.True(model.OutputCostPerMillion > 0);
    }

    [Fact]
    public void GetModelInfo_FindsCodex53_ById()
    {
        var info = ModelCatalog.GetModelInfo("codex-5.3");
        Assert.NotNull(info);
        Assert.Equal("openai", info.Provider);
        Assert.Equal("gpt-5.3-codex", info.Id);
        Assert.Equal("Codex 5.3", info.DisplayName);
    }

    [Fact]
    public void GetModelInfo_FindsCodex53_ByAlias()
    {
        var info = ModelCatalog.GetModelInfo("gpt-5.3-codex");
        Assert.NotNull(info);
        Assert.Equal("gpt-5.3-codex", info.Id);
    }

    [Fact]
    public void GetModelInfo_FindsGpt54_ById()
    {
        var info = ModelCatalog.GetModelInfo("gpt-5.4");
        Assert.NotNull(info);
        Assert.Equal("openai", info.Provider);
        Assert.Equal("gpt-5.4", info.Id);
        Assert.Equal("GPT-5.4", info.DisplayName);
        Assert.True(info.SupportsReasoning);
    }

    [Fact]
    public void GetModelInfo_Gpt54_HasNoPublishedCostInfo()
    {
        var info = ModelCatalog.GetModelInfo("gpt-5.4");
        Assert.NotNull(info);
        Assert.Null(info.InputCostPerMillion);
        Assert.Null(info.OutputCostPerMillion);
    }

    [Fact]
    public void GetModelInfo_FindsGptImage2_ById()
    {
        var info = ModelCatalog.GetModelInfo("gpt-image-2");
        Assert.NotNull(info);
        Assert.Equal("openai", info.Provider);
        Assert.Equal("gpt-image-2", info.Id);
        Assert.Equal("GPT Image 2", info.DisplayName);
        Assert.True(info.SupportsImageOutput);
        Assert.True(info.SupportsImageInput);
        Assert.False(info.SupportsTools);
    }

    [Fact]
    public void GetModelInfo_FindsGemini3Flash_ByExactPreviewId()
    {
        var info = ModelCatalog.GetModelInfo("gemini-3-flash-preview");
        Assert.NotNull(info);
        Assert.Equal("gemini", info.Provider);
        Assert.Equal("gemini-3-flash-preview", info.Id);
        Assert.True(info.SupportsVision);
        Assert.True(info.SupportsTools);
        Assert.False(info.SupportsImageOutput);
    }

    [Fact]
    public void GetModelInfo_FindsGemini3Flash_ByLegacyAlias()
    {
        var info = ModelCatalog.GetModelInfo("gemini-3.0-flash-preview");
        Assert.NotNull(info);
        Assert.Equal("gemini-3-flash-preview", info.Id);
    }

    [Fact]
    public void GetModelInfo_FindsGemini25Pro_ById()
    {
        var info = ModelCatalog.GetModelInfo("gemini-2.5-pro");
        Assert.NotNull(info);
        Assert.Equal("gemini", info.Provider);
        Assert.Equal("gemini-2.5-pro", info.Id);
        Assert.Equal("Gemini 2.5 Pro", info.DisplayName);
        Assert.Equal(1_048_576, info.ContextWindow);
        Assert.Equal(65_536, info.MaxOutput);
        Assert.True(info.SupportsReasoning == true);
    }

    [Fact]
    public void GetModelInfo_Gemini25Pro_PreservesUnknownCapabilitiesAndCostAsNull()
    {
        var info = ModelCatalog.GetModelInfo("gemini-2.5-pro");
        Assert.NotNull(info);
        Assert.Null(info.SupportsTools);
        Assert.Null(info.SupportsVision);
        Assert.Null(info.InputCostPerMillion);
        Assert.Null(info.OutputCostPerMillion);
    }

    [Fact]
    public async Task ModelRegistry_LoadSnapshot_MergesDiscoveryCacheAndOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jc_model_registry_{Guid.NewGuid():N}");
        var paths = new ModelRegistryPaths(
            RootDirectory: root,
            DiscoveryDirectory: Path.Combine(root, "discovery"),
            OverridesPath: Path.Combine(root, "model-registry.overrides.json"));
        Directory.CreateDirectory(paths.DiscoveryDirectory);

        try
        {
            await ModelRegistry.WriteDiscoveryCacheAsync(
                "openai",
                [
                    new ProviderModelDescriptor(
                        Provider: "openai",
                        Id: "gpt-5.4-mini",
                        DisplayName: "GPT-5.4 Mini",
                        ContextWindow: 200_000,
                        MaxOutput: 16_384,
                        SupportsTools: true,
                        SupportsVision: true,
                        SupportsReasoning: true)
                ],
                TimeSpan.FromHours(12),
                paths);

            var overrides = new ModelRegistryOverrideFile(
                [
                    new ModelInfo(
                        Id: "gpt-5.4",
                        Provider: "openai",
                        DisplayName: "GPT-5.4",
                        ContextWindow: 200_000,
                        MaxOutput: 32_768,
                        SupportsTools: true,
                        SupportsVision: true,
                        SupportsReasoning: true,
                        InputCostPerMillion: null,
                        OutputCostPerMillion: null,
                        ExpectedLatencyMs: 333)
                ]);
            File.WriteAllText(paths.OverridesPath, JsonSerializer.Serialize(overrides, SnakeCaseJson));

            var snapshot = ModelRegistry.LoadSnapshot(paths);

            var discovered = snapshot.Models.Single(model => model.Id == "gpt-5.4-mini");
            Assert.Equal("openai", discovered.Provider);

            var overridden = snapshot.Models.Single(model => model.Id == "gpt-5.4");
            Assert.Equal(333, overridden.ExpectedLatencyMs);
            Assert.Contains(snapshot.Sources, source => source.SourceType == "discovery_cache" && source.Status == "loaded");
            Assert.Contains(snapshot.Sources, source => source.SourceType == "override_file" && source.Status == "loaded");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ModelRegistry_LoadSnapshot_SkipsExpiredDiscoveryCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jc_model_registry_expired_{Guid.NewGuid():N}");
        var paths = new ModelRegistryPaths(
            RootDirectory: root,
            DiscoveryDirectory: Path.Combine(root, "discovery"),
            OverridesPath: Path.Combine(root, "model-registry.overrides.json"));
        Directory.CreateDirectory(paths.DiscoveryDirectory);

        try
        {
            var expiredCache = new ModelRegistryDiscoveryCache(
                Provider: "openai",
                FetchedAtUtc: DateTimeOffset.UtcNow.AddDays(-2).ToString("o"),
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                Models:
                [
                    new ProviderModelDescriptor(
                        Provider: "openai",
                        Id: "gpt-expired-preview",
                        DisplayName: "Expired Preview")
                ]);
            File.WriteAllText(
                Path.Combine(paths.DiscoveryDirectory, "openai.json"),
                JsonSerializer.Serialize(expiredCache, SnakeCaseJson));

            var snapshot = ModelRegistry.LoadSnapshot(paths);

            Assert.DoesNotContain(snapshot.Models, model => model.Id == "gpt-expired-preview");
            Assert.Contains(snapshot.Sources, source => source.SourceType == "discovery_cache" && source.Status == "skipped_stale");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ModelRegistry_LoadSnapshot_PreservesBuiltInRoutingOrder_AheadOfDiscoveryOnlyModels()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jc_model_registry_order_{Guid.NewGuid():N}");
        var paths = new ModelRegistryPaths(
            RootDirectory: root,
            DiscoveryDirectory: Path.Combine(root, "discovery"),
            OverridesPath: Path.Combine(root, "model-registry.overrides.json"));
        Directory.CreateDirectory(paths.DiscoveryDirectory);

        try
        {
            await ModelRegistry.WriteDiscoveryCacheAsync(
                "openai",
                [
                    new ProviderModelDescriptor(
                        Provider: "openai",
                        Id: "a-openai-preview",
                        DisplayName: "A OpenAI Preview")
                ],
                TimeSpan.FromHours(12),
                paths);

            var snapshot = ModelRegistry.LoadSnapshot(paths);
            var openaiModels = snapshot.Models.Where(model => model.Provider == "openai").ToList();

            Assert.NotEmpty(openaiModels);
            Assert.Equal("gpt-5.2", openaiModels[0].Id);
            Assert.Equal("a-openai-preview", openaiModels[^1].Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
        Assert.Null(req.OutputModalities);
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

// ── QA Plan Tests T11, T12, T13 ────────────────────────────────────────────

public class T11_StreamingMiddlewareTests
{
    [Fact]
    public async Task StreamAsync_AppliesMiddleware_ToTransformRequest()
    {
        var capturingProvider = new ModelCapturingProvider();
        var providers = new Dictionary<string, IProviderAdapter>
        {
            ["test"] = capturingProvider
        };

        // Middleware that transforms the model name
        var middleware = new List<Func<Request, Func<Request, Task<Response>>, Task<Response>>>
        {
            async (req, next) =>
            {
                var modified = req with { Model = "transformed-model" };
                return await next(modified);
            }
        };

        var client = new Client(providers, middleware: middleware);

        var request = new Request
        {
            Model = "original-model",
            Messages = new List<Message> { Message.UserMsg("test") }
        };

        // Consume the stream
        await foreach (var evt in client.StreamAsync(request))
        {
            // Just consume
        }

        // The provider should have received the transformed model
        Assert.Equal("transformed-model", capturingProvider.LastReceivedModel);
    }

    [Fact]
    public async Task StreamAsync_RunsMiddleware_AroundTheFullStreamLifecycle()
    {
        var providerLog = new List<string>();
        var provider = new LoggingStreamingProvider(providerLog);
        var middlewareLog = new List<string>();
        var middleware = new List<Func<Request, Func<Request, Task<Response>>, Task<Response>>>
        {
            async (req, next) =>
            {
                middlewareLog.Add("mw-before");
                var response = await next(req);
                middlewareLog.Add("mw-after");
                return response;
            }
        };

        var client = new Client(
            new Dictionary<string, IProviderAdapter> { ["test"] = provider },
            middleware: middleware);

        await foreach (var _ in client.StreamAsync(new Request
        {
            Model = "stream-model",
            Provider = "test",
            Messages = [Message.UserMsg("hi")]
        }))
        {
        }

        Assert.Equal(["mw-before", "mw-after"], middlewareLog);
        Assert.Equal(["provider-start", "provider-finish"], providerLog);
    }
}

public class T12_AnthropicCacheBreakpointTests
{
    [Fact]
    public void AnthropicAdapter_AddsCacheBreakpoint_ToLastToolResult()
    {
        // We test this by creating an adapter and inspecting the request body via reflection
        var adapter = new AnthropicAdapter("test-key");

        // Build a request with tool results in history
        var request = new Request
        {
            Model = "claude-sonnet-4-6",
            Messages = new List<Message>
            {
                Message.SystemMsg("system prompt"),
                Message.UserMsg("user message"),
                new Message(Role.Assistant, new List<ContentPart>
                {
                    ContentPart.ToolCallPart(new ToolCallData("tc1", "tool1", "{}")),
                }),
                Message.ToolResultMsg("tc1", "first result", false),
                new Message(Role.Assistant, new List<ContentPart>
                {
                    ContentPart.ToolCallPart(new ToolCallData("tc2", "tool2", "{}")),
                }),
                Message.ToolResultMsg("tc2", "second result", false),
            }
        };

        // Use reflection to call the private BuildRequestBody method
        var method = typeof(AnthropicAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        // Find the last tool_result message and check for cache_control
        var messages = body!["messages"]!.AsArray();
        System.Text.Json.Nodes.JsonObject? lastToolResult = null;
        foreach (var msg in messages)
        {
            var content = msg?["content"]?.AsArray();
            if (content is not null)
            {
                foreach (var block in content)
                {
                    if (block?["type"]?.GetValue<string>() == "tool_result")
                        lastToolResult = block.AsObject();
                }
            }
        }

        Assert.NotNull(lastToolResult);
        Assert.NotNull(lastToolResult!["cache_control"]);
        Assert.Equal("ephemeral", lastToolResult["cache_control"]!["type"]!.GetValue<string>());
    }
}

public class T13_ProviderOptionsPassThroughTests
{
    [Fact]
    public void OpenAIAdapter_MergesProviderOptions_IntoBody()
    {
        var adapter = new OpenAIAdapter("test-key");
        var request = new Request
        {
            Model = "gpt-5.2",
            Messages = new List<Message> { Message.UserMsg("test") },
            ProviderOptions = new Dictionary<string, object> { ["store"] = true }
        };

        var method = typeof(OpenAIAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("store"));
    }

    [Fact]
    public void OpenAIAdapter_EmitsXHighReasoningEffort()
    {
        var adapter = new OpenAIAdapter("test-key");
        var request = new Request
        {
            Model = "gpt-5.4",
            Messages = new List<Message> { Message.UserMsg("test") },
            ReasoningEffort = "xhigh"
        };

        var method = typeof(OpenAIAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);
        Assert.Equal("xhigh", body!["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAIAdapter_EmitsItemsSchema_ForArrayToolParameters()
    {
        var adapter = new OpenAIAdapter("test-key");
        var request = new Request
        {
            Model = "gpt-5.2",
            Messages = new List<Message> { Message.UserMsg("test") },
            Tools = new List<ToolDefinition>
            {
                new(
                    "queue_validation_check",
                    "Registers a validation check.",
                    new List<ToolParameter>
                    {
                        new("paths", "array", "Multiple path targets.", false, ItemsType: "string")
                    })
            }
        };

        var method = typeof(OpenAIAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var tools = body!["tools"]!.AsArray();
        var parameters = tools[0]!["parameters"]!.AsObject();
        var paths = parameters["properties"]!["paths"]!.AsObject();

        Assert.NotNull(paths["items"]);
        Assert.Equal("string", paths["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_MergesProviderOptions_IntoBody()
    {
        var adapter = new GeminiAdapter("test-key");
        var request = new Request
        {
            Model = "gemini-2.5-pro",
            Messages = new List<Message> { Message.UserMsg("test") },
            ProviderOptions = new Dictionary<string, object> { ["customSetting"] = "value" }
        };

        var method = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("customSetting"));
    }

    [Fact]
    public void GeminiAdapter_PreservesFunctionCallIdsAndSignatures_InRequestBody()
    {
        var adapter = new GeminiAdapter("test-key");
        var request = new Request
        {
            Model = "gemini-3-flash-preview",
            Messages = new List<Message>
            {
                Message.UserMsg("Create the file."),
                new Message(Role.Assistant, new List<ContentPart>
                {
                    ContentPart.ToolCallPart(new ToolCallData(
                        "call-123",
                        "write_file",
                        "{\"path\":\"demo.txt\",\"content\":\"hello\"}",
                        Signature: "sig-abc"))
                }),
                Message.ToolResultMsg("call-123", "{\"ok\":true}", false)
            }
        };

        var method = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var contents = body!["contents"]!.AsArray();
        Assert.Equal(3, contents.Count);

        var assistantPart = contents[1]!["parts"]![0]!;
        Assert.Equal("call-123", assistantPart["functionCall"]!["id"]!.GetValue<string>());
        Assert.Equal("write_file", assistantPart["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("sig-abc", assistantPart["thoughtSignature"]!.GetValue<string>());

        var toolResponse = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("call-123", toolResponse["id"]!.GetValue<string>());
        Assert.Equal("write_file", toolResponse["name"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_ParsesFunctionCallIdsAndSignatures_FromResponse()
    {
        var adapter = new GeminiAdapter("test-key");
        var responseBody = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "functionCall": {
                          "id": "call-123",
                          "name": "write_file",
                          "args": {
                            "path": "demo.txt",
                            "content": "hello"
                          }
                        },
                        "thoughtSignature": "sig-abc"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 10,
                "candidatesTokenCount": 5
              }
            }
            """;

        var method = typeof(GeminiAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var response = method!.Invoke(adapter, new object[] { responseBody, "gemini-3-flash-preview" }) as Response;
        Assert.NotNull(response);
        Assert.Single(response!.ToolCalls);
        Assert.Equal("call-123", response.ToolCalls[0].Id);
        Assert.Equal("write_file", response.ToolCalls[0].Name);
        Assert.Equal("sig-abc", response.ToolCalls[0].Signature);
    }
}

public class T14_MultimodalAdapterTests
{
    [Fact]
    public void OpenAIAdapter_AddsImageGenerationTool_WhenImageOutputRequested()
    {
        var adapter = new OpenAIAdapter("test-key");
        var request = new Request
        {
            Model = "gpt-5.4",
            Messages = new List<Message> { Message.UserMsg("Generate an image") },
            OutputModalities = new List<ResponseModality> { ResponseModality.Text, ResponseModality.Image }
        };

        var method = typeof(OpenAIAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var tools = body!["tools"]!.AsArray();
        Assert.Contains(tools, tool => tool?["type"]?.GetValue<string>() == "image_generation");
    }

    [Fact]
    public void OpenAIAdapter_UsesImagesApiBody_ForGptImage2Generation()
    {
        var adapter = new OpenAIAdapter("test-key");
        var request = new Request
        {
            Model = "gpt-image-2",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Draw a character reference sheet."),
                    ContentPart.DocumentPart(DocumentData.FromBytes(
                        System.Text.Encoding.UTF8.GetBytes("# Style\nInk-heavy manga linework."),
                        "text/markdown",
                        "style.md")))
            ],
            OutputModalities = new List<ResponseModality> { ResponseModality.Text, ResponseModality.Image }
        };

        var method = typeof(OpenAIAdapter).GetMethod("BuildImagesApiRequest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var imageRequest = method!.Invoke(adapter, new object[] { request });
        Assert.NotNull(imageRequest);
        var jsonBody = imageRequest!.GetType().GetProperty("JsonBody")!.GetValue(imageRequest) as System.Text.Json.Nodes.JsonObject;
        var inputImages = imageRequest.GetType().GetProperty("InputImages")!.GetValue(imageRequest) as System.Collections.ICollection;

        Assert.NotNull(jsonBody);
        Assert.NotNull(inputImages);
        Assert.Empty(inputImages!);
        Assert.Equal("gpt-image-2", jsonBody!["model"]!.GetValue<string>());
        Assert.Contains("Draw a character reference sheet.", jsonBody["prompt"]!.GetValue<string>());
        Assert.Contains("Ink-heavy manga linework.", jsonBody["prompt"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAIAdapter_ParsesImageGenerationCall_Output()
    {
        var adapter = new OpenAIAdapter("test-key");
        var responseBody = $$"""
            {
              "id": "resp_123",
              "model": "gpt-5.4",
              "status": "completed",
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "caption" }
                  ]
                },
                {
                  "id": "ig_123",
                  "type": "image_generation_call",
                  "status": "completed",
                  "result": "{{UnifiedLlmTestAssets.TestImageBase64}}"
                }
              ],
              "usage": {
                "input_tokens": 10,
                "output_tokens": 20
              }
            }
            """;

        var method = typeof(OpenAIAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var response = method!.Invoke(adapter, new object[] { responseBody, "gpt-5.4" }) as Response;
        Assert.NotNull(response);
        Assert.Equal("caption", response!.Text);
        var image = Assert.Single(response.Images);
        Assert.NotNull(image.Data);
        Assert.NotNull(image.ProviderState);
        Assert.Equal("openai", image.ProviderState!["provider"]?.GetValue<string>());
        Assert.Equal("image_generation_call", image.ProviderState["source"]?.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", image.ProviderState["image_url"]?.GetValue<string>());
    }

    [Fact]
    public void OpenAIAdapter_ParsesImagesApi_Output()
    {
        var adapter = new OpenAIAdapter("test-key");
        var responseBody = $$"""
            {
              "created": 1776399795,
              "data": [
                {
                  "b64_json": "{{UnifiedLlmTestAssets.TestImageBase64}}",
                  "revised_prompt": "Revised manga prompt"
                }
              ],
              "usage": {
                "input_tokens": 11,
                "output_tokens": 22
              }
            }
            """;

        var method = typeof(OpenAIAdapter).GetMethod("ParseImagesResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var response = method!.Invoke(adapter, new object[] { responseBody, "gpt-image-2" }) as Response;
        Assert.NotNull(response);
        Assert.Equal("Revised manga prompt", response!.Text);
        var image = Assert.Single(response.Images);
        Assert.NotNull(image.Data);
        Assert.NotNull(image.ProviderState);
        Assert.Equal("openai", image.ProviderState!["provider"]?.GetValue<string>());
        Assert.Equal("images_api", image.ProviderState["source"]?.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", image.ProviderState["image_url"]?.GetValue<string>());
        Assert.Equal(33, response.Usage.TotalTokens);
    }

    [Fact]
    public void OpenAIAdapter_ChainsPreviousResponseId_WhenBuildingFollowUpConversation()
    {
        var adapter = new OpenAIAdapter("test-key");
        var responseBody = $$"""
            {
              "id": "resp_123",
              "model": "gpt-5.4",
              "status": "completed",
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "caption" }
                  ]
                },
                {
                  "id": "ig_123",
                  "type": "image_generation_call",
                  "status": "completed",
                  "result": "{{UnifiedLlmTestAssets.TestImageBase64}}"
                }
              ]
            }
            """;

        var parseMethod = typeof(OpenAIAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var buildMethod = typeof(OpenAIAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(parseMethod);
        Assert.NotNull(buildMethod);

        var parsed = parseMethod!.Invoke(adapter, new object[] { responseBody, "gpt-5.4" }) as Response;
        Assert.NotNull(parsed);
        var priorImage = Assert.Single(parsed!.Images);

        var request = new Request
        {
            Model = "gpt-5.4",
            Messages =
            [
                Message.UserMsg("Create the initial image."),
                new Message(Role.Assistant,
                [
                    ContentPart.TextPart("caption"),
                    ContentPart.ImagePart(priorImage)
                ],
                ResponseId: parsed.Id),
                Message.UserMsg("Make it look more geometric.")
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        Assert.Equal("resp_123", body!["previous_response_id"]!.GetValue<string>());
        var input = body["input"]!.AsArray();
        Assert.Single(input);
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("Make it look more geometric.", input[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_AddsResponseModalities_WhenImageOutputRequested()
    {
        var adapter = new GeminiAdapter("test-key");
        var request = new Request
        {
            Model = "gemini-3.1-flash-image-preview",
            Messages = new List<Message> { Message.UserMsg("Generate an image") },
            OutputModalities = new List<ResponseModality> { ResponseModality.Text, ResponseModality.Image }
        };

        var method = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var modalities = body!["generationConfig"]!["responseModalities"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToList();
        Assert.Contains("TEXT", modalities);
        Assert.Contains("IMAGE", modalities);
    }

    [Fact]
    public void GeminiAdapter_ParsesInlineImage_Output()
    {
        var adapter = new GeminiAdapter("test-key");
        var responseBody = $$"""
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "text": "caption" },
                      {
                        "inlineData": {
                          "mimeType": "image/png",
                          "data": "{{UnifiedLlmTestAssets.TestImageBase64}}"
                        }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 10,
                "candidatesTokenCount": 20
              }
            }
            """;

        var method = typeof(GeminiAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var response = method!.Invoke(adapter, new object[] { responseBody, "gemini-3.1-flash-image-preview" }) as Response;
        Assert.NotNull(response);
        Assert.Equal("caption", response!.Text);
        Assert.Single(response.Images);
        Assert.NotNull(response.Images[0].Data);
    }

    [Fact]
    public void GeminiAdapter_ReusesInlineImageProviderState_WhenReplayingConversation()
    {
        var adapter = new GeminiAdapter("test-key");
        var responseBody = $$"""
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "inlineData": {
                          "mimeType": "image/png",
                          "data": "{{UnifiedLlmTestAssets.TestImageBase64}}"
                        },
                        "thoughtSignature": "sig-inline-image"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var parseMethod = typeof(GeminiAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var buildMethod = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(parseMethod);
        Assert.NotNull(buildMethod);

        var parsed = parseMethod!.Invoke(adapter, new object[] { responseBody, "gemini-3.1-flash-image-preview" }) as Response;
        Assert.NotNull(parsed);
        var image = Assert.Single(parsed!.Images);
        Assert.NotNull(image.ProviderState);
        Assert.Equal("gemini", image.ProviderState!["provider"]?.GetValue<string>());
        Assert.Equal("sig-inline-image", image.ProviderState["thoughtSignature"]?.GetValue<string>());

        var request = new Request
        {
            Model = "gemini-3.1-flash-image-preview",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Use the prior image"),
                    ContentPart.ImagePart(image))
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var inlineData = body!["contents"]![0]!["parts"]![1]!["inlineData"];
        Assert.NotNull(inlineData);
        Assert.Equal(UnifiedLlmTestAssets.TestImageBase64, inlineData!["data"]!.GetValue<string>());
        Assert.Equal("sig-inline-image", body["contents"]![0]!["parts"]![1]!["thoughtSignature"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_ReusesFileImageProviderState_WhenReplayingConversation()
    {
        var adapter = new GeminiAdapter("test-key");
        var responseBody = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "fileData": {
                          "mimeType": "image/png",
                          "fileUri": "gs://demo/generated.png"
                        }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var parseMethod = typeof(GeminiAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var buildMethod = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(parseMethod);
        Assert.NotNull(buildMethod);

        var parsed = parseMethod!.Invoke(adapter, new object[] { responseBody, "gemini-3.1-flash-image-preview" }) as Response;
        Assert.NotNull(parsed);
        var image = Assert.Single(parsed!.Images);
        Assert.Equal("gs://demo/generated.png", image.Url);
        Assert.NotNull(image.ProviderState);

        var request = new Request
        {
            Model = "gemini-3.1-flash-image-preview",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Edit the prior file image"),
                    ContentPart.ImagePart(image))
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var fileData = body!["contents"]![0]!["parts"]![1]!["fileData"];
        Assert.NotNull(fileData);
        Assert.Equal("gs://demo/generated.png", fileData!["fileUri"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_ReplaysAssistantTextAndImageThoughtSignatures_WhenReplayingConversation()
    {
        var adapter = new GeminiAdapter("test-key");
        var responseBody = $$"""
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "text": "caption",
                        "thoughtSignature": "sig-text"
                      },
                      {
                        "inlineData": {
                          "mimeType": "image/png",
                          "data": "{{UnifiedLlmTestAssets.TestImageBase64}}"
                        },
                        "thoughtSignature": "sig-image"
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var parseMethod = typeof(GeminiAdapter).GetMethod("ParseResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var buildMethod = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(parseMethod);
        Assert.NotNull(buildMethod);

        var parsed = parseMethod!.Invoke(adapter, new object[] { responseBody, "gemini-3.1-flash-image-preview" }) as Response;
        Assert.NotNull(parsed);

        var request = new Request
        {
            Model = "gemini-3.1-flash-image-preview",
            Messages =
            [
                parsed!.Message,
                Message.UserMsg("Refine the earlier image.")
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var assistantParts = body!["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("caption", assistantParts[0]!["text"]!.GetValue<string>());
        Assert.Equal("sig-text", assistantParts[0]!["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("sig-image", assistantParts[1]!["thoughtSignature"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_BuildsDocumentAndAudioInlineParts()
    {
        var adapter = new GeminiAdapter("test-key");
        var buildMethod = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(buildMethod);

        var request = new Request
        {
            Model = "gemini-2.5-pro",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the brief and listen to the note."),
                    ContentPart.DocumentPart(DocumentData.FromBytes([0x25, 0x50, 0x44, 0x46], "application/pdf", "brief.pdf")),
                    ContentPart.AudioPart(AudioData.FromBytes([0x49, 0x44, 0x33], "audio/mpeg", "notes.mp3")))
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var parts = body!["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("application/pdf", parts[1]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("audio/mpeg", parts[2]!["inlineData"]!["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_ReusesInlineDocumentProviderState_WhenReplayingConversation()
    {
        var adapter = new GeminiAdapter("test-key");
        var buildMethod = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(buildMethod);
        var providerState = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "provider": "gemini",
              "inlineData": {
                "mimeType": "application/pdf",
                "data": "JVBERg=="
              }
            }
            """)!.AsObject();
        var document = DocumentData.FromBytes(
            [0x25, 0x50, 0x44, 0x46],
            "application/pdf",
            "brief.pdf",
            providerState);

        var request = new Request
        {
            Model = "gemini-2.5-pro",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Use the prior brief."),
                    ContentPart.DocumentPart(document))
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var inlineData = body!["contents"]![0]!["parts"]![1]!["inlineData"];
        Assert.NotNull(inlineData);
        Assert.Equal("application/pdf", inlineData!["mimeType"]!.GetValue<string>());
        Assert.Equal("JVBERg==", inlineData["data"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiAdapter_ReusesFileAudioProviderState_WhenReplayingConversation()
    {
        var adapter = new GeminiAdapter("test-key");
        var buildMethod = typeof(GeminiAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(buildMethod);
        var providerState = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "provider": "gemini",
              "fileData": {
                "mimeType": "audio/mpeg",
                "fileUri": "gs://demo/notes.mp3"
              }
            }
            """)!.AsObject();
        var audio = AudioData.FromUrl(
            "gs://demo/notes.mp3",
            "audio/mpeg",
            "notes.mp3",
            providerState);

        var request = new Request
        {
            Model = "gemini-2.5-pro",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Use the prior note."),
                    ContentPart.AudioPart(audio))
            ]
        };

        var body = buildMethod!.Invoke(adapter, new object[] { request }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var fileData = body!["contents"]![0]!["parts"]![1]!["fileData"];
        Assert.NotNull(fileData);
        Assert.Equal("audio/mpeg", fileData!["mimeType"]!.GetValue<string>());
        Assert.Equal("gs://demo/notes.mp3", fileData["fileUri"]!.GetValue<string>());
    }

    [Fact]
    public void AnthropicAdapter_Throws_WhenImageOutputRequested()
    {
        var adapter = new AnthropicAdapter("test-key");
        var request = new Request
        {
            Model = "claude-opus-4-6",
            Messages = new List<Message> { Message.UserMsg("Generate an image") },
            OutputModalities = new List<ResponseModality> { ResponseModality.Image }
        };

        var method = typeof(AnthropicAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.Invoke(adapter, new object[] { request, false }));
        Assert.IsType<ConfigurationError>(ex.InnerException);
    }

    [Fact]
    public void AnthropicAdapter_AddsDocumentBlock_WhenPdfInputRequested()
    {
        var adapter = new AnthropicAdapter("test-key");
        var request = new Request
        {
            Model = "claude-opus-4-6",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the attached PDF."),
                    ContentPart.DocumentPart(DocumentData.FromBytes([0x25, 0x50, 0x44, 0x46], "application/pdf", "brief.pdf")))
            ]
        };

        var method = typeof(AnthropicAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var content = body!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("document", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("base64", content[1]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("application/pdf", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("brief.pdf", content[1]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void AnthropicAdapter_AddsPlainTextDocumentBlock_WhenTextDocumentRequested()
    {
        var adapter = new AnthropicAdapter("test-key");
        var request = new Request
        {
            Model = "claude-opus-4-6",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the attached notes."),
                    ContentPart.DocumentPart(DocumentData.FromBytes(System.Text.Encoding.UTF8.GetBytes("alpha\nbeta"), "text/markdown", "brief.md")))
            ]
        };

        var method = typeof(AnthropicAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var content = body!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("document", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("text/plain", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("alpha\nbeta", content[1]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void AnthropicAdapter_ReplaysPriorUserDocument_WhenBuildingFollowUpConversation()
    {
        var adapter = new AnthropicAdapter("test-key");
        var request = new Request
        {
            Model = "claude-opus-4-6",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the attached brief."),
                    ContentPart.DocumentPart(DocumentData.FromBytes([0x25, 0x50, 0x44, 0x46], "application/pdf", "brief.pdf"))),
                new Message(Role.Assistant, [ContentPart.TextPart("I reviewed the brief.")]),
                Message.UserMsg("What was the main risk in the prior brief?")
            ]
        };

        var method = typeof(AnthropicAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var messages = body!["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("document", messages[0]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("brief.pdf", messages[0]!["content"]![1]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAIAdapter_AddsInputFilePart_WhenDocumentInputRequested()
    {
        var adapter = new OpenAIAdapter("test-key");
        var request = new Request
        {
            Model = "gpt-5.4",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the attached brief."),
                    ContentPart.DocumentPart(DocumentData.FromBytes([0x25, 0x50, 0x44, 0x46], "application/pdf", "brief.pdf")))
            ]
        };

        var method = typeof(OpenAIAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var body = method!.Invoke(adapter, new object[] { request, false }) as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(body);

        var content = body!["input"]![0]!["content"]!.AsArray();
        Assert.Equal("input_file", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("brief.pdf", content[1]!["filename"]!.GetValue<string>());
        Assert.StartsWith("data:application/pdf;base64,", content[1]!["file_data"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAIAdapter_UsesChatCompletions_ForAudioInputRequests()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "id": "chatcmpl_123",
                  "model": "gpt-audio",
                  "choices": [
                    {
                      "message": {
                        "role": "assistant",
                        "content": "Transcription complete."
                      },
                      "finish_reason": "stop"
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 10,
                    "completion_tokens": 4,
                    "total_tokens": 14
                  }
                }
                """)
        });
        var adapter = new OpenAIAdapter("test-key", httpClient: new HttpClient(handler));
        var request = new Request
        {
            Model = "gpt-audio",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the attached brief and note."),
                    ContentPart.DocumentPart(DocumentData.FromBytes([0x25, 0x50, 0x44, 0x46], "application/pdf", "brief.pdf")),
                    ContentPart.AudioPart(AudioData.FromBytes([0x49, 0x44, 0x33], "audio/mpeg", "notes.mp3")))
            ]
        };

        var response = await adapter.CompleteAsync(request);

        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Equal("Transcription complete.", response.Text);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("file", content[1].GetProperty("type").GetString());
        Assert.Equal("brief.pdf", content[1].GetProperty("file").GetProperty("filename").GetString());
        Assert.Equal("input_audio", content[2].GetProperty("type").GetString());
        Assert.Equal("mp3", content[2].GetProperty("input_audio").GetProperty("format").GetString());
    }

    [Fact]
    public void AnthropicAdapter_Throws_WhenAudioInputRequested()
    {
        var adapter = new AnthropicAdapter("test-key");
        var request = new Request
        {
            Model = "claude-opus-4-6",
            Messages =
            [
                Message.UserMsg(
                    ContentPart.TextPart("Review the spoken note."),
                    ContentPart.AudioPart(AudioData.FromBytes([0x49, 0x44, 0x33], "audio/mpeg", "notes.mp3")))
            ]
        };

        var method = typeof(AnthropicAdapter).GetMethod("BuildRequestBody",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.Invoke(adapter, new object[] { request, false }));
        Assert.IsType<ConfigurationError>(ex.InnerException);
    }

    [Fact]
    public void ModelCapabilityValidator_FailsClosed_WhenImageOutputRequestedOnTextOnlyModel()
    {
        var ex = Assert.Throws<CapabilityValidationError>(() =>
            ModelCapabilityValidator.ValidateResolvedCodergenSelection(
                "openai",
                "gpt-5.3-codex",
                reasoningEffort: null,
                new CodergenCapabilityRequirements(
                    ExecutionLane: "multimodal_leaf",
                    OutputModalities: [ResponseModality.Image])));

        Assert.Equal("image_output", ex.Capability);
        Assert.Equal("gpt-5.3-codex", ex.ModelId);
    }

    [Fact]
    public void ModelCapabilityValidator_FailsClosed_WhenDocumentInputRequestedOnUnsupportedModel()
    {
        var ex = Assert.Throws<CapabilityValidationError>(() =>
            ModelCapabilityValidator.ValidateResolvedCodergenSelection(
                "openai",
                "gpt-audio",
                reasoningEffort: null,
                new CodergenCapabilityRequirements(RequireDocumentInput: true)));

        Assert.Equal("document_input", ex.Capability);
        Assert.Equal("gpt-audio", ex.ModelId);
    }

    [Fact]
    public void ModelCapabilityValidator_FailsClosed_WhenAudioInputRequestedOnUnsupportedModel()
    {
        var ex = Assert.Throws<CapabilityValidationError>(() =>
            ModelCapabilityValidator.ValidateResolvedCodergenSelection(
                "openai",
                "gpt-5.4",
                reasoningEffort: null,
                new CodergenCapabilityRequirements(RequireAudioInput: true)));

        Assert.Equal("audio_input", ex.Capability);
        Assert.Equal("gpt-5.4", ex.ModelId);
    }

    [Fact]
    public void ModelCapabilityValidator_AllowsDocumentInput_OnAnthropicModel()
    {
        ModelCapabilityValidator.ValidateResolvedCodergenSelection(
            "anthropic",
            "claude-opus-4-6",
            reasoningEffort: null,
            new CodergenCapabilityRequirements(RequireDocumentInput: true));
    }

    [Fact]
    public void ModelCapabilityValidator_FailsClosed_WhenAudioInputRequestedOnAnthropicModel()
    {
        var ex = Assert.Throws<CapabilityValidationError>(() =>
            ModelCapabilityValidator.ValidateResolvedCodergenSelection(
                "anthropic",
                "claude-opus-4-6",
                reasoningEffort: null,
                new CodergenCapabilityRequirements(RequireAudioInput: true)));

        Assert.Equal("audio_input", ex.Capability);
        Assert.Equal("claude-opus-4-6", ex.ModelId);
    }

    [Fact]
    public void ModelCapabilityValidator_AllowsAudioInput_OnGptAudio()
    {
        ModelCapabilityValidator.ValidateResolvedCodergenSelection(
            "openai",
            "gpt-audio",
            reasoningEffort: null,
            new CodergenCapabilityRequirements(RequireAudioInput: true));
    }
}

// ── T11 test helper ─────────────────────────────────────────────────────────

internal class ModelCapturingProvider : IProviderAdapter
{
    public string Name => "capturing";
    public string? LastReceivedModel { get; private set; }

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        LastReceivedModel = request.Model;
        return Task.FromResult(new Response("id", request.Model, Name,
            Message.AssistantMsg("ok"), FinishReason.Stop, Usage.Empty));
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(Request request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        LastReceivedModel = request.Model;
        yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = "ok" };
        yield return new StreamEvent { Type = StreamEventType.Finish, FinishReason = FinishReason.Stop };
        await Task.CompletedTask;
    }
}

internal sealed class LoggingStreamingProvider : IProviderAdapter
{
    private readonly List<string> _log;

    public LoggingStreamingProvider(List<string> log)
    {
        _log = log;
    }

    public string Name => "logging-stream";

    public Task<Response> CompleteAsync(Request request, CancellationToken ct = default) =>
        Task.FromResult(new Response("id", request.Model, Name, Message.AssistantMsg("ok"), FinishReason.Stop, Usage.Empty));

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Request request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _log.Add("provider-start");
        yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = "ok" };
        await Task.Yield();
        _log.Add("provider-finish");
        yield return new StreamEvent { Type = StreamEventType.Finish, FinishReason = FinishReason.Stop };
    }
}

internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public Uri? LastRequestUri { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastMethod = request.Method;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return _responder(request);
    }
}

internal static partial class UnifiedLlmTestAssets
{
    public const string TestImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR42mP4b2xMEmIY1TCqYfhqAACXHWUQdfT2ygAAAABJRU5ErkJggg==";
}
