namespace JcAttractor.Attractor;

using System.Text.Json;

/// <summary>
/// Canonical structured stage output contract used for routing and context propagation.
/// </summary>
public sealed record BlockingQuestion(
    string Text,
    string? Context = null,
    string? DesiredFormat = null);

public sealed record StageStatusContract(
    OutcomeStatus Status,
    string PreferredNextLabel,
    List<string> SuggestedNextIds,
    Dictionary<string, string> ContextUpdates,
    string Notes,
    string? FailureReason = null,
    BlockingQuestion? BlockingQuestion = null)
{
    public static StageStatusContract FromLegacy(CodergenResult result, string? fallbackReason = null)
    {
        var notes = string.IsNullOrWhiteSpace(result.Response)
            ? $"Codergen completed with status {result.Status}."
            : $"Codergen completed with status {result.Status} (legacy fallback).";

        return new StageStatusContract(
            Status: result.Status,
            PreferredNextLabel: result.PreferredLabel ?? string.Empty,
            SuggestedNextIds: result.SuggestedNextIds ?? new List<string>(),
            ContextUpdates: result.ContextUpdates ?? new Dictionary<string, string>(),
            Notes: notes,
            FailureReason: fallbackReason,
            BlockingQuestion: null);
    }

    public Outcome ToOutcome()
    {
        return new Outcome(
            Status: Status,
            PreferredLabel: PreferredNextLabel,
            SuggestedNextIds: SuggestedNextIds,
            ContextUpdates: ContextUpdates,
            Notes: Notes);
    }

    public Dictionary<string, object?> ToStatusJson(
        string nodeId,
        string? model,
        string? provider,
        bool usedFallback,
        string? validationError = null)
    {
        return new Dictionary<string, object?>
        {
            ["node_id"] = nodeId,
            ["status"] = ToStatusString(Status),
            ["preferred_next_label"] = string.IsNullOrWhiteSpace(PreferredNextLabel) ? null : PreferredNextLabel,
            ["suggested_next_ids"] = SuggestedNextIds,
            ["context_updates"] = ContextUpdates,
            ["notes"] = Notes,
            ["failure_reason"] = FailureReason,
            ["blocking_question"] = BlockingQuestion is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["text"] = BlockingQuestion.Text,
                    ["context"] = BlockingQuestion.Context,
                    ["desired_format"] = BlockingQuestion.DesiredFormat
                },
            ["model"] = model,
            ["provider"] = provider,
            ["contract_validated"] = true,
            ["used_fallback"] = usedFallback,
            ["validation_error"] = validationError
        };
    }

    public static bool TryParseAssistantResponse(string text, out StageStatusContract? status, out string error)
    {
        status = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Assistant response was empty.";
            return false;
        }

        var json = ExtractJson(text);
        return TryParseJson(json, out status, out error);
    }

    public static bool TryParseJson(string json, out StageStatusContract? status, out string error)
    {
        status = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Stage status JSON was empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Stage status must be a JSON object.";
                return false;
            }

            var root = doc.RootElement;
            var statusText = ReadString(root, "status", "outcome");
            if (string.IsNullOrWhiteSpace(statusText) || !TryParseStatus(statusText, out var parsedStatus))
            {
                error = $"Invalid or missing status value '{statusText ?? "<null>"}'.";
                return false;
            }

            var preferred = ReadString(root, "preferred_next_label", "preferred_label") ?? string.Empty;
            var notes = ReadString(root, "notes") ?? string.Empty;
            var failureReason = ReadString(root, "failure_reason", "reason");
            var blockingQuestion = ReadBlockingQuestion(root);

            var suggested = new List<string>();
            if (TryReadProperty(root, out var suggestedEl, "suggested_next_ids", "suggestedNextIds") &&
                suggestedEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in suggestedEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            suggested.Add(value);
                    }
                }
            }

            var updates = new Dictionary<string, string>(StringComparer.Ordinal);
            if (TryReadProperty(root, out var updatesEl, "context_updates", "contextUpdates") &&
                updatesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in updatesEl.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        updates[property.Name] = property.Value.GetString() ?? string.Empty;
                    else
                        updates[property.Name] = property.Value.ToString();
                }
            }

            status = new StageStatusContract(
                Status: parsedStatus,
                PreferredNextLabel: preferred,
                SuggestedNextIds: suggested,
                ContextUpdates: updates,
                Notes: notes,
                FailureReason: failureReason,
                BlockingQuestion: blockingQuestion);

            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }

    public static string ToStatusString(OutcomeStatus status) => status switch
    {
        OutcomeStatus.Success => "success",
        OutcomeStatus.Retry => "retry",
        OutcomeStatus.Fail => "fail",
        OutcomeStatus.PartialSuccess => "partial_success",
        _ => status.ToString().ToLowerInvariant()
    };

    private static bool TryParseStatus(string value, out OutcomeStatus status)
    {
        var normalized = value.Trim().Replace('-', '_').ToLowerInvariant();
        switch (normalized)
        {
            case "success":
                status = OutcomeStatus.Success;
                return true;
            case "retry":
                status = OutcomeStatus.Retry;
                return true;
            case "fail":
            case "failure":
                status = OutcomeStatus.Fail;
                return true;
            case "partial_success":
            case "partialsuccess":
                status = OutcomeStatus.PartialSuccess;
                return true;
            default:
                status = OutcomeStatus.Fail;
                return false;
        }
    }

    private static string ExtractJson(string text)
    {
        // Prefer ```json fenced code blocks.
        var jsonStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonStart >= 0)
        {
            var contentStart = text.IndexOf('\n', jsonStart);
            if (contentStart >= 0)
            {
                var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (end > contentStart)
                    return text[(contentStart + 1)..end].Trim();
            }
        }

        // Fallback to generic fenced code block.
        jsonStart = text.IndexOf("```", StringComparison.Ordinal);
        if (jsonStart >= 0)
        {
            var contentStart = text.IndexOf('\n', jsonStart);
            if (contentStart >= 0)
            {
                var end = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (end > contentStart)
                    return text[(contentStart + 1)..end].Trim();
            }
        }

        // Final fallback: bracket scan.
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return text[firstBrace..(lastBrace + 1)].Trim();

        return text.Trim();
    }

    private static string? ReadString(JsonElement root, params string[] keys)
    {
        if (!TryReadProperty(root, out var value, keys))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static BlockingQuestion? ReadBlockingQuestion(JsonElement root)
    {
        if (!TryReadProperty(root, out var questionValue, "blocking_question", "blockingQuestion", "question"))
            return null;

        return questionValue.ValueKind switch
        {
            JsonValueKind.String => CreateBlockingQuestion(questionValue.GetString()),
            JsonValueKind.Object => ReadBlockingQuestionObject(questionValue),
            _ => null
        };
    }

    private static BlockingQuestion? ReadBlockingQuestionObject(JsonElement value)
    {
        var text = ReadString(value, "text", "prompt", "question");
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new BlockingQuestion(
            Text: text,
            Context: ReadString(value, "context", "details"),
            DesiredFormat: ReadString(value, "desired_format", "desiredFormat", "format"));
    }

    private static BlockingQuestion? CreateBlockingQuestion(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : new BlockingQuestion(text);
    }

    private static bool TryReadProperty(JsonElement root, out JsonElement value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out value))
                return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            var normalized = property.Name.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .ToLowerInvariant();

            foreach (var key in keys)
            {
                var normalizedKey = key.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .ToLowerInvariant();
                if (normalized == normalizedKey)
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
