using System.Net;
using Soulcaster.UnifiedLlm;

namespace Soulcaster.Tests;

public class ProviderDiscoveryAdapterTests
{
    [Fact]
    public async Task OpenAIDiscovery_ListModels_ParsesModelIds_AndPingCountsModels()
    {
        var handler = new StubHttpMessageHandler(
            _ => JsonResponse("""
{
  "data": [
    { "id": "gpt-5.4-mini", "object": "model" },
    { "id": "text-embedding-3-small", "object": "model" }
  ]
}
"""),
            _ => JsonResponse("""
{
  "data": [
    { "id": "gpt-5.4-mini", "object": "model" },
    { "id": "text-embedding-3-small", "object": "model" }
  ]
}
"""));
        var adapter = new OpenAIAdapter("test-key", httpClient: new HttpClient(handler));

        var models = await adapter.ListModelsAsync();
        var ping = await adapter.PingAsync();

        Assert.Equal(["gpt-5.4-mini", "text-embedding-3-small"], models.Select(model => model.Id).ToArray());
        Assert.True(ping.Success);
        Assert.Equal(2, ping.ModelCount);
        Assert.Equal("Bearer test-key", handler.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task AnthropicDiscovery_ListModels_FollowsPagination()
    {
        var handler = new StubHttpMessageHandler(
            _ => JsonResponse("""
{
  "data": [
    { "id": "claude-sonnet-4-7", "display_name": "Claude Sonnet 4.7" }
  ],
  "has_more": true
}
"""),
            _ => JsonResponse("""
{
  "data": [
    { "id": "claude-haiku-4-6", "display_name": "Claude Haiku 4.6" }
  ],
  "has_more": false
}
"""));
        var adapter = new AnthropicAdapter("test-key", httpClient: new HttpClient(handler));

        var models = await adapter.ListModelsAsync();

        Assert.Equal(["claude-haiku-4-6", "claude-sonnet-4-7"], models.Select(model => model.Id).ToArray());
        Assert.Equal("Claude Sonnet 4.7", models.Single(model => model.Id == "claude-sonnet-4-7").DisplayName);
        Assert.Contains("after_id=claude-sonnet-4-7", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiDiscovery_ListModels_NormalizesName_AndReadsTokenLimits()
    {
        var handler = new StubHttpMessageHandler(
            _ => JsonResponse("""
{
  "models": [
    {
      "name": "models/gemini-3.1-pro",
      "displayName": "Gemini 3.1 Pro",
      "inputTokenLimit": 1048576,
      "outputTokenLimit": 65536,
      "thinking": true
    }
  ]
}
"""));
        var adapter = new GeminiAdapter("test-key", httpClient: new HttpClient(handler));

        var models = await adapter.ListModelsAsync();

        var model = Assert.Single(models);
        Assert.Equal("gemini-3.1-pro", model.Id);
        Assert.Equal("Gemini 3.1 Pro", model.DisplayName);
        Assert.Equal(1_048_576, model.ContextWindow);
        Assert.Equal(65_536, model.MaxOutput);
        Assert.True(model.SupportsReasoning);
    }

    [Fact]
    public async Task PingAsync_ReturnsFailureDetails_WhenProviderReturnsHttpError()
    {
        var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""
{
  "error": {
    "message": "bad api key"
  }
}
""")
            });
        var adapter = new OpenAIAdapter("bad-key", httpClient: new HttpClient(handler));

        var ping = await adapter.PingAsync();

        Assert.False(ping.Success);
        Assert.Equal(401, ping.StatusCode);
        Assert.Equal("bad api key", ping.Message);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public StubHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public List<ObservedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("No queued response for request.");

            Requests.Add(new ObservedRequest(
                request.Method,
                request.RequestUri ?? new Uri("http://localhost"),
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase)));

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed record ObservedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers);
}
