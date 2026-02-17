using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JcAttractor.UnifiedLlm;

public sealed class AnthropicAdapter : IProviderAdapter
{
    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public string Name => "anthropic";

    public AnthropicAdapter(string apiKey, string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient();
    }

    // ── CompleteAsync ──────────────────────────────────────────────────

    public async Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: false);
        using var httpReq = CreateHttpRequest(request, body);
        using var httpRes = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        var responseBody = await httpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccess(httpRes, responseBody);
        return ParseResponse(responseBody);
    }

    // ── StreamAsync ────────────────────────────────────────────────────

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Request request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: true);
        using var httpReq = CreateHttpRequest(request, body);
        using var httpRes = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!httpRes.IsSuccessStatusCode)
        {
            var errorBody = await httpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureSuccess(httpRes, errorBody);
        }

        using var stream = await httpRes.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? currentEvent = null;
        var dataBuffer = new System.Text.StringBuilder();
        var textAccum = new System.Text.StringBuilder();
        var reasoningAccum = new System.Text.StringBuilder();
        var toolCalls = new List<(string id, string name, System.Text.StringBuilder args)>();
        Usage? finalUsage = null;
        string? responseId = null;
        string? responseModel = null;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            if (line.StartsWith("event: "))
            {
                currentEvent = line["event: ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data: "))
            {
                dataBuffer.Append(line["data: ".Length..]);
                continue;
            }

            if (line.Length == 0 && currentEvent is not null && dataBuffer.Length > 0)
            {
                var data = dataBuffer.ToString();
                dataBuffer.Clear();

                JsonNode? node = null;
                try { node = JsonNode.Parse(data); }
                catch { /* skip unparseable */ }

                if (node is null)
                {
                    currentEvent = null;
                    continue;
                }

                switch (currentEvent)
                {
                    case "message_start":
                        responseId = node["message"]?["id"]?.GetValue<string>();
                        responseModel = node["message"]?["model"]?.GetValue<string>();
                        var startUsage = node["message"]?["usage"];
                        if (startUsage is not null)
                        {
                            finalUsage = ParseUsage(startUsage);
                        }
                        yield return new StreamEvent { Type = StreamEventType.StreamStart };
                        break;

                    case "content_block_start":
                        var blockType = node["content_block"]?["type"]?.GetValue<string>();
                        if (blockType == "text")
                        {
                            var initialText = node["content_block"]?["text"]?.GetValue<string>() ?? "";
                            yield return new StreamEvent { Type = StreamEventType.TextStart, Delta = initialText };
                            textAccum.Append(initialText);
                        }
                        else if (blockType == "thinking")
                        {
                            yield return new StreamEvent { Type = StreamEventType.ReasoningStart };
                        }
                        else if (blockType == "tool_use")
                        {
                            var toolId = node["content_block"]?["id"]?.GetValue<string>() ?? "";
                            var toolName = node["content_block"]?["name"]?.GetValue<string>() ?? "";
                            toolCalls.Add((toolId, toolName, new System.Text.StringBuilder()));
                            yield return new StreamEvent
                            {
                                Type = StreamEventType.ToolCallStart,
                                ToolCall = new ToolCallData(toolId, toolName, "")
                            };
                        }
                        break;

                    case "content_block_delta":
                        var deltaType = node["delta"]?["type"]?.GetValue<string>();
                        if (deltaType == "text_delta")
                        {
                            var text = node["delta"]?["text"]?.GetValue<string>() ?? "";
                            textAccum.Append(text);
                            yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = text };
                        }
                        else if (deltaType == "thinking_delta")
                        {
                            var thinking = node["delta"]?["thinking"]?.GetValue<string>() ?? "";
                            reasoningAccum.Append(thinking);
                            yield return new StreamEvent { Type = StreamEventType.ReasoningDelta, ReasoningDelta = thinking };
                        }
                        else if (deltaType == "input_json_delta")
                        {
                            var json = node["delta"]?["partial_json"]?.GetValue<string>() ?? "";
                            if (toolCalls.Count > 0)
                                toolCalls[^1].args.Append(json);
                            yield return new StreamEvent { Type = StreamEventType.ToolCallDelta, Delta = json };
                        }
                        break;

                    case "content_block_stop":
                        // Determine which block just stopped based on what's active
                        // The API sends index - use it if present
                        break;

                    case "message_delta":
                        var stopReason = node["delta"]?["stop_reason"]?.GetValue<string>();
                        var deltaUsage = node["usage"];
                        if (deltaUsage is not null)
                        {
                            var usg = ParseUsage(deltaUsage);
                            finalUsage = finalUsage is not null ? finalUsage + usg : usg;
                        }
                        break;

                    case "message_stop":
                        // Build the final response
                        var parts = new List<ContentPart>();
                        if (reasoningAccum.Length > 0)
                            parts.Add(ContentPart.ThinkingPart(new ThinkingData(reasoningAccum.ToString(), null, false)));
                        if (textAccum.Length > 0)
                            parts.Add(ContentPart.TextPart(textAccum.ToString()));
                        foreach (var tc in toolCalls)
                            parts.Add(ContentPart.ToolCallPart(new ToolCallData(tc.id, tc.name, tc.args.ToString())));

                        var finishReason = toolCalls.Count > 0 ? FinishReason.ToolCalls : FinishReason.Stop;
                        var msg = new Message(Role.Assistant, parts);
                        var resp = new Response(
                            responseId ?? "",
                            responseModel ?? request.Model,
                            Name,
                            msg,
                            finishReason,
                            finalUsage ?? Usage.Empty);

                        yield return new StreamEvent
                        {
                            Type = StreamEventType.Finish,
                            FinishReason = finishReason,
                            Usage = finalUsage,
                            Response = resp
                        };
                        break;

                    case "error":
                        var errMsg = node["error"]?["message"]?.GetValue<string>() ?? "Unknown streaming error";
                        yield return new StreamEvent
                        {
                            Type = StreamEventType.Error,
                            Error = new ProviderError(errMsg, HttpStatusCode.InternalServerError, providerName: Name)
                        };
                        break;
                }

                currentEvent = null;
            }
        }
    }

    // ── Request building ───────────────────────────────────────────────

    private JsonObject BuildRequestBody(Request request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = stream
        };

        if (request.MaxTokens is not null)
            body["max_tokens"] = request.MaxTokens.Value;
        else
            body["max_tokens"] = 8192; // Anthropic requires max_tokens

        if (request.Temperature is not null)
            body["temperature"] = request.Temperature.Value;

        if (request.TopP is not null)
            body["top_p"] = request.TopP.Value;

        if (request.StopSequences is not null && request.StopSequences.Count > 0)
        {
            var stops = new JsonArray();
            foreach (var s in request.StopSequences) stops.Add(s);
            body["stop_sequences"] = stops;
        }

        // Extract system messages
        var systemParts = new List<JsonNode>();
        var messages = new JsonArray();

        foreach (var msg in request.Messages)
        {
            if (msg.Role == Role.System)
            {
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ContentKind.Text && part.Text is not null)
                    {
                        var sysPart = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = part.Text
                        };
                        // Add cache_control for prompt caching on the last system block
                        sysPart["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                        systemParts.Add(sysPart);
                    }
                }
                continue;
            }

            if (msg.Role == Role.Tool)
            {
                // Tool results are sent as user messages with tool_result content blocks
                var toolContent = new JsonArray();
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
                    {
                        var resultBlock = new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = part.ToolResult.ToolCallId,
                            ["content"] = part.ToolResult.Content
                        };
                        if (part.ToolResult.IsError)
                            resultBlock["is_error"] = true;
                        toolContent.Add(resultBlock);
                    }
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = toolContent
                });
                continue;
            }

            var role = msg.Role switch
            {
                Role.User => "user",
                Role.Assistant => "assistant",
                Role.Developer => "user",
                _ => "user"
            };

            var content = BuildContentArray(msg);
            messages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content
            });
        }

        if (systemParts.Count > 0)
        {
            var sysArray = new JsonArray();
            foreach (var sp in systemParts) sysArray.Add(sp);
            body["system"] = sysArray;
        }

        body["messages"] = messages;

        // Tools
        if (request.Tools is not null && request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var param in tool.Parameters)
                {
                    var prop = new JsonObject { ["type"] = param.Type };
                    if (param.Description is not null)
                        prop["description"] = param.Description;
                    properties[param.Name] = prop;
                    if (param.Required)
                        required.Add(param.Name);
                }

                var inputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required
                };

                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = inputSchema
                });
            }
            body["tools"] = tools;
        }

        // Tool choice
        if (request.ToolChoice is not null)
        {
            body["tool_choice"] = request.ToolChoice.Mode switch
            {
                ToolChoiceMode.Auto => new JsonObject { ["type"] = "auto" },
                ToolChoiceMode.None => new JsonObject { ["type"] = "none" },
                ToolChoiceMode.Required => new JsonObject { ["type"] = "any" },
                ToolChoiceMode.Named => new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = request.ToolChoice.ToolName
                },
                _ => new JsonObject { ["type"] = "auto" }
            };
        }

        // Reasoning / extended thinking
        if (request.ReasoningEffort is not null)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = request.ReasoningEffort.ToLowerInvariant() switch
                {
                    "low" => 2048,
                    "medium" => 8192,
                    "high" => 32768,
                    _ => int.TryParse(request.ReasoningEffort, out var n) ? n : 8192
                }
            };
        }

        // Metadata
        if (request.Metadata is not null && request.Metadata.Count > 0)
        {
            var meta = new JsonObject();
            foreach (var kv in request.Metadata) meta[kv.Key] = kv.Value;
            body["metadata"] = meta;
        }

        return body;
    }

    private static JsonArray BuildContentArray(Message msg)
    {
        var content = new JsonArray();

        foreach (var part in msg.Content)
        {
            switch (part.Kind)
            {
                case ContentKind.Text when part.Text is not null:
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = part.Text
                    });
                    break;

                case ContentKind.Image when part.Image is not null:
                    if (part.Image.Url is not null)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "url",
                                ["url"] = part.Image.Url
                            }
                        });
                    }
                    else if (part.Image.Data is not null)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = part.Image.MediaType ?? "image/png",
                                ["data"] = Convert.ToBase64String(part.Image.Data)
                            }
                        });
                    }
                    break;

                case ContentKind.Thinking when part.Thinking is not null:
                    if (part.Thinking.Redacted)
                    {
                        content.Add(new JsonObject
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = part.Thinking.Text
                        });
                    }
                    else
                    {
                        var thinkingBlock = new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = part.Thinking.Text
                        };
                        if (part.Thinking.Signature is not null)
                            thinkingBlock["signature"] = part.Thinking.Signature;
                        content.Add(thinkingBlock);
                    }
                    break;

                case ContentKind.ToolCall when part.ToolCall is not null:
                    JsonNode? inputNode;
                    try { inputNode = JsonNode.Parse(part.ToolCall.Arguments); }
                    catch { inputNode = new JsonObject(); }

                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = part.ToolCall.Id,
                        ["name"] = part.ToolCall.Name,
                        ["input"] = inputNode
                    });
                    break;
            }
        }

        return content;
    }

    // ── HTTP helpers ───────────────────────────────────────────────────

    private HttpRequestMessage CreateHttpRequest(Request request, JsonObject body)
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", ApiVersion);

        // Beta headers from provider_options
        if (request.ProviderOptions is not null)
        {
            if (request.ProviderOptions.TryGetValue("anthropic-beta", out var beta))
                httpReq.Headers.Add("anthropic-beta", beta?.ToString());
        }

        return httpReq;
    }

    private void EnsureSuccess(HttpResponseMessage httpRes, string responseBody)
    {
        if (httpRes.IsSuccessStatusCode) return;

        var status = httpRes.StatusCode;
        var message = $"Anthropic API error ({(int)status})";

        try
        {
            var node = JsonNode.Parse(responseBody);
            var errMsg = node?["error"]?["message"]?.GetValue<string>();
            if (errMsg is not null) message = errMsg;
        }
        catch { /* use default message */ }

        TimeSpan? retryAfter = null;
        if (httpRes.Headers.TryGetValues("retry-after", out var retryValues))
        {
            var val = retryValues.FirstOrDefault();
            if (val is not null && int.TryParse(val, out var seconds))
                retryAfter = TimeSpan.FromSeconds(seconds);
        }

        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AuthenticationError(message, status, providerName: Name, responseBody: responseBody),
            HttpStatusCode.TooManyRequests =>
                new RateLimitError(message, retryAfter, providerName: Name, responseBody: responseBody),
            HttpStatusCode.NotFound =>
                new NotFoundError(message, providerName: Name, responseBody: responseBody),
            (HttpStatusCode)529 => // Anthropic overloaded
                new ProviderError(message, status, retryable: true, retryAfter: retryAfter, providerName: Name, responseBody: responseBody),
            _ =>
                new ProviderError(message, status, retryable: (int)status >= 500, providerName: Name, responseBody: responseBody)
        };
    }

    // ── Response parsing ───────────────────────────────────────────────

    private Response ParseResponse(string responseBody)
    {
        var node = JsonNode.Parse(responseBody)
            ?? throw new ProviderError("Empty response from Anthropic", HttpStatusCode.InternalServerError, providerName: Name);

        var id = node["id"]?.GetValue<string>() ?? "";
        var model = node["model"]?.GetValue<string>() ?? "";
        var stopReason = node["stop_reason"]?.GetValue<string>();

        var content = new List<ContentPart>();
        var contentArray = node["content"]?.AsArray();
        if (contentArray is not null)
        {
            foreach (var block in contentArray)
            {
                if (block is null) continue;
                var type = block["type"]?.GetValue<string>();

                switch (type)
                {
                    case "text":
                        content.Add(ContentPart.TextPart(block["text"]?.GetValue<string>() ?? ""));
                        break;

                    case "thinking":
                        var thinkingText = block["thinking"]?.GetValue<string>() ?? "";
                        var signature = block["signature"]?.GetValue<string>();
                        content.Add(ContentPart.ThinkingPart(new ThinkingData(thinkingText, signature, false)));
                        break;

                    case "redacted_thinking":
                        var redactedData = block["data"]?.GetValue<string>() ?? "";
                        content.Add(ContentPart.ThinkingPart(new ThinkingData(redactedData, null, true)));
                        break;

                    case "tool_use":
                        var toolId = block["id"]?.GetValue<string>() ?? "";
                        var toolName = block["name"]?.GetValue<string>() ?? "";
                        var inputJson = block["input"]?.ToJsonString() ?? "{}";
                        content.Add(ContentPart.ToolCallPart(new ToolCallData(toolId, toolName, inputJson)));
                        break;
                }
            }
        }

        var finishReason = MapFinishReason(stopReason);
        var usage = ParseUsage(node["usage"]);

        var msg = new Message(Role.Assistant, content);

        // Parse rate limit headers are not in the JSON body; we handle this for streaming in the future
        return new Response(id, model, Name, msg, finishReason, usage);
    }

    private static FinishReason MapFinishReason(string? reason) => reason switch
    {
        "end_turn" => FinishReason.Stop,
        "stop_sequence" => new FinishReason("stop", reason),
        "max_tokens" => FinishReason.Length,
        "tool_use" => FinishReason.ToolCalls,
        _ => FinishReason.Stop
    };

    private static Usage ParseUsage(JsonNode? usage)
    {
        if (usage is null) return Usage.Empty;

        var input = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var output = usage["output_tokens"]?.GetValue<int>() ?? 0;
        var cacheRead = usage["cache_read_input_tokens"]?.GetValue<int>();
        var cacheWrite = usage["cache_creation_input_tokens"]?.GetValue<int>();

        return new Usage(
            InputTokens: input,
            OutputTokens: output,
            TotalTokens: input + output,
            CacheReadTokens: cacheRead is > 0 ? cacheRead : null,
            CacheWriteTokens: cacheWrite is > 0 ? cacheWrite : null);
    }
}
