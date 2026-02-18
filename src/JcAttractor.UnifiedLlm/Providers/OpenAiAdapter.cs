using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JcAttractor.UnifiedLlm;

public sealed class OpenAiAdapter : IProviderAdapter
{
    private const string DefaultBaseUrl = "https://api.openai.com";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public string Name => "openai";

    public OpenAiAdapter(string apiKey, string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient();
    }

    // ── CompleteAsync ──────────────────────────────────────────────────

    public async Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: false);
        using var httpReq = CreateHttpRequest(body);
        using var httpRes = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        var responseBody = await httpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccess(httpRes, responseBody);
        return ParseResponse(responseBody, request.Model);
    }

    // ── StreamAsync ────────────────────────────────────────────────────

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        Request request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request, stream: true);
        using var httpReq = CreateHttpRequest(body);
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
        var textAccum = new System.Text.StringBuilder();
        var reasoningAccum = new System.Text.StringBuilder();
        var toolCalls = new Dictionary<int, (string id, string name, System.Text.StringBuilder args)>();
        Usage? finalUsage = null;
        string? responseId = null;
        string? responseModel = null;
        FinishReason? finishReason = null;
        bool started = false;

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

            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }
            if (node is null) continue;

            var eventType = currentEvent ?? node["type"]?.GetValue<string>() ?? "";

            switch (eventType)
            {
                case "response.created":
                    responseId = node["response"]?["id"]?.GetValue<string>();
                    responseModel = node["response"]?["model"]?.GetValue<string>();
                    if (!started)
                    {
                        started = true;
                        yield return new StreamEvent { Type = StreamEventType.StreamStart };
                    }
                    break;

                case "response.output_item.added":
                    var itemType = node["item"]?["type"]?.GetValue<string>();
                    if (itemType == "message")
                    {
                        yield return new StreamEvent { Type = StreamEventType.TextStart };
                    }
                    else if (itemType == "function_call")
                    {
                        var idx = node["output_index"]?.GetValue<int>() ?? toolCalls.Count;
                        var callId = node["item"]?["call_id"]?.GetValue<string>() ?? "";
                        var funcName = node["item"]?["name"]?.GetValue<string>() ?? "";
                        toolCalls[idx] = (callId, funcName, new System.Text.StringBuilder());
                        yield return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallStart,
                            ToolCall = new ToolCallData(callId, funcName, "")
                        };
                    }
                    else if (itemType == "reasoning")
                    {
                        yield return new StreamEvent { Type = StreamEventType.ReasoningStart };
                    }
                    break;

                case "response.output_text.delta":
                    var textDelta = node["delta"]?.GetValue<string>() ?? "";
                    textAccum.Append(textDelta);
                    yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = textDelta };
                    break;

                case "response.reasoning.delta":
                case "response.reasoning_summary_text.delta":
                    var reasonDelta = node["delta"]?.GetValue<string>() ?? "";
                    reasoningAccum.Append(reasonDelta);
                    yield return new StreamEvent { Type = StreamEventType.ReasoningDelta, ReasoningDelta = reasonDelta };
                    break;

                case "response.function_call_arguments.delta":
                    var argDelta = node["delta"]?.GetValue<string>() ?? "";
                    var argIdx = node["output_index"]?.GetValue<int>() ?? 0;
                    if (toolCalls.TryGetValue(argIdx, out var tc))
                        tc.args.Append(argDelta);
                    yield return new StreamEvent { Type = StreamEventType.ToolCallDelta, Delta = argDelta };
                    break;

                case "response.output_text.done":
                    yield return new StreamEvent { Type = StreamEventType.TextEnd };
                    break;

                case "response.function_call_arguments.done":
                    var doneIdx = node["output_index"]?.GetValue<int>() ?? 0;
                    if (toolCalls.TryGetValue(doneIdx, out var doneTc))
                    {
                        yield return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallEnd,
                            ToolCall = new ToolCallData(doneTc.id, doneTc.name, doneTc.args.ToString())
                        };
                    }
                    break;

                case "response.completed":
                case "response.done":
                    var respNode = node["response"];
                    if (respNode is not null)
                    {
                        responseId ??= respNode["id"]?.GetValue<string>();
                        responseModel ??= respNode["model"]?.GetValue<string>();
                        var usageNode = respNode["usage"];
                        if (usageNode is not null)
                            finalUsage = ParseUsage(usageNode);

                        var status = respNode["status"]?.GetValue<string>();
                        finishReason = MapFinishReason(status, toolCalls.Count > 0);
                    }

                    // Build final response
                    var parts = new List<ContentPart>();
                    if (reasoningAccum.Length > 0)
                        parts.Add(ContentPart.ThinkingPart(new ThinkingData(reasoningAccum.ToString(), null, false)));
                    if (textAccum.Length > 0)
                        parts.Add(ContentPart.TextPart(textAccum.ToString()));
                    foreach (var kvp in toolCalls.OrderBy(k => k.Key))
                        parts.Add(ContentPart.ToolCallPart(new ToolCallData(kvp.Value.id, kvp.Value.name, kvp.Value.args.ToString())));

                    var fr = finishReason ?? (toolCalls.Count > 0 ? FinishReason.ToolCalls : FinishReason.Stop);
                    var msg = new Message(Role.Assistant, parts);
                    var resp = new Response(
                        responseId ?? "",
                        responseModel ?? request.Model,
                        Name,
                        msg,
                        fr,
                        finalUsage ?? Usage.Empty);

                    yield return new StreamEvent
                    {
                        Type = StreamEventType.Finish,
                        FinishReason = fr,
                        Usage = finalUsage,
                        Response = resp
                    };
                    break;

                case "error":
                    var errMsg = node["error"]?["message"]?.GetValue<string>()
                        ?? node["message"]?.GetValue<string>()
                        ?? "Unknown streaming error";
                    yield return new StreamEvent
                    {
                        Type = StreamEventType.Error,
                        Error = new ProviderError(errMsg, HttpStatusCode.InternalServerError, providerName: Name)
                    };
                    break;

                default:
                    // Forward unknown events as provider events
                    yield return new StreamEvent
                    {
                        Type = StreamEventType.ProviderEvent,
                        Raw = new Dictionary<string, object>
                        {
                            ["event"] = eventType,
                            ["data"] = data
                        }
                    };
                    break;
            }

            currentEvent = null;
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

        // Build input (messages array)
        var input = new JsonArray();
        foreach (var msg in request.Messages)
        {
            var role = msg.Role switch
            {
                Role.System => "system",
                Role.User => "user",
                Role.Assistant => "assistant",
                Role.Developer => "developer",
                Role.Tool => "tool",
                _ => "user"
            };

            if (msg.Role == Role.Tool)
            {
                // Tool results in the Responses API
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = part.ToolResult.ToolCallId,
                            ["output"] = part.ToolResult.Content
                        });
                    }
                }
                continue;
            }

            if (msg.Role == Role.Assistant)
            {
                // Assistant messages may contain tool calls
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ContentKind.Text && part.Text is not null)
                    {
                        input.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = part.Text
                        });
                    }
                    else if (part.Kind == ContentKind.ToolCall && part.ToolCall is not null)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = part.ToolCall.Id,
                            ["name"] = part.ToolCall.Name,
                            ["arguments"] = part.ToolCall.Arguments
                        });
                    }
                }
                continue;
            }

            // Simple text-based messages
            if (msg.Content.Count == 1 && msg.Content[0].Kind == ContentKind.Text)
            {
                input.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = msg.Content[0].Text
                });
            }
            else
            {
                // Multi-part content
                var contentArray = new JsonArray();
                foreach (var part in msg.Content)
                {
                    switch (part.Kind)
                    {
                        case ContentKind.Text when part.Text is not null:
                            contentArray.Add(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = part.Text
                            });
                            break;

                        case ContentKind.Image when part.Image is not null:
                            if (part.Image.Url is not null)
                            {
                                contentArray.Add(new JsonObject
                                {
                                    ["type"] = "input_image",
                                    ["image_url"] = part.Image.Url
                                });
                            }
                            else if (part.Image.Data is not null)
                            {
                                var mimeType = part.Image.MediaType ?? "image/png";
                                var b64 = Convert.ToBase64String(part.Image.Data);
                                contentArray.Add(new JsonObject
                                {
                                    ["type"] = "input_image",
                                    ["image_url"] = $"data:{mimeType};base64,{b64}"
                                });
                            }
                            break;
                    }
                }
                input.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = contentArray
                });
            }
        }

        body["input"] = input;

        // Max tokens
        if (request.MaxTokens is not null)
            body["max_output_tokens"] = request.MaxTokens.Value;

        // Temperature
        if (request.Temperature is not null)
            body["temperature"] = request.Temperature.Value;

        // Top P
        if (request.TopP is not null)
            body["top_p"] = request.TopP.Value;

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
                    var propType = param.Required
                        ? param.Type
                        : param.Type;
                    var prop = new JsonObject { ["type"] = new JsonArray(param.Type, "null") };
                    if (param.Description is not null)
                        prop["description"] = param.Description;
                    if (param.Required)
                        prop["type"] = param.Type;
                    properties[param.Name] = prop;
                    // OpenAI strict mode requires all properties in required
                    required.Add(param.Name);
                }

                var parameters = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false
                };

                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters,
                    ["strict"] = true
                });
            }
            body["tools"] = tools;
        }

        // Tool choice
        if (request.ToolChoice is not null)
        {
            body["tool_choice"] = request.ToolChoice.Mode switch
            {
                ToolChoiceMode.Auto => "auto",
                ToolChoiceMode.None => "none",
                ToolChoiceMode.Required => "required",
                ToolChoiceMode.Named => JsonValue.Create(request.ToolChoice.ToolName),
                _ => "auto"
            };
        }

        // Reasoning effort
        if (request.ReasoningEffort is not null)
        {
            body["reasoning"] = new JsonObject
            {
                ["effort"] = request.ReasoningEffort.ToLowerInvariant()
            };
        }

        // Response format
        if (request.ResponseFormat is not null)
        {
            if (request.ResponseFormat.Type == "json_schema" && request.ResponseFormat.JsonSchema is not null)
            {
                var schemaJson = JsonSerializer.SerializeToNode(request.ResponseFormat.JsonSchema);
                body["text"] = new JsonObject
                {
                    ["format"] = new JsonObject
                    {
                        ["type"] = "json_schema",
                        ["schema"] = schemaJson,
                        ["strict"] = request.ResponseFormat.Strict
                    }
                };
            }
            else if (request.ResponseFormat.Type == "json_object")
            {
                body["text"] = new JsonObject
                {
                    ["format"] = new JsonObject { ["type"] = "json_object" }
                };
            }
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

    // ── HTTP helpers ───────────────────────────────────────────────────

    private HttpRequestMessage CreateHttpRequest(JsonObject body)
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/responses")
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        return httpReq;
    }

    private void EnsureSuccess(HttpResponseMessage httpRes, string responseBody)
    {
        if (httpRes.IsSuccessStatusCode) return;

        var status = httpRes.StatusCode;
        var message = $"OpenAI API error ({(int)status})";

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
            _ =>
                new ProviderError(message, status, retryable: (int)status >= 500, providerName: Name, responseBody: responseBody)
        };
    }

    // ── Response parsing ───────────────────────────────────────────────

    private Response ParseResponse(string responseBody, string requestModel)
    {
        var node = JsonNode.Parse(responseBody)
            ?? throw new ProviderError("Empty response from OpenAI", HttpStatusCode.InternalServerError, providerName: Name);

        var id = node["id"]?.GetValue<string>() ?? "";
        var model = node["model"]?.GetValue<string>() ?? requestModel;
        var status = node["status"]?.GetValue<string>();

        var content = new List<ContentPart>();

        // Parse output array
        var outputArray = node["output"]?.AsArray();
        if (outputArray is not null)
        {
            foreach (var item in outputArray)
            {
                if (item is null) continue;
                var type = item["type"]?.GetValue<string>();

                switch (type)
                {
                    case "message":
                        var messageContent = item["content"]?.AsArray();
                        if (messageContent is not null)
                        {
                            foreach (var block in messageContent)
                            {
                                if (block is null) continue;
                                var blockType = block["type"]?.GetValue<string>();
                                if (blockType == "output_text")
                                {
                                    content.Add(ContentPart.TextPart(block["text"]?.GetValue<string>() ?? ""));
                                }
                            }
                        }
                        break;

                    case "function_call":
                        var callId = item["call_id"]?.GetValue<string>() ?? "";
                        var funcName = item["name"]?.GetValue<string>() ?? "";
                        var arguments = item["arguments"]?.GetValue<string>() ?? "{}";
                        content.Add(ContentPart.ToolCallPart(new ToolCallData(callId, funcName, arguments)));
                        break;

                    case "reasoning":
                        var summaryArray = item["summary"]?.AsArray();
                        if (summaryArray is not null)
                        {
                            foreach (var summary in summaryArray)
                            {
                                if (summary is null) continue;
                                var summaryText = summary["text"]?.GetValue<string>();
                                if (summaryText is not null)
                                    content.Add(ContentPart.ThinkingPart(new ThinkingData(summaryText, null, false)));
                            }
                        }
                        break;
                }
            }
        }

        var hasToolCalls = content.Any(c => c.Kind == ContentKind.ToolCall);
        var finishReason = MapFinishReason(status, hasToolCalls);
        var usage = ParseUsage(node["usage"]);

        var msg = new Message(Role.Assistant, content.Count > 0 ? content : [ContentPart.TextPart("")]);

        return new Response(id, model, Name, msg, finishReason, usage);
    }

    private static FinishReason MapFinishReason(string? status, bool hasToolCalls)
    {
        if (hasToolCalls) return FinishReason.ToolCalls;

        return status switch
        {
            "completed" => FinishReason.Stop,
            "incomplete" => FinishReason.Length,
            "failed" => FinishReason.Error,
            _ => FinishReason.Stop
        };
    }

    private static Usage ParseUsage(JsonNode? usage)
    {
        if (usage is null) return Usage.Empty;

        var input = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var output = usage["output_tokens"]?.GetValue<int>() ?? 0;

        int? reasoningTokens = null;
        var outputDetails = usage["output_tokens_details"];
        if (outputDetails is not null)
        {
            reasoningTokens = outputDetails["reasoning_tokens"]?.GetValue<int>();
        }

        int? cacheRead = null;
        var inputDetails = usage["input_tokens_details"];
        if (inputDetails is not null)
        {
            cacheRead = inputDetails["cached_tokens"]?.GetValue<int>();
        }

        return new Usage(
            InputTokens: input,
            OutputTokens: output,
            TotalTokens: input + output,
            ReasoningTokens: reasoningTokens is > 0 ? reasoningTokens : null,
            CacheReadTokens: cacheRead is > 0 ? cacheRead : null);
    }
}
