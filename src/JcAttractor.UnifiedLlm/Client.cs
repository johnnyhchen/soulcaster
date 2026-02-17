namespace JcAttractor.UnifiedLlm;

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
            providers["openai"] = new OpenAiAdapter(openAiKey);

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
        var provider = GetProviderForRequest(request);

        // Build the base handler
        Func<Request, Task<Response>> handler = req => provider.CompleteAsync(req, ct);

        // Apply middleware in reverse order so the first middleware wraps outermost
        if (_middleware is not null)
        {
            for (var i = _middleware.Count - 1; i >= 0; i--)
            {
                var mw = _middleware[i];
                var next = handler;
                handler = req => mw(req, next);
            }
        }

        return await handler(request).ConfigureAwait(false);
    }

    // ── StreamAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Streams a completion from the appropriate provider.
    /// Middleware is not applied to streaming (middleware wraps the non-streaming path).
    /// </summary>
    public IAsyncEnumerable<StreamEvent> StreamAsync(Request request, CancellationToken ct = default)
    {
        var provider = GetProviderForRequest(request);
        return provider.StreamAsync(request, ct);
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
        if ((lower.StartsWith("gpt") || lower.StartsWith("o1") || lower.StartsWith("o3") || lower.StartsWith("o4"))
            && _providers.ContainsKey("openai"))
            return "openai";

        // Gemini models
        if (lower.StartsWith("gemini") && _providers.ContainsKey("gemini"))
            return "gemini";

        return null;
    }
}
