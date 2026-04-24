namespace Soulcaster.Runner.Storage;

using System.Text.Json;
using Microsoft.Data.Sqlite;

internal sealed record OperatorMutationRecord(
    string RunId,
    string MutationType,
    string MutationStatus,
    string? NodeId,
    string? TargetNodeId,
    string? Actor,
    string? Rationale,
    string? Source,
    string? Message,
    long? RunVersion = null,
    string? ArtifactId = null,
    string? ArtifactVersionId = null,
    string? CreatedAtUtc = null);

internal static class OperatorMutationStore
{
    public static async Task RecordAsync(
        string workingDirectory,
        OperatorMutationRecord record,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(record);

        var databasePath = Path.Combine(Path.GetFullPath(workingDirectory), "store", "workflow.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);

        var createdAtUtc = string.IsNullOrWhiteSpace(record.CreatedAtUtc)
            ? DateTimeOffset.UtcNow.ToString("o")
            : record.CreatedAtUtc;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mutation_id"] = $"{record.RunId}:mutation:{Guid.NewGuid():N}",
            ["run_id"] = record.RunId,
            ["mutation_type"] = record.MutationType,
            ["mutation_status"] = record.MutationStatus,
            ["node_id"] = record.NodeId,
            ["target_node_id"] = record.TargetNodeId,
            ["actor"] = record.Actor,
            ["rationale"] = record.Rationale,
            ["source"] = record.Source,
            ["message"] = record.Message,
            ["artifact_id"] = record.ArtifactId,
            ["artifact_version_id"] = record.ArtifactVersionId,
            ["run_version"] = record.RunVersion,
            ["created_at"] = createdAtUtc
        };

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operator_mutations (
                mutation_id,
                run_id,
                mutation_type,
                mutation_status,
                node_id,
                target_node_id,
                actor,
                rationale,
                source,
                message,
                artifact_id,
                artifact_version_id,
                run_version,
                created_at,
                payload_json
            ) VALUES (
                $mutation_id,
                $run_id,
                $mutation_type,
                $mutation_status,
                $node_id,
                $target_node_id,
                $actor,
                $rationale,
                $source,
                $message,
                $artifact_id,
                $artifact_version_id,
                $run_version,
                $created_at,
                $payload_json
            );
            """;
        command.Parameters.AddWithValue("$mutation_id", payload["mutation_id"]!);
        command.Parameters.AddWithValue("$run_id", record.RunId);
        command.Parameters.AddWithValue("$mutation_type", record.MutationType);
        command.Parameters.AddWithValue("$mutation_status", record.MutationStatus);
        AddValue(command, "$node_id", record.NodeId);
        AddValue(command, "$target_node_id", record.TargetNodeId);
        AddValue(command, "$actor", record.Actor);
        AddValue(command, "$rationale", record.Rationale);
        AddValue(command, "$source", record.Source);
        AddValue(command, "$message", record.Message);
        AddValue(command, "$artifact_id", record.ArtifactId);
        AddValue(command, "$artifact_version_id", record.ArtifactVersionId);
        AddValue(command, "$run_version", record.RunVersion);
        command.Parameters.AddWithValue("$created_at", createdAtUtc);
        command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS operator_mutations (
                mutation_id TEXT PRIMARY KEY,
                run_id TEXT,
                mutation_type TEXT,
                mutation_status TEXT,
                node_id TEXT,
                target_node_id TEXT,
                actor TEXT,
                rationale TEXT,
                source TEXT,
                message TEXT,
                artifact_id TEXT,
                artifact_version_id TEXT,
                run_version INTEGER,
                created_at TEXT,
                payload_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_operator_mutations_type_time
                ON operator_mutations (mutation_type, created_at DESC);

            CREATE INDEX IF NOT EXISTS idx_operator_mutations_run_time
                ON operator_mutations (run_id, created_at DESC);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SqliteConnection CreateConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return new SqliteConnection(builder.ToString());
    }

    private static void AddValue(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
