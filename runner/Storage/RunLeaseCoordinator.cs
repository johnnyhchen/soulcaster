namespace Soulcaster.Runner.Storage;

using Microsoft.Data.Sqlite;

internal sealed record LeaseAcquireResult(
    bool Success,
    string LeaseId,
    long Generation,
    string? Error = null);

internal static class RunLeaseCoordinator
{
    public static string GetDatabasePath(string workingDirectory) =>
        Path.Combine(Path.GetFullPath(workingDirectory), "store", "workflow.sqlite");

    public static async Task<LeaseAcquireResult> TryAcquireAsync(
        string workingDirectory,
        string runId,
        int pid,
        Func<int, bool> isProcessAlive,
        CancellationToken ct = default)
    {
        var leaseId = $"{runId}:lease:primary";
        var dbPath = GetDatabasePath(workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var connection = CreateConnection(dbPath);
        await connection.OpenAsync(ct);
        await EnsureOwnershipSchemaAsync(connection, ct);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var current = await ReadOwnershipAsync(connection, transaction, runId, ct);

        if (current is { State: "active", OwnerPid: > 0 } active &&
            active.OwnerPid != pid &&
            isProcessAlive(active.OwnerPid))
        {
            await transaction.RollbackAsync(ct);
            return new LeaseAcquireResult(
                Success: false,
                LeaseId: leaseId,
                Generation: current.Generation,
                Error: $"Run is already active (pid={active.OwnerPid}).");
        }

        var generation = (current?.Generation ?? 0) + 1;
        var timestampUtc = DateTimeOffset.UtcNow.ToString("o");

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO lease_ownership (
                    run_id,
                    lease_id,
                    owner_pid,
                    acquired_at,
                    released_at,
                    generation,
                    state
                ) VALUES (
                    $run_id,
                    $lease_id,
                    $owner_pid,
                    $acquired_at,
                    NULL,
                    $generation,
                    'active'
                )
                ON CONFLICT(run_id) DO UPDATE SET
                    lease_id = excluded.lease_id,
                    owner_pid = excluded.owner_pid,
                    acquired_at = excluded.acquired_at,
                    released_at = NULL,
                    generation = excluded.generation,
                    state = 'active';
                """;
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$lease_id", leaseId);
            command.Parameters.AddWithValue("$owner_pid", pid);
            command.Parameters.AddWithValue("$acquired_at", timestampUtc);
            command.Parameters.AddWithValue("$generation", generation);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new LeaseAcquireResult(true, leaseId, generation);
    }

    public static async Task ReleaseAsync(
        string workingDirectory,
        string runId,
        int pid,
        CancellationToken ct = default)
    {
        var dbPath = GetDatabasePath(workingDirectory);
        if (!File.Exists(dbPath))
            return;

        await using var connection = CreateConnection(dbPath);
        await connection.OpenAsync(ct);
        await EnsureOwnershipSchemaAsync(connection, ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE lease_ownership
            SET
                released_at = $released_at,
                state = 'released'
            WHERE run_id = $run_id AND owner_pid = $owner_pid AND state = 'active';
            """;
        command.Parameters.AddWithValue("$released_at", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$owner_pid", pid);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<bool> IsActiveAsync(
        string workingDirectory,
        string runId,
        Func<int, bool> isProcessAlive,
        CancellationToken ct = default)
    {
        var dbPath = GetDatabasePath(workingDirectory);
        if (!File.Exists(dbPath))
            return false;

        await using var connection = CreateConnection(dbPath);
        await connection.OpenAsync(ct);
        await EnsureOwnershipSchemaAsync(connection, ct);

        var current = await ReadOwnershipAsync(connection, transaction: null, runId, ct);
        if (current is not { State: "active", OwnerPid: > 0 } active)
            return false;

        if (isProcessAlive(active.OwnerPid))
            return true;

        await ReleaseAsync(workingDirectory, runId, active.OwnerPid, ct);
        return false;
    }

    private static SqliteConnection CreateConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task EnsureOwnershipSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS lease_ownership (
                run_id TEXT PRIMARY KEY,
                lease_id TEXT NOT NULL,
                owner_pid INTEGER,
                acquired_at TEXT,
                released_at TEXT,
                generation INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<LeaseOwnershipRow?> ReadOwnershipAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT lease_id, owner_pid, acquired_at, released_at, generation, state
            FROM lease_ownership
            WHERE run_id = $run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new LeaseOwnershipRow(
            LeaseId: reader.GetString(0),
            OwnerPid: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            AcquiredAt: reader.IsDBNull(2) ? null : reader.GetString(2),
            ReleasedAt: reader.IsDBNull(3) ? null : reader.GetString(3),
            Generation: reader.GetInt64(4),
            State: reader.GetString(5));
    }

    private sealed record LeaseOwnershipRow(
        string LeaseId,
        int OwnerPid,
        string? AcquiredAt,
        string? ReleasedAt,
        long Generation,
        string State);
}
