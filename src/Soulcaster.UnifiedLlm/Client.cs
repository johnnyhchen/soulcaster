using System.Text;
using System.Threading.Channels;

namespace Soulcaster.UnifiedLlm;

/// <summary>
/// Core client that routes requests to the appropriate provider adapter,
/// optionally applying middleware.
/// </summary>
public sealed class Client
{
    private readonly Dictionary<string, IProviderAdapter> _providers;
    private readonly string? _defaultProvider;
    private readonly List<Func<Request, Func<Request, Task<Response>>, Task<Response>>>? _middleware;

    /// <summary>
    /// Creates a new Client with explicit providers and optional configuration.
    /// </summary>
    /// <param name="providers">Dictionary of provider name to adapter instance.</param>
    /// <param name="defaultProvider">
    /// Name of the default provider used when a request does not specify one.
    /// If null the first registered provider is used.
    /// </param>
    /// <param name="middleware">
    /// Optional middleware chain. Each function receives the request and a "next" delegate,
    /// allowing pre/post processing.
    /// </param>
    public Client(
        Dictionary<string, IProviderAdapter> providers,
        string? defaultProvider = null,
        List<Func<Request, Func<Request, Task<Response>>, Task<Response>>>? middleware = null)
    {
        if (providers is null || providers.Count == 0)
            throw new ConfigurationError("At least one provider must be registered.");

        _providers = new Dictionary<string, IProviderAdapter>(providers, StringComparer.OrdinalIgnoreCase);
        _defaultProvider = defaultProvider;
        _middleware = middleware;

        // Validate default provider
        if (_defaultProvider is not null && !_providers.ContainsKey(_defaultProvider))
            throw new ConfigurationError($"Default provider '{_defaultProvider}' is not in the registered providers.");
    }

    /// <summary>
    /// Registered provider names.
    /// </summary>
    public IReadOnlyCollection<string> Providers => _providers.Keys;

    // ── Factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Client from environment variables.
    /// Reads ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY.
    /// </summary>
    public static Client FromEnv()
    {
        var providers = new Dictionary<string, IProviderAdapter>(StringComparer.OrdinalIgnoreCase);

        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(anthropicKey))
            providers["anthropic"] = new AnthropicAdapter(anthropicKey);

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiKey))
            providers["openai"] = new OpenAIAdapter(openAiKey);

        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey))
            providers["gemini"] = new GeminiAdapter(geminiKey);

        if (providers.Count == 0)
            throw new ConfigurationError(
                "No API keys found in environment. " +
                "Set at least one of: ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY.");

        // Default priority: anthropic > openai > gemini
        string? defaultProvider = null;
        if (providers.ContainsKey("anthropic")) defaultProvider = "anthropic";
        else if (providers.ContainsKey("openai")) defaultProvider = "openai";
        else if (providers.ContainsKey("gemini")) defaultProvider = "gemini";

        return new Client(providers, defaultProvider);
    }

    // ── CompleteAsync ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a completion request to the appropriate provider, applying any middleware.
    /// </summary>
    public async Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        var normalizedRequest = NormalizeRequest(request);
        var provider = GetProviderForRequest(normalizedRequest);
        var handler = BuildMiddlewarePipeline(req => provider.CompleteAsync(req, ct));
        return await handler(normalizedRequest).ConfigureAwait(false);
    }

    // ── StreamAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Streams a completion from the appropriate provider.
    /// Middleware is applied around the stream initiation — each middleware
    /// can transform the request before it reaches the provider.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Request request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedRequest = NormalizeRequest(request);
        var provider = GetProviderForRequest(normalizedRequest);
        var channel = Channel.CreateUnbounded<StreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamToken = streamCts.Token;
        var text = new StringBuilder();
        var usage = Usage.Empty;
        FinishReason? finishReason = null;
        Response? finalResponse = null;

        var handler = BuildMiddlewarePipeline(async req =>
        {
            await foreach (var evt in provider.StreamAsync(req, streamToken).ConfigureAwait(false))
            {
                if (evt.Type == StreamEventType.TextDelta && evt.Delta is not null)
                    text.Append(evt.Delta);

                if (evt.Usage is not null)
                    usage += evt.Usage;

                if (evt.FinishReason is not null)
                    finishReason = evt.FinishReason;

                if (evt.Response is not null)
                    finalResponse = evt.Response;

                await channel.Writer.WriteAsync(evt, streamToken).ConfigureAwait(false);
            }

            return finalResponse ?? new Response(
                Id: Guid.NewGuid().ToString(),
                Model: req.Model,
                Provider: provider.Name,
                Message: Message.AssistantMsg(text.ToString()),
                FinishReason: finishReason ?? FinishReason.Stop,
                Usage: usage);
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                await handler(normalizedRequest).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(streamToken).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            streamCts.Cancel();
        }

        await producer.ConfigureAwait(false);
    }

    // ── Provider resolution ────────────────────────────────────────────

    /// <summary>
    /// Resolves the provider adapter for a given request.
    /// Uses the request's Provider property, falling back to the default provider.
    /// </summary>
    internal IProviderAdapter GetProviderForRequest(Request request)
    {
        var providerName = request.Provider;

        // If no provider specified, try to infer from model name
        if (string.IsNullOrEmpty(providerName))
        {
            providerName = InferProviderFromModel(request.Model) ?? _defaultProvider;
        }

        if (string.IsNullOrEmpty(providerName))
        {
            // Fall back to first registered provider
            providerName = _providers.Keys.FirstOrDefault()
                ?? throw new ConfigurationError("No providers registered.");
        }

        if (!_providers.TryGetValue(providerName, out var provider))
            throw new ConfigurationError($"Provider '{providerName}' is not registered. Available: {string.Join(", ", _providers.Keys)}");

        return provider;
    }

    /// <summary>
    /// Resolves a model alias to its canonical ID via the ModelCatalog.
    /// Returns the original model string if no alias match is found.
    /// </summary>
    public static string ResolveModelAlias(string model)
    {
        if (string.IsNullOrEmpty(model)) return model;
        var info = ModelCatalog.GetModelInfo(model);
        return info?.Id ?? model;
    }

    /// <summary>
    /// Attempts to infer the provider from the model name.
    /// </summary>
    private string? InferProviderFromModel(string model)
    {
        if (string.IsNullOrEmpty(model)) return null;

        var lower = model.ToLowerInvariant();

        // Anthropic models
        if (lower.StartsWith("claude") && _providers.ContainsKey("anthropic"))
            return "anthropic";

        // OpenAI models
        if ((lower.StartsWith("gpt") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4") || lower.StartsWith("codex"))
            && _providers.ContainsKey("openai"))
            return "openai";

        // Gemini models
        if (lower.StartsWith("gemini") && _providers.ContainsKey("gemini"))
            return "gemini";

        return null;
    }

    private static Request NormalizeRequest(Request request)
    {
        var resolvedModel = ResolveModelAlias(request.Model);
        return resolvedModel == request.Model
            ? request
            : request with { Model = resolvedModel };
    }

    private Func<Request, Task<Response>> BuildMiddlewarePipeline(Func<Request, Task<Response>> terminalHandler)
    {
        var handler = terminalHandler;
        if (_middleware is null)
            return handler;

        for (var i = _middleware.Count - 1; i >= 0; i--)
        {
            var mw = _middleware[i];
            var next = handler;
            handler = req => mw(req, next);
        }

        return handler;
    }
}
