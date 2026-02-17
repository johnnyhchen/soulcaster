namespace JcAttractor.Attractor;

using System.Text.Json;

public record Checkpoint(
    string CurrentNodeId,
    List<string> CompletedNodes,
    Dictionary<string, string> ContextData,
    Dictionary<string, int> RetryCounts
)
{
    public void Save(string logsRoot)
    {
        var path = Path.Combine(logsRoot, "checkpoint.json");
        Directory.CreateDirectory(logsRoot);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Checkpoint? Load(string logsRoot)
    {
        var path = Path.Combine(logsRoot, "checkpoint.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path));
    }
}
