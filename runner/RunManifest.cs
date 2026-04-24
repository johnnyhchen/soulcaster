using System.Text.Json;

namespace Soulcaster.Runner;

public enum AutoResumePolicy
{
    Off,
    On,
    Always
}

public sealed class RunLock
{
    public int pid { get; set; }
    public string started_at { get; set; } = "";
}

public sealed class RunManifest
{
    public string run_id { get; set; } = "";
    public long state_version { get; set; }
    public int pid { get; set; }
    public string graph_path { get; set; } = "";
    public string started_at { get; set; } = "";
    public string updated_at { get; set; } = "";
    public string active_stage { get; set; } = "";
    public string status { get; set; } = "";
    public string? crash { get; set; }
    public string auto_resume_policy { get; set; } = "on";
    public string resume_source { get; set; } = "fresh";
    public string? checkpoint_path { get; set; }
    public string? result_path { get; set; }
    public int respawn_count { get; set; }
    public string? last_respawned_at { get; set; }
    public string? backend_mode { get; set; }
    public string? backend_script_path { get; set; }
    public string? crash_after_stage { get; set; }
    public int crash_injections_remaining { get; set; }
    public string? cancel_requested_at { get; set; }
    public string? cancel_requested_actor { get; set; }
    public string? cancel_requested_rationale { get; set; }
    public string? cancel_requested_source { get; set; }

    public static RunManifest? Load(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return null;

        return JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath));
    }

    public void Save(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(this, RunnerJson.Options));
    }
}

public static class RunnerJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true };
}
