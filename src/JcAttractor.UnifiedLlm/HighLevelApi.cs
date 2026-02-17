using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JcAttractor.UnifiedLlm;

/// <summary>
/// High-level static API for common LLM operations.
/// </summary>
public static class Llm
{
    private static Client? _defaultClient;
    private static readonly object _lock = new();

    /// <summary>
    /// Sets the module-level default client.
    /// </summary>
    public static void SetDefaultClient(Client client)
    {
        lock (_lock)
        {
            _defaultClient = client ?? throw new ArgumentNullException(nameof(client));
        }
    }

    /// <summary>
    /// Gets the default client, lazily initializing from environment variables if needed.
    /// </summary>
    private static Client GetDefaultClient()
    {
        if (_defaultClient is not null) return _defaultClient;

        lock (_lock)
        {
            _defaultClient ??= Client.FromEnv();
            return _defaultClient;
        }
    }

    // ── GenerateAsync ──────────────────────────────────────────────────

    /// <summary>
    /// Generates a completion, optionally executing tool calls in a loop.
    /// </summary>
    /// <param name="request">The completion request.</param>
    /// <param name="maxToolRounds">Maximum number of tool-call round trips (default 10).</param>
    /// <param name="maxRetries">Maximum number of retries on transient errors (default 2).</param>
    /// <param name="client">Optional client override; uses default if null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The final response after all tool calls have been resolved.</returns>
    public static async Task<Response> GenerateAsync(
        Request request,
        int maxToolRounds = 10,
        int maxRetries = 2,
        Client? client = null,
        CancellationToken ct = default)
    {
        client ??= GetDefaultClient();

        var messages = new List<Message>(request.Messages);
        var currentRequest = request;

        for (var round = 0; round <= maxToolRounds; round++)
        {
            var response = await CompleteWithRetriesAsync(client, currentRequest, maxRetries, ct)
                .ConfigureAwait(false);

            // If no tool calls or no tool executors, return immediately
            if (response.FinishReason != FinishReason.ToolCalls || request.Tools is null)
                return response;

            var toolCalls = response.ToolCalls;
            if (toolCalls.Count == 0)
                return response;

            // Check if any tools have Execute delegates
            var executableTools = request.Tools
                .Where(t => t.Execute is not null)
                .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            if (executableTools.Count == 0)
                return response; // No executors registered, return as-is

            // Add assistant message with tool calls
            messages.Add(response.Message);

            // Execute each tool call
            foreach (var toolCall in toolCalls)
            {
                string resultContent;
                bool isError = false;

                if (executableTools.TryGetValue(toolCall.Name, out var toolDef) && toolDef.Execute is not null)
                {
                    try
                    {
                        resultContent = await toolDef.Execute(toolCall.Arguments).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        resultContent = $"Tool execution error: {ex.Message}";
                        isError = true;
                    }
                }
                else
                {
                    resultContent = $"Unknown tool: {toolCall.Name}";
                    isError = true;
                }

                messages.Add(Message.ToolResultMsg(toolCall.Id, resultContent, isError));
            }

            // Build next request with updated messages
            currentRequest = request with { Messages = new List<Message>(messages) };
        }

        throw new SdkException($"Tool execution loop exceeded maximum rounds ({maxToolRounds}).");
    }

    // ── StreamAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Streams a completion from the provider.
    /// </summary>
    public static IAsyncEnumerable<StreamEvent> StreamAsync(
        Request request,
        Client? client = null,
        CancellationToken ct = default)
    {
        client ??= GetDefaultClient();
        return client.StreamAsync(request, ct);
    }

    // ── GenerateObjectAsync ────────────────────────────────────────────

    /// <summary>
    /// Generates a structured JSON object response.
    /// Validates that the output parses as JSON. Retries if parsing fails.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the JSON into.</typeparam>
    /// <param name="request">
    /// The completion request. Should have ResponseFormat set for best results,
    /// but the method will add JSON instructions to the prompt if needed.
    /// </param>
    /// <param name="jsonSerializerOptions">Optional custom JSON serializer options.</param>
    /// <param name="maxAttempts">Maximum number of generation attempts (default 3).</param>
    /// <param name="client">Optional client override.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<T> GenerateObjectAsync<T>(
        Request request,
        JsonSerializerOptions? jsonSerializerOptions = null,
        int maxAttempts = 3,
        Client? client = null,
        CancellationToken ct = default)
    {
        client ??= GetDefaultClient();
        jsonSerializerOptions ??= new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Ensure we're requesting JSON output
        var effectiveRequest = request;
        if (request.ResponseFormat is null)
        {
            effectiveRequest = request with
            {
                ResponseFormat = ResponseFormat.JsonFormat
            };
        }

        string? lastRawOutput = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var response = await client.CompleteAsync(effectiveRequest, ct).ConfigureAwait(false);
            var text = response.Text.Trim();
            lastRawOutput = text;

            // Try to extract JSON if wrapped in markdown code blocks
            text = ExtractJson(text);

            try
            {
                var result = JsonSerializer.Deserialize<T>(text, jsonSerializerOptions);
                if (result is not null)
                    return result;
            }
            catch (JsonException)
            {
                // Will retry
            }

            // On retry, add a clarifying message
            if (attempt < maxAttempts - 1)
            {
                var retryMessages = new List<Message>(effectiveRequest.Messages)
                {
                    Message.AssistantMsg(text),
                    Message.UserMsg(
                        "Your previous response was not valid JSON. " +
                        "Please respond with ONLY a valid JSON object, no markdown or explanation.")
                };
                effectiveRequest = effectiveRequest with { Messages = retryMessages };
            }
        }

        throw new NoObjectGeneratedError(
            $"Failed to generate a valid JSON object after {maxAttempts} attempts.",
            lastRawOutput);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<Response> CompleteWithRetriesAsync(
        Client client,
        Request request,
        int maxRetries,
        CancellationToken ct)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await client.CompleteAsync(request, ct).ConfigureAwait(false);
            }
            catch (ProviderError ex) when (ex.Retryable && attempt < maxRetries)
            {
                lastException = ex;
                var delay = ex.RetryAfter ?? TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 500);
                // Cap delay at 30 seconds
                if (delay > TimeSpan.FromSeconds(30))
                    delay = TimeSpan.FromSeconds(30);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 500);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw lastException ?? new SdkException("All retry attempts exhausted.");
    }

    /// <summary>
    /// Attempts to extract JSON from text that may be wrapped in markdown code fences.
    /// </summary>
    private static string ExtractJson(string text)
    {
        // Try to extract from ```json ... ``` blocks
        var jsonStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonStart >= 0)
        {
            var contentStart = text.IndexOf('\n', jsonStart);
            if (contentStart >= 0)
            {
                var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (end >= 0)
                    return text[(contentStart + 1)..end].Trim();
            }
        }

        // Try to extract from ``` ... ``` blocks
        jsonStart = text.IndexOf("```", StringComparison.Ordinal);
        if (jsonStart >= 0)
        {
            var contentStart = text.IndexOf('\n', jsonStart);
            if (contentStart >= 0)
            {
                var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (end >= 0)
                    return text[(contentStart + 1)..end].Trim();
            }
        }

        return text;
    }
}
