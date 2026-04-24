namespace Soulcaster.Runner.Storage;

using System.Text.Json;
using Microsoft.Data.Sqlite;

internal sealed record DurableRunStateSnapshot(
    RunManifest Manifest,
    long Version);

internal sealed class RunStateConflictException : Exception
{
    public RunStateConflictException(string runId, long? expectedVersion, long currentVersion)
        : base(expectedVersion is null
            ? $"Run '{runId}' changed during mutation. Current version is {currentVersion}."
            : $"Expected run '{runId}' to be at version {expectedVersion.Value}, but current version is {currentVersion}.")
    {
        RunId = runId;
        ExpectedVersion = expectedVersion;
        CurrentVersion = currentVersion;
    }

    public string RunId { get; }

    public long? ExpectedVersion { get; }

    public long CurrentVersion { get; }
}

internal sealed class SqliteRunStateStore
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SqliteRunStateStore(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        StoreDirectory = Path.Combine(WorkingDirectory, "store");
        DatabasePath = Path.Combine(StoreDirectory, "workflow.sqlite");
    }

    public string WorkingDirectory { get; }

    public string StoreDirectory { get; }

    public string DatabasePath { get; }

    public async Task<DurableRunStateSnapshot> EnsureInitializedAsync(
        string manifestPath,
        RunManifest manifest,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.run_id))
            throw new InvalidOperationException("Run manifest must have a run_id before durable state initialization.");

        await _syncLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(StoreDirectory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            var current = await LoadInternalAsync(connection, manifest.run_id, ct);
            if (current is not null)
            {
                current.Manifest.Save(manifestPath);
                return current;
            }

            var initialized = Clone(manifest);
            initialized.state_version = Math.Max(1, initialized.state_version);
            if (string.IsNullOrWhiteSpace(initialized.updated_at))
                initialized.updated_at = DateTimeOffset.UtcNow.ToString("o");

            await InsertAsync(connection, initialized, initialized.state_version, ct);
            initialized.Save(manifestPath);
            return new DurableRunStateSnapshot(initialized, initialized.state_version);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<DurableRunStateSnapshot?> LoadAsync(string runId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        await _syncLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(DatabasePath))
                return null;

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);
            return await LoadInternalAsync(connection, runId, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<DurableRunStateSnapshot> PersistAsync(
        string manifestPath,
        RunManifest manifest,
        bool incrementVersion = true,
        bool preserveCancellationMarkers = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.run_id))
            throw new InvalidOperationException("Run manifest must have a run_id before persistence.");

        await _syncLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(StoreDirectory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            var current = await LoadInternalAsync(connection, manifest.run_id, ct);
            var merged = preserveCancellationMarkers
                ? MergeState(current?.Manifest, manifest)
                : Clone(manifest);
            var targetVersion = current is null
                ? Math.Max(1, merged.state_version)
                : current.Version + (incrementVersion ? 1 : 0);

            merged.state_version = targetVersion;
            if (current is null)
                await InsertAsync(connection, merged, targetVersion, ct);
            else
                await UpdateAsync(connection, merged, current.Version, targetVersion, ct);

            merged.Save(manifestPath);
            return new DurableRunStateSnapshot(merged, targetVersion);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<DurableRunStateSnapshot> MutateAsync(
        string manifestPath,
        string runId,
        long? expectedVersion,
        Func<RunManifest, bool> mutator,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new InvalidOperationException("Run ID is required for mutation.");

        await _syncLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(StoreDirectory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            var current = await LoadInternalAsync(connection, runId, ct);
            if (current is null)
            {
                var fileManifest = RunManifest.Load(manifestPath)
                    ?? throw new InvalidOperationException($"Run manifest not found under '{manifestPath}'.");
                current = await BootstrapFromFileAsync(connection, fileManifest, ct);
            }

            if (expectedVersion is not null && expectedVersion.Value != current.Version)
                throw new RunStateConflictException(runId, expectedVersion, current.Version);

            var next = Clone(current.Manifest);
            if (!mutator(next))
                return current;

            next.state_version = current.Version + 1;
            if (string.IsNullOrWhiteSpace(next.updated_at))
                next.updated_at = DateTimeOffset.UtcNow.ToString("o");

            var changed = await TryUpdateAsync(connection, next, current.Version, next.state_version, ct);
            if (!changed)
            {
                var reloaded = await LoadInternalAsync(connection, runId, ct);
                throw new RunStateConflictException(runId, expectedVersion, reloaded?.Version ?? current.Version);
            }

            next.Save(manifestPath);
            return new DurableRunStateSnapshot(next, next.state_version);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS run_state (
                run_id TEXT PRIMARY KEY,
                version INTEGER NOT NULL,
                status TEXT,
                active_stage TEXT,
                updated_at TEXT,
                started_at TEXT,
                graph_path TEXT,
                checkpoint_path TEXT,
                result_path TEXT,
                crash TEXT,
                auto_resume_policy TEXT,
                resume_source TEXT,
                respawn_count INTEGER,
                last_respawned_at TEXT,
                backend_mode TEXT,
                backend_script_path TEXT,
                pid INTEGER,
                cancel_requested_at TEXT,
                cancel_requested_actor TEXT,
                cancel_requested_rationale TEXT,
                cancel_requested_source TEXT,
                payload_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DurableRunStateSnapshot?> LoadInternalAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT version, payload_json
            FROM run_state
            WHERE run_id = $run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var version = reader.GetInt64(0);
        var payload = reader.GetString(1);
        var manifest = JsonSerializer.Deserialize<RunManifest>(payload)
            ?? throw new InvalidOperationException($"Stored run state for '{runId}' could not be deserialized.");
        manifest.state_version = version;
        return new DurableRunStateSnapshot(manifest, version);
    }

    private static async Task<DurableRunStateSnapshot> BootstrapFromFileAsync(
        SqliteConnection connection,
        RunManifest manifest,
        CancellationToken ct)
    {
        manifest.state_version = Math.Max(1, manifest.state_version);
        await InsertAsync(connection, manifest, manifest.state_version, ct);
        return new DurableRunStateSnapshot(Clone(manifest), manifest.state_version);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        RunManifest manifest,
        long version,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO run_state (
                run_id,
                version,
                status,
                active_stage,
                updated_at,
                started_at,
                graph_path,
                checkpoint_path,
                result_path,
                crash,
                auto_resume_policy,
                resume_source,
                respawn_count,
                last_respawned_at,
                backend_mode,
                backend_script_path,
                pid,
                cancel_requested_at,
                cancel_requested_actor,
                cancel_requested_rationale,
                cancel_requested_source,
                payload_json
            ) VALUES (
                $run_id,
                $version,
                $status,
                $active_stage,
                $updated_at,
                $started_at,
                $graph_path,
                $checkpoint_path,
                $result_path,
                $crash,
                $auto_resume_policy,
                $resume_source,
                $respawn_count,
                $last_respawned_at,
                $backend_mode,
                $backend_script_path,
                $pid,
                $cancel_requested_at,
                $cancel_requested_actor,
                $cancel_requested_rationale,
                $cancel_requested_source,
                $payload_json
            );
            """;
        Bind(command, manifest, version);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAsync(
        SqliteConnection connection,
        RunManifest manifest,
        long currentVersion,
        long targetVersion,
        CancellationToken ct)
    {
        var changed = await TryUpdateAsync(connection, manifest, currentVersion, targetVersion, ct);
        if (!changed)
        {
            var current = await LoadInternalAsync(connection, manifest.run_id, ct);
            throw new RunStateConflictException(manifest.run_id, currentVersion, current?.Version ?? currentVersion);
        }
    }

    private static async Task<bool> TryUpdateAsync(
        SqliteConnection connection,
        RunManifest manifest,
        long currentVersion,
        long targetVersion,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_state
            SET
                version = $version,
                status = $status,
                active_stage = $active_stage,
                updated_at = $updated_at,
                started_at = $started_at,
                graph_path = $graph_path,
                checkpoint_path = $checkpoint_path,
                result_path = $result_path,
                crash = $crash,
                auto_resume_policy = $auto_resume_policy,
                resume_source = $resume_source,
                respawn_count = $respawn_count,
                last_respawned_at = $last_respawned_at,
                backend_mode = $backend_mode,
                backend_script_path = $backend_script_path,
                pid = $pid,
                cancel_requested_at = $cancel_requested_at,
                cancel_requested_actor = $cancel_requested_actor,
                cancel_requested_rationale = $cancel_requested_rationale,
                cancel_requested_source = $cancel_requested_source,
                payload_json = $payload_json
            WHERE run_id = $run_id AND version = $current_version;
            """;
        Bind(command, manifest, targetVersion);
        command.Parameters.AddWithValue("$current_version", currentVersion);
        var affected = await command.ExecuteNonQueryAsync(ct);
        return affected == 1;
    }

    private static void Bind(SqliteCommand command, RunManifest manifest, long version)
    {
        AddValue(command, "$run_id", manifest.run_id);
        AddValue(command, "$version", version);
        AddValue(command, "$status", manifest.status);
        AddValue(command, "$active_stage", manifest.active_stage);
        AddValue(command, "$updated_at", manifest.updated_at);
        AddValue(command, "$started_at", manifest.started_at);
        AddValue(command, "$graph_path", manifest.graph_path);
        AddValue(command, "$checkpoint_path", manifest.checkpoint_path);
        AddValue(command, "$result_path", manifest.result_path);
        AddValue(command, "$crash", manifest.crash);
        AddValue(command, "$auto_resume_policy", manifest.auto_resume_policy);
        AddValue(command, "$resume_source", manifest.resume_source);
        AddValue(command, "$respawn_count", manifest.respawn_count);
        AddValue(command, "$last_respawned_at", manifest.last_respawned_at);
        AddValue(command, "$backend_mode", manifest.backend_mode);
        AddValue(command, "$backend_script_path", manifest.backend_script_path);
        AddValue(command, "$pid", manifest.pid);
        AddValue(command, "$cancel_requested_at", manifest.cancel_requested_at);
        AddValue(command, "$cancel_requested_actor", manifest.cancel_requested_actor);
        AddValue(command, "$cancel_requested_rationale", manifest.cancel_requested_rationale);
        AddValue(command, "$cancel_requested_source", manifest.cancel_requested_source);
        AddValue(command, "$payload_json", JsonSerializer.Serialize(manifest, RunnerJson.Options));
    }

    private static void AddValue(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static RunManifest MergeState(RunManifest? current, RunManifest incoming)
    {
        var merged = Clone(incoming);
        if (current is null)
            return merged;

        if (string.IsNullOrWhiteSpace(merged.cancel_requested_at))
            merged.cancel_requested_at = current.cancel_requested_at;
        if (string.IsNullOrWhiteSpace(merged.cancel_requested_actor))
            merged.cancel_requested_actor = current.cancel_requested_actor;
        if (string.IsNullOrWhiteSpace(merged.cancel_requested_rationale))
            merged.cancel_requested_rationale = current.cancel_requested_rationale;
        if (string.IsNullOrWhiteSpace(merged.cancel_requested_source))
            merged.cancel_requested_source = current.cancel_requested_source;

        return merged;
    }

    private static RunManifest Clone(RunManifest manifest)
    {
        return JsonSerializer.Deserialize<RunManifest>(JsonSerializer.Serialize(manifest, RunnerJson.Options))
               ?? new RunManifest();
    }
}
