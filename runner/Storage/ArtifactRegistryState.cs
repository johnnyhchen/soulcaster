namespace Soulcaster.Runner.Storage;

using System.Text.Json;

internal sealed record ArtifactRegistrySelection(
    string ArtifactId,
    string CurrentVersionId,
    string ApprovalState,
    string? Actor,
    string? Rationale,
    string? Source,
    string UpdatedAtUtc);

internal sealed record ArtifactPromotionRecord(
    string PromotionId,
    string ArtifactId,
    string ArtifactVersionId,
    string Action,
    string? Actor,
    string? Rationale,
    string? Source,
    string TimestampUtc);

internal sealed class ArtifactRegistryState
{
    public Dictionary<string, ArtifactRegistrySelection> Selections { get; init; } =
        new(StringComparer.Ordinal);

    public List<ArtifactPromotionRecord> History { get; init; } = [];
}

internal static class ArtifactRegistryStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string GetPath(string storeDirectory) =>
        Path.Combine(storeDirectory, "artifact_registry_state.json");

    public static ArtifactRegistryState Load(string storeDirectory)
    {
        var path = GetPath(storeDirectory);
        if (!File.Exists(path))
            return new ArtifactRegistryState();

        try
        {
            var payload = JsonSerializer.Deserialize<ArtifactRegistryState>(File.ReadAllText(path), Json);
            return payload ?? new ArtifactRegistryState();
        }
        catch
        {
            return new ArtifactRegistryState();
        }
    }

    public static async Task SaveAsync(string storeDirectory, ArtifactRegistryState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(storeDirectory);
        await File.WriteAllTextAsync(GetPath(storeDirectory), JsonSerializer.Serialize(state, Json), ct);
    }

    public static async Task<ArtifactRegistrySelection> PromoteAsync(
        string storeDirectory,
        string artifactId,
        string artifactVersionId,
        string action,
        string? actor,
        string? rationale,
        string? source,
        CancellationToken ct = default)
    {
        var state = Load(storeDirectory);
        var timestampUtc = DateTimeOffset.UtcNow.ToString("o");
        var selection = new ArtifactRegistrySelection(
            ArtifactId: artifactId,
            CurrentVersionId: artifactVersionId,
            ApprovalState: "approved",
            Actor: NormalizeOptional(actor),
            Rationale: NormalizeOptional(rationale),
            Source: NormalizeOptional(source) ?? "control-plane",
            UpdatedAtUtc: timestampUtc);

        state.Selections[artifactId] = selection;
        state.History.Add(new ArtifactPromotionRecord(
            PromotionId: $"{artifactId}:{artifactVersionId}:{timestampUtc}",
            ArtifactId: artifactId,
            ArtifactVersionId: artifactVersionId,
            Action: action,
            Actor: selection.Actor,
            Rationale: selection.Rationale,
            Source: selection.Source,
            TimestampUtc: timestampUtc));

        await SaveAsync(storeDirectory, state, ct);
        return selection;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
