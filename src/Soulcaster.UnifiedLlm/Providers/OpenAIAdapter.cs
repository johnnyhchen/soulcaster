using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soulcaster.UnifiedLlm.Providers;

public sealed class OpenAIAdapter : IProviderAdapter, IProviderDiscoveryAdapter
{
    private const string DefaultBaseUrl = "https://api.openai.com";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public string Name => "openai";

    public OpenAIAdapter(string apiKey, string baseUrl = DefaultBaseUrl, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    public async Task<ProviderPingResult> PingAsync(CancellationToken ct = default)
    {
        var endpoint = $"{_baseUrl}/v1/models";

        try
        {
            var models = await ListModelsAsync(ct).ConfigureAwait(false);
            return new ProviderPingResult(Name, true, endpoint, StatusCode: 200, ModelCount: models.Count);
        }
        catch (ProviderError ex)
        {
            return new ProviderPingResult(Name, false, endpoint, StatusCode: (int)ex.StatusCode, Message: ex.Message);
        }
        catch (Exception ex)
        {
            return new ProviderPingResult(Name, false, endpoint, Message: ex.Message);
        }
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> ListModelsAsync(CancellationToken ct = default)
    {
        using var httpReq = CreateModelsRequest();
        using var httpRes = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        var responseBody = await httpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccess(httpRes, responseBody);

        using var doc = JsonDocument.Parse(responseBody);
        var models = new List<ProviderModelDescriptor>();

        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = ProviderDiscoveryJson.GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                models.Add(new ProviderModelDescriptor(
                    Provider: Name,
                    Id: id,
                    DisplayName: ProviderDiscoveryJson.GetString(item, "display_name", "name"),
                    ContextWindow: ProviderDiscoveryJson.GetInt32(item, "context_window", "input_token_limit"),
                    MaxOutput: ProviderDiscoveryJson.GetInt32(item, "max_output_tokens", "output_token_limit"),
                    SupportsTools: ProviderDiscoveryJson.GetBool(item, "supports_tools"),
                    SupportsVision: ProviderDiscoveryJson.GetBool(item, "supports_vision"),
                    SupportsReasoning: ProviderDiscoveryJson.GetBool(item, "supports_reasoning"),
                    RawJson: item.GetRawText()));
            }
        }

        return models
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    // ── CompleteAsync ──────────────────────────────────────────────────

    public async Task<Response> CompleteAsync(Request request, CancellationToken ct = default)
    {
        if (UsesImagesApi(request))
        {
            var imageRequest = BuildImagesApiRequest(request);
            using var imageHttpReq = imageRequest.InputImages.Count == 0
                ? CreateImagesGenerationsHttpRequest(imageRequest.JsonBody!)
                : CreateImagesEditsHttpRequest(imageRequest.MultipartBody!);
            using var imageHttpRes = await _http.SendAsync(imageHttpReq, ct).ConfigureAwait(false);
            var imageResponseBody = await imageHttpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureSuccess(imageHttpRes, imageResponseBody);
            return ParseImagesResponse(imageResponseBody, request.Model);
        }

        if (RequiresChatCompletionsApi(request))
        {
            var chatBody = BuildChatCompletionsRequestBody(request, stream: false);
            using var chatHttpReq = CreateChatCompletionsHttpRequest(chatBody);
            using var chatHttpRes = await _http.SendAsync(chatHttpReq, ct).ConfigureAwait(false);
            var chatResponseBody = await chatHttpRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureSuccess(chatHttpRes, chatResponseBody);
            return ParseChatCompletionsResponse(chatResponseBody, request.Model);
        }

        var body = BuildRequestBody(request, stream: false);
        using var httpReq = CreateResponsesHttpRequest(body);
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
        if (UsesImagesApi(request) || RequiresChatCompletionsApi(request))
        {
            var response = await CompleteAsync(request, ct).ConfigureAwait(false);
            yield return new StreamEvent { Type = StreamEventType.StreamStart };
            if (!string.IsNullOrEmpty(response.Text))
            {
                yield return new StreamEvent { Type = StreamEventType.TextStart };
                yield return new StreamEvent { Type = StreamEventType.TextDelta, Delta = response.Text };
                yield return new StreamEvent { Type = StreamEventType.TextEnd };
            }

            yield return new StreamEvent
            {
                Type = StreamEventType.Finish,
                FinishReason = response.FinishReason,
                Usage = response.Usage,
                Response = response
            };
            yield break;
        }

        var body = BuildRequestBody(request, stream: true);
        using var httpReq = CreateResponsesHttpRequest(body);
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

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

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

    private sealed record ImagesApiRequest(
        JsonObject? JsonBody,
        MultipartFormDataContent? MultipartBody,
        IReadOnlyList<ImageData> InputImages);

    private ImagesApiRequest BuildImagesApiRequest(Request request)
    {
        var prompt = BuildImagesPrompt(request);
        var inputImages = ExtractInputImages(request.Messages);
        if (inputImages.Count == 0)
        {
            return new ImagesApiRequest(
                JsonBody: BuildImagesGenerationsBody(request, prompt),
                MultipartBody: null,
                InputImages: inputImages);
        }

        return new ImagesApiRequest(
            JsonBody: null,
            MultipartBody: BuildImagesEditsBody(request, prompt, inputImages),
            InputImages: inputImages);
    }

    private JsonObject BuildImagesGenerationsBody(Request request, string prompt)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["prompt"] = prompt
        };
        AddImageApiOptions(body, request);
        return body;
    }

