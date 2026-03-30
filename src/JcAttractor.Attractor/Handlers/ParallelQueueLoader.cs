namespace JcAttractor.Attractor;

using System.Text.Json;

internal sealed record QueueWorkItem(
    int Index,
    string Id,
    Dictionary<string, string> ContextValues)
{
    public string DisplayName =>
        ContextValues.TryGetValue("queue.item.name", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : Id;
}

internal static class ParallelQueueLoader
{
    public static List<QueueWorkItem> Load(string queueSource, string logsRoot)
    {
        var resolvedSource = ResolveQueueSourcePath(queueSource, logsRoot);
        if (Directory.Exists(resolvedSource))
            return LoadDirectory(resolvedSource);

        if (File.Exists(resolvedSource))
            return LoadManifest(resolvedSource);

        throw new FileNotFoundException($"Queue source '{queueSource}' could not be resolved.", resolvedSource);
    }

    private static List<QueueWorkItem> LoadDirectory(string directoryPath)
    {
        var entries = Directory
            .GetFileSystemEntries(directoryPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<QueueWorkItem>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entryPath = Path.GetFullPath(entries[index]);
            var name = Path.GetFileName(entryPath);
            var itemId = string.IsNullOrWhiteSpace(name) ? $"item-{index + 1:D3}" : name;
            var contextValues = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["queue.item.index"] = index.ToString(),
                ["queue.item.id"] = itemId,
                ["queue.item.name"] = name,
                ["queue.item.value"] = entryPath,
                ["queue.item.path"] = entryPath,
                ["queue.item.kind"] = Directory.Exists(entryPath) ? "directory" : "file",
                ["queue.item.source"] = directoryPath
            };
            items.Add(new QueueWorkItem(index, itemId, contextValues));
        }

        return items;
    }

    private static List<QueueWorkItem> LoadManifest(string manifestPath)
    {
        var extension = Path.GetExtension(manifestPath);
        return extension.ToLowerInvariant() switch
        {
            ".json" => LoadJsonManifest(manifestPath),
            ".jsonl" => LoadJsonLinesManifest(manifestPath),
            _ => LoadTextManifest(manifestPath)
        };
    }

    private static List<QueueWorkItem> LoadJsonManifest(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
            return LoadJsonArray(manifestPath, root);

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("items", out var itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Array)
        {
            return LoadJsonArray(manifestPath, itemsElement);
        }

        throw new InvalidOperationException($"Queue manifest '{manifestPath}' must be a JSON array or object with an 'items' array.");
    }

    private static List<QueueWorkItem> LoadJsonLinesManifest(string manifestPath)
    {
        var items = new List<QueueWorkItem>();
        var lines = File.ReadAllLines(manifestPath);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            items.Add(CreateWorkItem(items.Count, manifestPath, document.RootElement));
        }

        return items;
    }

    private static List<QueueWorkItem> LoadTextManifest(string manifestPath)
    {
        var items = new List<QueueWorkItem>();
        foreach (var rawLine in File.ReadAllLines(manifestPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            items.Add(CreateWorkItem(items.Count, manifestPath, line));
        }

        return items;
    }

    private static List<QueueWorkItem> LoadJsonArray(string manifestPath, JsonElement arrayElement)
    {
        var items = new List<QueueWorkItem>();
        foreach (var itemElement in arrayElement.EnumerateArray())
        {
            items.Add(CreateWorkItem(items.Count, manifestPath, itemElement));
        }

        return items;
    }

    private static QueueWorkItem CreateWorkItem(int index, string manifestPath, JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => CreateWorkItem(index, manifestPath, element.GetString() ?? string.Empty),
            JsonValueKind.Object => CreateObjectWorkItem(index, manifestPath, element),
            _ => CreateWorkItem(index, manifestPath, element.GetRawText())
        };
    }

    private static QueueWorkItem CreateWorkItem(int index, string manifestPath, string rawValue)
    {
        var contextValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["queue.item.index"] = index.ToString(),
            ["queue.item.value"] = rawValue,
            ["queue.item.source"] = manifestPath,
            ["queue.item.kind"] = "manifest"
        };

        var resolvedPath = TryResolveManifestItemPath(rawValue, manifestPath);
        if (!string.IsNullOrWhiteSpace(resolvedPath))
        {
            contextValues["queue.item.path"] = resolvedPath;
            contextValues["queue.item.name"] = Path.GetFileName(resolvedPath);
        }

        var itemId = BuildItemId(index, contextValues, rawValue);
        contextValues["queue.item.id"] = itemId;
        contextValues.TryAdd("queue.item.name", itemId);

        return new QueueWorkItem(index, itemId, contextValues);
    }

    private static QueueWorkItem CreateObjectWorkItem(int index, string manifestPath, JsonElement element)
    {
        var contextValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["queue.item.index"] = index.ToString(),
            ["queue.item.source"] = manifestPath,
            ["queue.item.kind"] = "manifest"
        };

        foreach (var property in element.EnumerateObject())
        {
            var normalizedKey = NormalizeQueueField(property.Name);
            var stringValue = ConvertJsonElementToString(property.Value);
            contextValues[$"queue.item.{normalizedKey}"] = stringValue;
        }

        if (contextValues.TryGetValue("queue.item.path", out var pathValue) &&
            !string.IsNullOrWhiteSpace(pathValue))
        {
            contextValues["queue.item.path"] = ResolveManifestRelativePath(pathValue, manifestPath);
        }

        if (!contextValues.ContainsKey("queue.item.value"))
        {
            if (contextValues.TryGetValue("queue.item.path", out var itemPath))
                contextValues["queue.item.value"] = itemPath;
            else if (contextValues.TryGetValue("queue.item.name", out var itemName))
                contextValues["queue.item.value"] = itemName;
        }

        if (contextValues.TryGetValue("queue.item.path", out var resolvedPath))
            contextValues.TryAdd("queue.item.name", Path.GetFileName(resolvedPath));

        var itemId = BuildItemId(index, contextValues, $"item-{index + 1:D3}");
        contextValues["queue.item.id"] = itemId;
        contextValues.TryAdd("queue.item.name", itemId);

        return new QueueWorkItem(index, itemId, contextValues);
    }

    private static string ResolveQueueSourcePath(string queueSource, string logsRoot)
    {
        if (Path.IsPathRooted(queueSource))
            return Path.GetFullPath(queueSource);

        var outputRoot = Path.GetDirectoryName(logsRoot) ?? logsRoot;
        var outputRelative = Path.GetFullPath(Path.Combine(outputRoot, queueSource));
        if (File.Exists(outputRelative) || Directory.Exists(outputRelative))
            return outputRelative;

        return Path.GetFullPath(Path.Combine(logsRoot, queueSource));
    }

    private static string? TryResolveManifestItemPath(string rawValue, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var candidate = ResolveManifestRelativePath(rawValue, manifestPath);
        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : null;
    }

    private static string ResolveManifestRelativePath(string rawPath, string manifestPath)
    {
        if (Path.IsPathRooted(rawPath))
            return Path.GetFullPath(rawPath);

        var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(manifestDirectory, rawPath));
    }

    private static string BuildItemId(int index, IReadOnlyDictionary<string, string> contextValues, string fallback)
    {
        if (contextValues.TryGetValue("queue.item.id", out var explicitId) && !string.IsNullOrWhiteSpace(explicitId))
            return explicitId;
        if (contextValues.TryGetValue("queue.item.name", out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        if (contextValues.TryGetValue("queue.item.path", out var path) && !string.IsNullOrWhiteSpace(path))
            return Path.GetFileName(path);
        if (contextValues.TryGetValue("queue.item.value", out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        return string.IsNullOrWhiteSpace(fallback) ? $"item-{index + 1:D3}" : fallback;
    }

    private static string NormalizeQueueField(string name)
    {
        var chars = name
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();

        var normalized = new string(chars);
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);

        return normalized.Trim('_');
    }

    private static string ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.GetRawText()
        };
    }
}
