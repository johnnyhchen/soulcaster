namespace Soulcaster.CodingAgent.Profiles;

using System.Text.Json;
using Soulcaster.UnifiedLlm;

internal static class ValidationTools
{
    private const string CommandKind = "command";
    private const string FileExistsKind = "file_exists";
    private const string FileContentKind = "file_content";
    private const string DiffKind = "diff";
    private const string ArtifactKind = "artifact";
    private const string SchemaKind = "schema";

    public static void Register(ToolRegistry registry)
    {
        registry.Register(new RegisteredTool(
            "queue_validation_check",
            new ToolDefinition(
                "queue_validation_check",
                "Registers a runtime-owned validation check for the post-work validation segment. This does not execute the check immediately.",
                new List<ToolParameter>
                {
                    new("kind", "string", "Validation check kind: command, file_exists, file_content, diff, artifact, or schema.", true),
                    new("name", "string", "Short name for this validation check.", false),
                    new("command", "string", "Command to execute during the validation segment when kind='command'.", false),
                    new("path", "string", "Single path target for file, diff, artifact, or schema checks.", false),
                    new("paths", "array", "Multiple path targets for file, diff, or artifact checks.", false, ItemsType: "string"),
                    new("workdir", "string", "Optional working directory or base directory for relative paths.", false),
                    new("contains_text", "string", "Expected literal text when kind='file_content'.", false),
                    new("matches_regex", "string", "Expected regex when kind='file_content'.", false),
                    new("json_path", "string", "Expected JSON path when kind='file_content'.", false),
                    new("expected_value_json", "string", "Expected JSON value for the selected json_path.", false),
                    new("expected_schema_json", "string", "Expected JSON schema when kind='schema'.", false),
                    new("require_any_change", "boolean", "For diff checks with multiple paths, pass when any listed path changed.", false),
                    new("timeout_ms", "integer", "Optional timeout in milliseconds for command checks.", false),
                    new("required", "boolean", "Whether this check is required for stage success.", false)
                }),
            async (args, _) =>
            {
                using var doc = JsonDocument.Parse(args);
                var root = doc.RootElement;

                var kind = NormalizeKind(TryGetString(root, "kind"));
                var name = TryGetString(root, "name");
                var command = TryGetString(root, "command");
                var path = TryGetString(root, "path");
                var paths = TryReadStringList(root, "paths");
                var workdir = TryGetString(root, "workdir");
                var containsText = TryGetString(root, "contains_text");
                var matchesRegex = TryGetString(root, "matches_regex");
                var jsonPath = TryGetString(root, "json_path");
                var expectedValueJson = TryGetString(root, "expected_value_json");
                var expectedSchemaJson = TryGetString(root, "expected_schema_json");
                var requireAnyChange = root.TryGetProperty("require_any_change", out var anyChangeEl) &&
                                       anyChangeEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                       anyChangeEl.GetBoolean();
                var timeoutMs = TryReadInt(root, "timeout_ms");
                var required = root.TryGetProperty("required", out var requiredEl) &&
                               requiredEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                               requiredEl.GetBoolean();

                Validate(kind, command, path, paths, containsText, matchesRegex, jsonPath, expectedSchemaJson);

                await Task.CompletedTask;
                return JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["queued"] = true,
                    ["kind"] = kind,
                    ["name"] = string.IsNullOrWhiteSpace(name) ? $"queued-{kind}" : name,
                    ["command"] = command,
                    ["path"] = path,
                    ["paths"] = paths,
                    ["workdir"] = workdir,
                    ["contains_text"] = containsText,
                    ["matches_regex"] = matchesRegex,
                    ["json_path"] = jsonPath,
                    ["expected_value_json"] = expectedValueJson,
                    ["expected_schema_json"] = expectedSchemaJson,
                    ["require_any_change"] = requireAnyChange,
                    ["timeout_ms"] = timeoutMs,
                    ["required"] = required
                });
            }));
    }

    private static void Validate(
        string kind,
        string? command,
        string? path,
        IReadOnlyList<string>? paths,
        string? containsText,
        string? matchesRegex,
        string? jsonPath,
        string? expectedSchemaJson)
    {
        var pathCount = (string.IsNullOrWhiteSpace(path) ? 0 : 1) + (paths?.Count ?? 0);

        switch (kind)
        {
            case CommandKind when string.IsNullOrWhiteSpace(command):
                throw new InvalidOperationException("queue_validation_check requires a non-empty command for kind='command'.");
            case FileExistsKind or ArtifactKind when pathCount == 0:
                throw new InvalidOperationException($"queue_validation_check requires path or paths for kind='{kind}'.");
            case FileContentKind when pathCount != 1:
                throw new InvalidOperationException("queue_validation_check requires exactly one path for kind='file_content'.");
            case FileContentKind when
                string.IsNullOrWhiteSpace(containsText) &&
                string.IsNullOrWhiteSpace(matchesRegex) &&
                string.IsNullOrWhiteSpace(jsonPath):
                throw new InvalidOperationException("queue_validation_check requires contains_text, matches_regex, or json_path for kind='file_content'.");
            case SchemaKind when pathCount != 1 || string.IsNullOrWhiteSpace(expectedSchemaJson):
                throw new InvalidOperationException("queue_validation_check requires exactly one path and expected_schema_json for kind='schema'.");
        }
    }

    private static string NormalizeKind(string? kind)
    {
        return kind?.Trim().ToLowerInvariant() switch
        {
            FileExistsKind => FileExistsKind,
            FileContentKind => FileContentKind,
            DiffKind => DiffKind,
            ArtifactKind => ArtifactKind,
            SchemaKind => SchemaKind,
            _ => CommandKind
        };
    }

    private static string? TryGetString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static int? TryReadInt(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadStringList(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrWhiteSpace(single) ? Array.Empty<string>() : new[] { single };
        }

        if (value.ValueKind != JsonValueKind.Array)
            return null;

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToList();
    }
}
