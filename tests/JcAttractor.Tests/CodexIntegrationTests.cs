using JcAttractor.UnifiedLlm;

namespace JcAttractor.Tests;

/// <summary>
/// Integration tests for codex-5.2 model support.
/// The live API test is skipped when OPENAI_API_KEY is not set.
/// </summary>
public class CodexIntegrationTests
{
    private static string? ApiKey => Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    [Fact]
    public async Task Client_InfersOpenAiProvider_ForCodexModel()
    {
        var openai = new FakeProvider("openai");
        var anthropic = new FakeProvider("anthropic");
        var client = new Client(new Dictionary<string, IProviderAdapter>
        {
            ["openai"] = openai,
            ["anthropic"] = anthropic
        });

        await client.CompleteAsync(new Request
        {
            Model = "codex-5.2",
            Messages = [Message.UserMsg("hi")]
        });

        Assert.True(openai.CallCount > 0, "Expected codex-5.2 to route to OpenAI provider");
        Assert.Equal(0, anthropic.CallCount);
    }

    [Fact]
    public void ResolveModelAlias_ResolvesCodex52()
    {
        var resolved = Client.ResolveModelAlias("codex-5.2");
        Assert.Equal("gpt-5.2-codex", resolved);
    }

    [Fact]
    public void ResolveModelAlias_PassesThroughCanonicalId()
    {
        var resolved = Client.ResolveModelAlias("gpt-5.2-codex");
        Assert.Equal("gpt-5.2-codex", resolved);
    }

    [SkippableFact]
    public async Task Codex52_SendTrivialRequest_GetsResponse()
    {
        var apiKey = ApiKey;
        Skip.If(string.IsNullOrEmpty(apiKey), "OPENAI_API_KEY not set — skipping live API test.");

        var adapter = new OpenAiAdapter(apiKey!);
        var request = new Request
        {
            Model = Client.ResolveModelAlias("codex-5.2"),
            Messages = [Message.UserMsg("Reply with exactly: hello world")],
            MaxTokens = 64
        };

        var response = await adapter.CompleteAsync(request);

        Assert.NotNull(response);
        Assert.Equal("openai", response.Provider);
        Assert.False(string.IsNullOrWhiteSpace(response.Text), "Expected non-empty text response");
        Assert.True(response.Usage.TotalTokens > 0, "Expected non-zero token usage");
    }
}
