using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JcAttractor.UnifiedLlm;

public sealed class GeminiAdapter : IProviderAdapter
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private int _syntheticCallIdCounter;

    public string Name => "gemini";

    public GeminiAdapter(string apiKey, string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    // ── CompleteAsync ──────────────────────────────────────────────────

    public async Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request);
        var url = $"{_baseUrl}/v1beta/models/{request.Model}:generateContent?key={_apiKey}";

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

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
        var body = BuildRequestBody(request);
        var url = $"{_baseUrl}/v1beta/models/{request.Model}:streamGenerateContent?key={_apiKey}&alt=sse";

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        using var httpRes = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!httpRes.IsSuccessStatusCode)
        {
            var errorBody = await httpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureSuccess(httpRes, errorBody);
        }

        using var stream = await httpRes.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var textAccum = new System.Text.StringBuilder();
        var reasoningAccum = new System.Text.StringBuilder();
        var toolCalls = new List<(string id, string name, string args)>();
        Usage? finalUsage = null;
        bool started = false;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }
            if (node is null) continue;

            if (!started)
            {
                started = true;
                yield return new StreamEvent { Type = StreamEventType.StreamStart };
            }

            // Parse usage metadata
            var usageMeta = node["usageMetadata"];
            if (usageMeta is not null)
                finalUsage = ParseUsage(usageMeta);

            // Parse candidates
            var candidates = node["candidates"]?.AsArray();
            if (candidates is null || candidates.Count == 0) continue;

            var candidate = candidates[0];
            var contentNode = candidate?["content"];
            var partsArray = contentNode?["parts"]?.AsArray();

            if (partsArray is not null)
            {
                foreach (var part in partsArray)
                {
                    if (part is null) continue;

                    // Text part
                    var text = part["text"]?.GetValue<string>();
                    if (text is not null)
                    {
                        // Check if this is a thought
                        var thought = part["thought"]?.GetValue<bool>() ?? false;
                        if (thought)
                        {
                            reasoningAccum.Append(text);
                            yield return new StreamEvent { Type = StreamEventType.ReasoningDelta, ReasoningDelta = text };
                        }
                        else
                        {
                            textAccum.Append(text);
                            yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = text };
                        }
                    }

                    // Function call part
                    var funcCall = part["functionCall"];
                    if (funcCall is not null)
                    {
                        var funcName = funcCall["name"]?.GetValue<string>() ?? "";
                        var argsJson = funcCall["args"]?.ToJsonString() ?? "{}";
                        var callId = GenerateSyntheticCallId();
                        toolCalls.Add((callId, funcName, argsJson));

                        yield return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallStart,
                            ToolCall = new ToolCallData(callId, funcName, argsJson)
                        };
                        yield return new StreamEvent
                        {
                            Type = StreamEventType.ToolCallEnd,
                            ToolCall = new ToolCallData(callId, funcName, argsJson)
                        };
                    }
                }
            }

            // Check for finish reason
            var finishReasonStr = candidate?["finishReason"]?.GetValue<string>();
            if (finishReasonStr is not null && finishReasonStr != "FINISH_REASON_UNSPECIFIED")
            {
                var parts = new List<ContentPart>();
                if (reasoningAccum.Length > 0)
                    parts.Add(ContentPart.ThinkingPart(new ThinkingData(reasoningAccum.ToString(), null, false)));
                if (textAccum.Length > 0)
                    parts.Add(ContentPart.TextPart(textAccum.ToString()));
                foreach (var tc in toolCalls)
                    parts.Add(ContentPart.ToolCallPart(new ToolCallData(tc.id, tc.name, tc.args)));

                var fr = MapFinishReason(finishReasonStr, toolCalls.Count > 0);
                var msg = new Message(Role.Assistant, parts.Count > 0 ? parts : [ContentPart.TextPart("")]);
                var resp = new Response(
                    "", // Gemini streaming doesn't provide a top-level response ID
                    request.Model,
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
            }
        }
    }

    // ── Request building ───────────────────────────────────────────────

    private JsonObject BuildRequestBody(Request request)
    {
        var body = new JsonObject();

        // System instruction - extract system messages
        var systemTexts = new List<string>();
        var conversationMessages = new List<Message>();

        foreach (var msg in request.Messages)
        {
            if (msg.Role == Role.System || msg.Role == Role.Developer)
            {
                var text = msg.Text;
                if (!string.IsNullOrEmpty(text))
                    systemTexts.Add(text);
            }
            else
            {
                conversationMessages.Add(msg);
            }
        }

        if (systemTexts.Count > 0)
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(
                    systemTexts.Select(t => (JsonNode)new JsonObject { ["text"] = t }).ToArray()
                )
            };
        }

        // Contents (conversation messages)
        var contents = new JsonArray();
        foreach (var msg in conversationMessages)
        {
            var geminiRole = msg.Role switch
            {
                Role.User => "user",
                Role.Assistant => "model",
                Role.Tool => "user", // function responses come as user role with functionResponse parts
                _ => "user"
            };

            var parts = new JsonArray();

            if (msg.Role == Role.Tool)
            {
                // Tool results are sent as functionResponse parts
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ContentKind.ToolResult && part.ToolResult is not null)
                    {
                        JsonNode? responseContent;
                        try { responseContent = JsonNode.Parse(part.ToolResult.Content); }
                        catch { responseContent = new JsonObject { ["result"] = part.ToolResult.Content }; }

                        parts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = part.ToolResult.ToolCallId, // Gemini uses the function name, but we map from call ID
                                ["response"] = responseContent
                            }
                        });
                    }
                }
            }
            else
            {
                foreach (var part in msg.Content)
                {
                    switch (part.Kind)
                    {
                        case ContentKind.Text when part.Text is not null:
                            parts.Add(new JsonObject { ["text"] = part.Text });
                            break;

                        case ContentKind.Image when part.Image is not null:
                            if (part.Image.Data is not null)
                            {
                                parts.Add(new JsonObject
                                {
                                    ["inlineData"] = new JsonObject
                                    {
                                        ["mimeType"] = part.Image.MediaType ?? "image/png",
                                        ["data"] = Convert.ToBase64String(part.Image.Data)
                                    }
                                });
                            }
                            else if (part.Image.Url is not null)
                            {
                                parts.Add(new JsonObject
                                {
                                    ["fileData"] = new JsonObject
                                    {
                                        ["mimeType"] = part.Image.MediaType ?? "image/png",
                                        ["fileUri"] = part.Image.Url
                                    }
                                });
                            }
                            break;

                        case ContentKind.ToolCall when part.ToolCall is not null:
                            JsonNode? argsNode;
                            try { argsNode = JsonNode.Parse(part.ToolCall.Arguments); }
                            catch { argsNode = new JsonObject(); }

                            parts.Add(new JsonObject
                            {
                                ["functionCall"] = new JsonObject
                                {
                                    ["name"] = part.ToolCall.Name,
                                    ["args"] = argsNode
                                }
                            });
                            break;

                        case ContentKind.Thinking when part.Thinking is not null:
                            parts.Add(new JsonObject
                            {
                                ["text"] = part.Thinking.Text,
                                ["thought"] = true
                            });
                            break;
                    }
                }
            }

            if (parts.Count > 0)
            {
                contents.Add(new JsonObject
                {
                    ["role"] = geminiRole,
                    ["parts"] = parts
                });
            }
        }

        body["contents"] = contents;

        // Generation config
        var genConfig = new JsonObject();
        bool hasGenConfig = false;

        if (request.Temperature is not null)
        {
            genConfig["temperature"] = request.Temperature.Value;
            hasGenConfig = true;
        }

        if (request.TopP is not null)
        {
            genConfig["topP"] = request.TopP.Value;
            hasGenConfig = true;
        }

        if (request.MaxTokens is not null)
        {
            genConfig["maxOutputTokens"] = request.MaxTokens.Value;
            hasGenConfig = true;
        }

        if (request.StopSequences is not null && request.StopSequences.Count > 0)
        {
            var stops = new JsonArray();
            foreach (var s in request.StopSequences) stops.Add(s);
            genConfig["stopSequences"] = stops;
            hasGenConfig = true;
        }

        if (request.ResponseFormat is not null)
        {
            if (request.ResponseFormat.Type == "json_object" || request.ResponseFormat.Type == "json_schema")
            {
                genConfig["responseMimeType"] = "application/json";
                hasGenConfig = true;

                if (request.ResponseFormat.JsonSchema is not null)
                {
                    genConfig["responseSchema"] = JsonSerializer.SerializeToNode(request.ResponseFormat.JsonSchema);
                }
            }
        }

        // Reasoning / thinking
        if (request.ReasoningEffort is not null)
        {
            genConfig["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = request.ReasoningEffort.ToLowerInvariant() switch
                {
                    "low" => 2048,
                    "medium" => 8192,
                    "high" => 32768,
                    _ => int.TryParse(request.ReasoningEffort, out var n) ? n : 8192
                }
            };
            hasGenConfig = true;
        }

        if (hasGenConfig)
            body["generationConfig"] = genConfig;

        // Tools
        if (request.Tools is not null && request.Tools.Count > 0)
        {
            var functionDeclarations = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var param in tool.Parameters)
                {
                    var prop = new JsonObject { ["type"] = param.Type.ToUpperInvariant() };
                    if (param.Description is not null)
                        prop["description"] = param.Description;
                    properties[param.Name] = prop;
                    if (param.Required)
                        required.Add(param.Name);
                }

                var parameters = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = properties
                };
                if (required.Count > 0)
                    parameters["required"] = required;

                functionDeclarations.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters
                });
            }

            body["tools"] = new JsonArray
            {
                new JsonObject { ["functionDeclarations"] = functionDeclarations }
            };
        }

        // Tool choice / tool config
        if (request.ToolChoice is not null)
        {
            var mode = request.ToolChoice.Mode switch
            {
                ToolChoiceMode.Auto => "AUTO",
                ToolChoiceMode.None => "NONE",
                ToolChoiceMode.Required => "ANY",
                ToolChoiceMode.Named => "ANY",
                _ => "AUTO"
            };

            var toolConfig = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject { ["mode"] = mode }
            };

            if (request.ToolChoice.Mode == ToolChoiceMode.Named && request.ToolChoice.ToolName is not null)
            {
                ((JsonObject)toolConfig["functionCallingConfig"]!)["allowedFunctionNames"] =
                    new JsonArray { request.ToolChoice.ToolName };
            }

            body["toolConfig"] = toolConfig;
        }

        return body;
    }

    // ── Error handling ─────────────────────────────────────────────────

    private void EnsureSuccess(HttpResponseMessage httpRes, string responseBody)
    {
        if (httpRes.IsSuccessStatusCode) return;

        var status = httpRes.StatusCode;
        var message = $"Gemini API error ({(int)status})";

        try
        {
            var node = JsonNode.Parse(responseBody);
            var errMsg = node?["error"]?["message"]?.GetValue<string>();
            if (errMsg is not null) message = errMsg;
        }
        catch { /* use default message */ }

        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AuthenticationError(message, status, providerName: Name, responseBody: responseBody),
            HttpStatusCode.TooManyRequests =>
                new RateLimitError(message, providerName: Name, responseBody: responseBody),
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
            ?? throw new ProviderError("Empty response from Gemini", HttpStatusCode.InternalServerError, providerName: Name);

        // Check for API-level error
        var errorNode = node["error"];
        if (errorNode is not null)
        {
            var errMsg = errorNode["message"]?.GetValue<string>() ?? "Unknown Gemini error";
            var errCode = errorNode["code"]?.GetValue<int>() ?? 500;
            throw new ProviderError(errMsg, (HttpStatusCode)errCode, providerName: Name, responseBody: responseBody);
        }

        var candidates = node["candidates"]?.AsArray();
        if (candidates is null || candidates.Count == 0)
        {
            // Check for prompt feedback (blocked prompts)
            var promptFeedback = node["promptFeedback"];
            var blockReason = promptFeedback?["blockReason"]?.GetValue<string>();
            if (blockReason is not null)
            {
                throw new ContentFilterError(
                    $"Prompt blocked by Gemini: {blockReason}",
                    providerName: Name,
                    responseBody: responseBody);
            }

            return new Response(
                "",
                requestModel,
                Name,
                new Message(Role.Assistant, [ContentPart.TextPart("")]),
                FinishReason.Stop,
                ParseUsage(node["usageMetadata"]));
        }

        var candidate = candidates[0]!;
        var contentNode = candidate["content"];
        var partsArray = contentNode?["parts"]?.AsArray();

        var content = new List<ContentPart>();
        var hasToolCalls = false;

        if (partsArray is not null)
        {
            foreach (var part in partsArray)
            {
                if (part is null) continue;

                // Text part
                var text = part["text"]?.GetValue<string>();
                if (text is not null)
                {
                    var isThought = part["thought"]?.GetValue<bool>() ?? false;
                    if (isThought)
                        content.Add(ContentPart.ThinkingPart(new ThinkingData(text, null, false)));
                    else
                        content.Add(ContentPart.TextPart(text));
                }

                // Function call part
                var funcCall = part["functionCall"];
                if (funcCall is not null)
                {
                    var funcName = funcCall["name"]?.GetValue<string>() ?? "";
                    var argsJson = funcCall["args"]?.ToJsonString() ?? "{}";
                    var callId = GenerateSyntheticCallId();
                    content.Add(ContentPart.ToolCallPart(new ToolCallData(callId, funcName, argsJson)));
                    hasToolCalls = true;
                }
            }
        }

        var finishReasonStr = candidate["finishReason"]?.GetValue<string>();
        var finishReason = MapFinishReason(finishReasonStr, hasToolCalls);
        var usage = ParseUsage(node["usageMetadata"]);

        var msg = new Message(Role.Assistant, content.Count > 0 ? content : [ContentPart.TextPart("")]);

        return new Response("", requestModel, Name, msg, finishReason, usage);
    }

    private static FinishReason MapFinishReason(string? reason, bool hasToolCalls)
    {
        if (hasToolCalls) return FinishReason.ToolCalls;

        return reason switch
        {
            "STOP" => FinishReason.Stop,
            "MAX_TOKENS" => FinishReason.Length,
            "SAFETY" => FinishReason.ContentFilter,
            "RECITATION" => FinishReason.ContentFilter,
            "BLOCKLIST" => FinishReason.ContentFilter,
            "PROHIBITED_CONTENT" => FinishReason.ContentFilter,
            "SPII" => FinishReason.ContentFilter,
            _ => FinishReason.Stop
        };
    }

    private static Usage ParseUsage(JsonNode? usage)
    {
        if (usage is null) return Usage.Empty;

        var input = usage["promptTokenCount"]?.GetValue<int>() ?? 0;
        var output = usage["candidatesTokenCount"]?.GetValue<int>() ?? 0;
        var thoughts = usage["thoughtsTokenCount"]?.GetValue<int>();
        var cached = usage["cachedContentTokenCount"]?.GetValue<int>();

        return new Usage(
            InputTokens: input,
            OutputTokens: output,
            TotalTokens: input + output,
            ReasoningTokens: thoughts is > 0 ? thoughts : null,
            CacheReadTokens: cached is > 0 ? cached : null);
    }

    private string GenerateSyntheticCallId()
    {
        var id = Interlocked.Increment(ref _syntheticCallIdCounter);
        return $"gemini_call_{id:D4}";
    }
}