    private MultipartFormDataContent BuildImagesEditsBody(
        Request request,
        string prompt,
        IReadOnlyList<ImageData> inputImages)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(request.Model), "model" },
            { new StringContent(prompt), "prompt" }
        };

        AddImageApiOptions(content, request);

        for (var i = 0; i < inputImages.Count; i++)
        {
            var image = inputImages[i];
            var data = ResolveImageBytes(image);
            if (data is null || data.Length == 0)
                throw new ConfigurationError("OpenAI Images API input images must be local bytes or data URLs.");

            var mediaType = image.MediaType ?? "image/png";
            var imageContent = new ByteArrayContent(data);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            var extension = GetImageExtension(mediaType);
            var fieldName = inputImages.Count == 1 ? "image" : "image[]";
            content.Add(imageContent, fieldName, $"input-{i + 1}{extension}");
        }

        return content;
    }

    private static void AddImageApiOptions(JsonObject body, Request request)
    {
        foreach (var key in SupportedImageApiOptionKeys())
        {
            if (request.ProviderOptions?.TryGetValue(key, out var value) == true)
                body[key] = JsonSerializer.SerializeToNode(value);
        }
    }

    private static void AddImageApiOptions(MultipartFormDataContent content, Request request)
    {
        foreach (var key in SupportedImageApiOptionKeys())
        {
            if (request.ProviderOptions?.TryGetValue(key, out var value) == true && value is not null)
                content.Add(new StringContent(value.ToString() ?? string.Empty), key);
        }
    }

    private static IReadOnlyList<string> SupportedImageApiOptionKeys() =>
    [
        "size",
        "quality",
        "background",
        "output_format",
        "output_compression",
        "moderation",
        "n"
    ];

    private static string BuildImagesPrompt(Request request)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var message in request.Messages)
        {
            foreach (var part in message.Content)
            {
                switch (part.Kind)
                {
                    case ContentKind.Text when !string.IsNullOrWhiteSpace(part.Text):
                        sb.AppendLine(part.Text);
                        break;

                    case ContentKind.Document when part.Document is not null:
                        var documentText = TryDecodeTextDocument(part.Document);
                        if (!string.IsNullOrWhiteSpace(documentText))
                        {
                            sb.AppendLine();
                            sb.AppendLine($"[Attached document: {part.Document.FileName ?? "document"}]");
                            sb.AppendLine(documentText);
                            sb.AppendLine("[/Attached document]");
                        }
                        break;
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static IReadOnlyList<ImageData> ExtractInputImages(IReadOnlyList<Message> messages)
    {
        var images = new List<ImageData>();
        foreach (var message in messages)
        {
            foreach (var part in message.Content)
            {
                if (part.Kind == ContentKind.Image && part.Image is not null)
                    images.Add(part.Image);
            }
        }

        return images;
    }

    private static string? TryDecodeTextDocument(DocumentData document)
    {
        if (document.Data is null || document.Data.Length == 0)
            return null;

        var mediaType = document.MediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var extension = Path.GetExtension(document.FileName ?? string.Empty).ToLowerInvariant();
        var isText =
            mediaType is "text/plain" or "text/markdown" or "application/json" or "text/csv" ||
            extension is ".txt" or ".md" or ".json" or ".csv";
        if (!isText)
            return null;

        return System.Text.Encoding.UTF8.GetString(document.Data);
    }

    private static byte[]? ResolveImageBytes(ImageData image)
    {
        if (image.Data is { Length: > 0 })
            return image.Data;

        if (image.Url is not null && image.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = image.Url.IndexOf(',');
            if (commaIndex >= 0)
                return Convert.FromBase64String(image.Url[(commaIndex + 1)..]);
        }

        return null;
    }

    private JsonObject BuildRequestBody(Request request, bool stream)
    {
        var (previousResponseId, inputMessages) = ResolvePreviousResponseContext(request.Messages);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = stream
        };
        if (!string.IsNullOrWhiteSpace(previousResponseId))
            body["previous_response_id"] = previousResponseId;

        // Build input (messages array)
        var input = new JsonArray();
        foreach (var msg in inputMessages)
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
                var assistantContent = new JsonArray();
                var assistantToolCalls = new List<JsonObject>();
                foreach (var part in msg.Content)
                {
                    if (part.Kind == ContentKind.Text && part.Text is not null)
                    {
                        assistantContent.Add(BuildResponsesTextPart(part.Text, assistantRole: true));
                    }
                    else if (part.Kind is ContentKind.Image or ContentKind.Document or ContentKind.Audio)
                    {
                        throw new ConfigurationError(
                            "OpenAI assistant media history must be replayed via previous_response_id. " +
                            "Preserve the originating assistant response ID when continuing a multimodal conversation.");
                    }
                    else if (part.Kind == ContentKind.ToolCall && part.ToolCall is not null)
                    {
                        assistantToolCalls.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = part.ToolCall.Id,
                            ["name"] = part.ToolCall.Name,
                            ["arguments"] = part.ToolCall.Arguments
                        });
                    }
                }

                if (assistantContent.Count > 0)
                {
                    input.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = assistantContent
                    });
                }

                foreach (var toolCall in assistantToolCalls)
                    input.Add(toolCall);

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
                            contentArray.Add(BuildResponsesTextPart(part.Text, assistantRole: false));
                            break;

                        case ContentKind.Image when part.Image is not null:
                            contentArray.Add(BuildResponsesImagePart(part.Image));
                            break;

                        case ContentKind.Document when part.Document is not null:
                            contentArray.Add(BuildResponsesDocumentPart(part.Document));
                            break;

                        case ContentKind.Audio when part.Audio is not null:
                            throw new ConfigurationError(
                                "OpenAI Responses API does not yet support audio input parts in Soulcaster. " +
                                "Audio input requests must use the Chat Completions audio path.");
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

        var wantsImageOutput = WantsOutputModality(request, ResponseModality.Image);

        // Tools
        if ((request.Tools is not null && request.Tools.Count > 0) || wantsImageOutput)
        {
            var tools = new JsonArray();
            if (request.Tools is not null)
            {
                foreach (var tool in request.Tools)
                {
                    var properties = new JsonObject();
                    var required = new JsonArray();
                    foreach (var param in tool.Parameters)
                    {
                        var prop = new JsonObject { ["type"] = new JsonArray(param.Type, "null") };
                        if (param.Description is not null)
                            prop["description"] = param.Description;
                        if (param.ItemsType is not null)
                            prop["items"] = new JsonObject { ["type"] = param.ItemsType };
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
            }

            if (wantsImageOutput)
                tools.Add(new JsonObject { ["type"] = "image_generation" });

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

        // Provider options escape hatch — merge extra keys into the request body
        if (request.ProviderOptions is not null)
        {
            foreach (var (key, value) in request.ProviderOptions)
            {
                if (body.ContainsKey(key)) continue; // Don't override existing keys
                body[key] = JsonSerializer.SerializeToNode(value);
            }
        }

        return body;
    }

    private static (string? PreviousResponseId, IReadOnlyList<Message> Messages) ResolvePreviousResponseContext(
        IReadOnlyList<Message> messages)
    {
        var assistantIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == Role.Assistant && !string.IsNullOrWhiteSpace(messages[i].ResponseId))
            {
                assistantIndex = i;
                break;
            }
        }

        if (assistantIndex < 0)
            return (null, messages);

        var chainedMessages = new List<Message>();
        chainedMessages.AddRange(messages.Where(message => message.Role is Role.System or Role.Developer));
        chainedMessages.AddRange(messages.Skip(assistantIndex + 1).Where(message => message.Role is not (Role.System or Role.Developer)));

        if (chainedMessages.Count == 0)
            return (null, messages);

        return (messages[assistantIndex].ResponseId, chainedMessages);
    }

    // ── HTTP helpers ───────────────────────────────────────────────────

    private HttpRequestMessage CreateModelsRequest()
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models");
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return httpReq;
    }

    private HttpRequestMessage CreateResponsesHttpRequest(JsonObject body)
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

    private HttpRequestMessage CreateImagesGenerationsHttpRequest(JsonObject body)
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/images/generations")
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        return httpReq;
    }

    private HttpRequestMessage CreateImagesEditsHttpRequest(MultipartFormDataContent body)
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/images/edits")
        {
            Content = body
        };

        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        return httpReq;
    }

    private HttpRequestMessage CreateChatCompletionsHttpRequest(JsonObject body)
    {
        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
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

                    case "image_generation_call":
                        var imageBase64 = item["result"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(imageBase64))
                        {
                            var mimeType = item["mime_type"]?.GetValue<string>() ?? "image/png";
                            var imageBytes = Convert.FromBase64String(imageBase64);
                            content.Add(ContentPart.ImagePart(ImageData.FromBytes(
                                imageBytes,
                                mimeType,
                                providerState: BuildImageProviderState(item, mimeType, imageBytes))));
                        }
                        break;
                }
            }
        }

        var hasToolCalls = content.Any(c => c.Kind == ContentKind.ToolCall);
        var finishReason = MapFinishReason(status, hasToolCalls);
        var usage = ParseUsage(node["usage"]);

        var msg = new Message(Role.Assistant, content.Count > 0 ? content : [ContentPart.TextPart("")], ResponseId: id);

        return new Response(id, model, Name, msg, finishReason, usage);
    }

    private Response ParseChatCompletionsResponse(string responseBody, string requestModel)
    {
        var node = JsonNode.Parse(responseBody)
            ?? throw new ProviderError("Empty response from OpenAI", HttpStatusCode.InternalServerError, providerName: Name);

        var id = node["id"]?.GetValue<string>() ?? "";
        var model = node["model"]?.GetValue<string>() ?? requestModel;
        var choice = node["choices"]?[0];
        var finishReason = MapChatCompletionsFinishReason(choice?["finish_reason"]?.GetValue<string>());
        var messageNode = choice?["message"];

        var content = new List<ContentPart>();
        var messageContent = messageNode?["content"];
        if (messageContent is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block is null)
                    continue;

                var type = block["type"]?.GetValue<string>();
                if (type == "text")
                    content.Add(ContentPart.TextPart(block["text"]?.GetValue<string>() ?? string.Empty));
            }
        }
        else if (messageContent is not null)
        {
            var text = messageContent.GetValue<string?>();
            if (!string.IsNullOrEmpty(text))
                content.Add(ContentPart.TextPart(text));
        }

        var toolCalls = messageNode?["tool_calls"]?.AsArray();
        if (toolCalls is not null)
        {
            foreach (var toolCall in toolCalls)
            {
                if (toolCall is null)
                    continue;

                var idValue = toolCall["id"]?.GetValue<string>() ?? string.Empty;
                var function = toolCall["function"];
                if (function is null)
                    continue;

                content.Add(ContentPart.ToolCallPart(new ToolCallData(
                    idValue,
                    function["name"]?.GetValue<string>() ?? string.Empty,
                    function["arguments"]?.GetValue<string>() ?? "{}")));
            }
        }

        var msg = new Message(Role.Assistant, content.Count > 0 ? content : [ContentPart.TextPart(string.Empty)], ResponseId: id);
        return new Response(id, model, Name, msg, finishReason, ParseChatCompletionsUsage(node["usage"]));
    }

    private Response ParseImagesResponse(string responseBody, string requestModel)
    {
        var node = JsonNode.Parse(responseBody)
            ?? throw new ProviderError("Empty response from OpenAI Images API", HttpStatusCode.InternalServerError, providerName: Name);

        var content = new List<ContentPart>();
        var dataArray = node["data"]?.AsArray();
        if (dataArray is not null)
        {
            foreach (var item in dataArray)
            {
                if (item is null)
                    continue;

                var revisedPrompt = item["revised_prompt"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(revisedPrompt))
                    content.Add(ContentPart.TextPart(revisedPrompt));

                var imageBase64 = item["b64_json"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(imageBase64))
                {
                    var imageBytes = Convert.FromBase64String(imageBase64);
                    var mimeType = ResolveImageMimeType(item["mime_type"]?.GetValue<string>());
                    content.Add(ContentPart.ImagePart(ImageData.FromBytes(
                        imageBytes,
                        mimeType,
                        providerState: BuildImagesApiProviderState(item, mimeType, imageBytes))));
                }
            }
        }

        var id = node["id"]?.GetValue<string>() ?? string.Empty;
        var msg = new Message(
            Role.Assistant,
            content.Count > 0 ? content : [ContentPart.TextPart(string.Empty)],
            ResponseId: string.IsNullOrWhiteSpace(id) ? null : id);

        return new Response(
            id,
            requestModel,
            Name,
            msg,
            FinishReason.Stop,
            ParseUsage(node["usage"]));
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

    private static FinishReason MapChatCompletionsFinishReason(string? finishReason) => finishReason switch
    {
        "tool_calls" => FinishReason.ToolCalls,
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        "stop" or null or "" => FinishReason.Stop,
        _ => FinishReason.Stop
    };

    private static bool UsesImagesApi(Request request) =>
        IsOpenAiImageModel(request.Model) &&
        (request.OutputModalities?.Contains(ResponseModality.Image) == true || !HasNonImageTooling(request));

    private static bool IsOpenAiImageModel(string model) =>
        model.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("dall-e-", StringComparison.OrdinalIgnoreCase);

    private static bool HasNonImageTooling(Request request) =>
        request.Tools is { Count: > 0 } || request.ToolChoice is not null || request.ResponseFormat is not null;

    private static string ResolveImageMimeType(string? declaredMimeType) =>
        string.IsNullOrWhiteSpace(declaredMimeType) ? "image/png" : declaredMimeType;

    private static string GetImageExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".png"
    };

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

    private static Usage ParseChatCompletionsUsage(JsonNode? usage)
    {
        if (usage is null) return Usage.Empty;

        var input = usage["prompt_tokens"]?.GetValue<int>() ?? 0;
        var output = usage["completion_tokens"]?.GetValue<int>() ?? 0;
        var total = usage["total_tokens"]?.GetValue<int>() ?? (input + output);

        int? reasoningTokens = null;
        var completionDetails = usage["completion_tokens_details"];
        if (completionDetails is not null)
            reasoningTokens = completionDetails["reasoning_tokens"]?.GetValue<int>();

        int? cacheRead = null;
        var promptDetails = usage["prompt_tokens_details"];
        if (promptDetails is not null)
            cacheRead = promptDetails["cached_tokens"]?.GetValue<int>();

        return new Usage(
            InputTokens: input,
            OutputTokens: output,
            TotalTokens: total,
            ReasoningTokens: reasoningTokens is > 0 ? reasoningTokens : null,
            CacheReadTokens: cacheRead is > 0 ? cacheRead : null);
    }

    private JsonObject BuildChatCompletionsRequestBody(Request request, bool stream)
    {
        if (request.Tools is { Count: > 0 })
        {
            throw new ConfigurationError(
                "OpenAI audio-input requests do not yet support tool calling in Soulcaster's Chat Completions path.");
        }

        if (WantsOutputModality(request, ResponseModality.Image))
        {
            throw new ConfigurationError(
                "OpenAI audio-input requests do not support image output in Soulcaster's Chat Completions path.");
        }

        if (request.ResponseFormat is not null)
        {
            throw new ConfigurationError(
                "OpenAI audio-input requests do not yet support response_format in Soulcaster's Chat Completions path.");
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = stream
        };

        var messages = new JsonArray();
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

            if (msg.Content.Count == 1 && msg.Content[0].Kind == ContentKind.Text)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = msg.Content[0].Text
                });
                continue;
            }

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
                        content.Add(BuildChatImagePart(part.Image));
                        break;

                    case ContentKind.Document when part.Document is not null:
                        content.Add(BuildChatDocumentPart(part.Document));
                        break;

                    case ContentKind.Audio when part.Audio is not null:
                        content.Add(BuildChatAudioPart(part.Audio));
                        break;

                    case ContentKind.ToolCall:
                    case ContentKind.ToolResult:
                        throw new ConfigurationError(
                            "OpenAI audio-input requests do not yet support tool-call transcript replay in Soulcaster's Chat Completions path.");
                }
            }

            messages.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content
            });
        }

        body["messages"] = messages;

        if (request.MaxTokens is not null)
            body["max_completion_tokens"] = request.MaxTokens.Value;

        if (request.Temperature is not null)
            body["temperature"] = request.Temperature.Value;

        if (request.TopP is not null)
            body["top_p"] = request.TopP.Value;

        if (request.StopSequences is { Count: > 0 })
        {
            var stops = new JsonArray();
            foreach (var stop in request.StopSequences)
                stops.Add(stop);
            body["stop"] = stops;
        }

        if (request.Metadata is not null && request.Metadata.Count > 0)
        {
            var meta = new JsonObject();
            foreach (var (key, value) in request.Metadata)
                meta[key] = value;
            body["metadata"] = meta;
        }

        if (request.ProviderOptions is not null)
        {
            foreach (var (key, value) in request.ProviderOptions)
            {
                if (body.ContainsKey(key))
                    continue;

                body[key] = JsonSerializer.SerializeToNode(value);
            }
        }

        return body;
    }

    private static JsonObject BuildResponsesTextPart(string text, bool assistantRole)
    {
        return new JsonObject
        {
            ["type"] = assistantRole ? "output_text" : "input_text",
            ["text"] = text
        };
    }

    private static JsonObject BuildResponsesImagePart(ImageData image)
    {
        if (TryGetOpenAiImageUrl(image, out var imageUrl))
        {
            return BuildResponsesImagePart(imageUrl, image.Detail);
        }

        if (!string.IsNullOrWhiteSpace(image.Url))
        {
            return BuildResponsesImagePart(image.Url!, image.Detail);
        }

        if (image.Data is null)
            throw new ConfigurationError("OpenAI image inputs require either raw bytes or a URL.");

        return BuildResponsesImagePart(
            BuildDataUrl(image.MediaType ?? "image/png", image.Data),
            image.Detail);
    }

    private static JsonObject BuildResponsesImagePart(string imageUrl, string? detail)
    {
        var part = new JsonObject
        {
            ["type"] = "input_image",
            ["image_url"] = imageUrl
        };

        if (!string.IsNullOrWhiteSpace(detail))
            part["detail"] = detail;

        return part;
    }

    private static JsonObject BuildResponsesDocumentPart(DocumentData document)
    {
        if (!string.IsNullOrWhiteSpace(document.Url))
        {
            return new JsonObject
            {
                ["type"] = "input_file",
                ["file_url"] = document.Url
            };
        }

        if (document.Data is null)
        {
            throw new ConfigurationError("OpenAI document inputs require either raw file bytes or a file URL.");
        }

        return new JsonObject
        {
            ["type"] = "input_file",
            ["filename"] = document.FileName ?? "attachment",
            ["file_data"] = BuildDataUrl(document.MediaType ?? "application/octet-stream", document.Data)
        };
    }

    private static JsonObject BuildChatImagePart(ImageData image)
    {
        if (TryGetOpenAiImageUrl(image, out var providerImageUrl))
        {
            return BuildChatImagePart(providerImageUrl, image.Detail);
        }

        if (!string.IsNullOrWhiteSpace(image.Url))
        {
            return BuildChatImagePart(image.Url!, image.Detail);
        }

        if (image.Data is null)
            throw new ConfigurationError("OpenAI image inputs require either raw bytes or a URL.");

        return BuildChatImagePart(
            BuildDataUrl(image.MediaType ?? "image/png", image.Data),
            image.Detail);
    }

    private static JsonObject BuildChatImagePart(string imageUrl, string? detail)
    {
        var imageUrlObject = new JsonObject
        {
            ["url"] = imageUrl
        };

        if (!string.IsNullOrWhiteSpace(detail))
            imageUrlObject["detail"] = detail;

        return new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = imageUrlObject
        };
    }

    private static bool TryGetOpenAiImageUrl(ImageData image, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (!MediaProviderState.IsProvider(image.ProviderState, "openai"))
            return false;

        var storedUrl = MediaProviderState.GetString(image.ProviderState, "image_url");
        if (string.IsNullOrWhiteSpace(storedUrl))
            return false;

        imageUrl = storedUrl;
        return true;
    }

    private static JsonObject BuildImageProviderState(JsonNode item, string mimeType, byte[] imageBytes)
    {
        var itemId = item["id"]?.GetValue<string>();
        var revisedPrompt = item["revised_prompt"]?.GetValue<string>();

        return MediaProviderState.Create(
            "openai",
            ("kind", JsonValue.Create("image")),
            ("source", JsonValue.Create("image_generation_call")),
            ("image_url", JsonValue.Create(BuildDataUrl(mimeType, imageBytes))),
            ("output_item_id", string.IsNullOrWhiteSpace(itemId) ? null : JsonValue.Create(itemId)),
            ("revised_prompt", string.IsNullOrWhiteSpace(revisedPrompt) ? null : JsonValue.Create(revisedPrompt)));
    }

    private static JsonObject BuildImagesApiProviderState(JsonNode item, string mimeType, byte[] imageBytes)
    {
        var revisedPrompt = item["revised_prompt"]?.GetValue<string>();

        return MediaProviderState.Create(
            "openai",
            ("kind", JsonValue.Create("image")),
            ("source", JsonValue.Create("images_api")),
            ("image_url", JsonValue.Create(BuildDataUrl(mimeType, imageBytes))),
            ("revised_prompt", string.IsNullOrWhiteSpace(revisedPrompt) ? null : JsonValue.Create(revisedPrompt)));
    }

    private static JsonObject BuildChatDocumentPart(DocumentData document)
    {
        if (!string.IsNullOrWhiteSpace(document.Url))
        {
            throw new ConfigurationError(
                "OpenAI Chat Completions file inputs do not support external document URLs in Soulcaster. Use raw bytes or a file ID.");
        }

        if (document.Data is null)
            throw new ConfigurationError("OpenAI document inputs require raw file bytes in Soulcaster's Chat Completions path.");

        return new JsonObject
        {
            ["type"] = "file",
            ["file"] = new JsonObject
            {
                ["filename"] = document.FileName ?? "attachment",
                ["file_data"] = BuildDataUrl(document.MediaType ?? "application/octet-stream", document.Data)
            }
        };
    }

    private static JsonObject BuildChatAudioPart(AudioData audio)
    {
        if (!string.IsNullOrWhiteSpace(audio.Url))
        {
            throw new ConfigurationError(
                "OpenAI Chat Completions audio inputs do not support external audio URLs in Soulcaster. Use raw audio bytes instead.");
        }

        if (audio.Data is null)
            throw new ConfigurationError("OpenAI audio inputs require raw audio bytes in Soulcaster's Chat Completions path.");

        return new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(audio.Data),
                ["format"] = ResolveAudioFormat(audio)
            }
        };
    }

    private static bool RequiresChatCompletionsApi(Request request) =>
        request.Messages
            .SelectMany(message => message.Content)
            .Any(part => part.Kind == ContentKind.Audio && part.Audio is not null);

    private static string BuildDataUrl(string mediaType, byte[] data) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(data)}";

    private static string ResolveAudioFormat(AudioData audio)
    {
        var mediaType = audio.MediaType?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType switch
            {
                "audio/wav" or "audio/x-wav" => "wav",
                "audio/mpeg" => "mp3",
                "audio/mp4" or "audio/x-m4a" => "m4a",
                "audio/ogg" => "ogg",
                "audio/flac" => "flac",
                _ => ResolveAudioFormatFromFileName(audio.FileName)
            };
        }

        return ResolveAudioFormatFromFileName(audio.FileName);
    }

    private static string ResolveAudioFormatFromFileName(string? fileName)
    {
        return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".wav" => "wav",
            ".mp3" => "mp3",
            ".m4a" => "m4a",
            ".ogg" => "ogg",
            ".flac" => "flac",
            _ => throw new ConfigurationError(
                "OpenAI audio inputs require a recognized audio format (wav, mp3, m4a, ogg, or flac).")
        };
    }

    private static bool WantsOutputModality(Request request, ResponseModality modality) =>
        request.OutputModalities?.Contains(modality) == true;
}
