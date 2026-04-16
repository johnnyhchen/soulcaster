using System.Text.Json;
using Soulcaster.Attractor;

namespace Soulcaster.Tests.Helpers;

internal static class ScenarioRunner
{
    public static async Task<ScenarioRun> RunDotAsync(
        string dotSource,
        DeterministicBackend backend,
        Checkpoint? checkpoint = null,
        string? logsRoot = null,
        IInterviewer? interviewer = null,
        CancellationToken ct = default)
    {
        var root = logsRoot ?? Path.Combine(Path.GetTempPath(), $"jc_scenario_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        checkpoint?.Save(root);

        var graph = DotParser.Parse(dotSource);
        var engine = new PipelineEngine(new PipelineConfig(
            LogsRoot: root,
            Backend: backend,
            Interviewer: interviewer,
            Transforms: new List<IGraphTransform>
            {
                new StylesheetTransform(),
                new VariableExpansionTransform()
            }));

        var result = await engine.RunAsync(graph, ct);
        var events = ReadEvents(Path.Combine(root, "events.jsonl"));
        var stageOrder = events
            .Where(evt => string.Equals(evt.GetValueOrDefault("event_type")?.ToString(), "stage_start", StringComparison.Ordinal))
            .Select(evt => evt.GetValueOrDefault("node_id")?.ToString() ?? string.Empty)
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .ToList();

        return new ScenarioRun(root, graph, result, stageOrder, events, backend.Invocations.ToList());
    }

    private static List<Dictionary<string, object?>> ReadEvents(string path)
    {
        var events = new List<Dictionary<string, object?>>();
        if (!File.Exists(path))
            return events;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            events.Add(ConvertObject(doc.RootElement));
        }

        return events;
    }

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            dict[property.Name] = ConvertValue(property.Value);
        return dict;
    }

    private static object? ConvertValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(value),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertValue).ToList(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var i64) => i64,
            JsonValueKind.Number when value.TryGetDouble(out var dbl) => dbl,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }
}

internal sealed class ScenarioRun : IDisposable
{
    public ScenarioRun(
        string logsRoot,
        Graph graph,
        PipelineResult result,
        IReadOnlyList<string> stageOrder,
        IReadOnlyList<Dictionary<string, object?>> events,
        IReadOnlyList<DeterministicInvocation> backendInvocations)
    {
        LogsRoot = logsRoot;
        Graph = graph;
        Result = result;
        StageOrder = stageOrder;
        Events = events;
        BackendInvocations = backendInvocations;
    }

    public string LogsRoot { get; }
    public Graph Graph { get; }
    public PipelineResult Result { get; }
    public IReadOnlyList<string> StageOrder { get; }
    public IReadOnlyList<Dictionary<string, object?>> Events { get; }
    public IReadOnlyList<DeterministicInvocation> BackendInvocations { get; }

    public void Dispose()
    {
        if (Directory.Exists(LogsRoot))
            Directory.Delete(LogsRoot, true);
    }
}
