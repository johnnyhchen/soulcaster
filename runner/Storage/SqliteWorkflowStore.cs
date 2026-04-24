namespace Soulcaster.Runner.Storage;

using System.Text.Json;
using Microsoft.Data.Sqlite;

internal sealed class SqliteWorkflowStore : IWorkflowStore
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SqliteWorkflowStore(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        StoreDirectory = Path.Combine(WorkingDirectory, "store");
        DatabasePath = Path.Combine(StoreDirectory, "workflow.sqlite");
    }

    public string BackendId => "sqlite_workflow_store";

    public string WorkingDirectory { get; }

    public string StoreDirectory { get; }

    public string DatabasePath { get; }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(StoreDirectory);
            await EnsureProjectionFilesAsync(ct);

            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            await EnsureSchemaAsync(connection, ct);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await ClearProjectionTablesAsync(connection, transaction, ct);

            await ImportProjectionAsync("runs", () => ImportRunsAsync(connection, transaction, Path.Combine(StoreDirectory, "runs.json"), ct));
            await ImportProjectionAsync("stage_attempts", () => ImportStageAttemptsAsync(connection, transaction, Path.Combine(StoreDirectory, "stage_attempts.json"), ct));
            await ImportProjectionAsync("run_events", () => ImportRunEventsAsync(connection, transaction, Path.Combine(StoreDirectory, "run_events.jsonl"), ct));
            await ImportProjectionAsync("gate_instances", () => ImportGatesAsync(connection, transaction, Path.Combine(StoreDirectory, "gate_instances.json"), ct));
            await ImportProjectionAsync("gate_answers", () => ImportGateAnswersAsync(connection, transaction, Path.Combine(StoreDirectory, "gate_answers.json"), ct));
            await ImportProjectionAsync("artifacts", () => ImportArtifactsAsync(connection, transaction, Path.Combine(StoreDirectory, "artifacts.json"), ct));
            await ImportProjectionAsync("artifact_versions", () => ImportArtifactVersionsAsync(connection, transaction, Path.Combine(StoreDirectory, "artifact_versions.json"), ct));
            await ImportProjectionAsync("artifact_lineage", () => ImportArtifactLineageAsync(connection, transaction, Path.Combine(StoreDirectory, "artifact_lineage.json"), ct));
            await ImportProjectionAsync("artifact_promotions", () => ImportArtifactPromotionsAsync(connection, transaction, ArtifactRegistryStateStore.GetPath(StoreDirectory), ct));
            await ImportProjectionAsync("agent_sessions", () => ImportAgentSessionsAsync(connection, transaction, Path.Combine(StoreDirectory, "agent_sessions.json"), ct));
            await ImportProjectionAsync("provider_invocations", () => ImportProviderInvocationsAsync(connection, transaction, Path.Combine(StoreDirectory, "provider_invocations.json"), ct));
            await ImportProjectionAsync("model_scorecards", () => ImportModelScorecardsAsync(connection, transaction, Path.Combine(StoreDirectory, "model_scorecards.json"), ct));
            await ImportProjectionAsync("leases", () => ImportLeasesAsync(connection, transaction, Path.Combine(StoreDirectory, "leases.json"), ct));
            await ImportProjectionAsync("replay_events", () => ImportReplayEventsAsync(connection, transaction, Path.Combine(StoreDirectory, "replay.json"), ct));
            await UpdateMetadataAsync(connection, transaction, ct);

            await transaction.CommitAsync(ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task EnsureProjectionFilesAsync(CancellationToken ct)
    {
        var runsPath = Path.Combine(StoreDirectory, "runs.json");
        if (File.Exists(runsPath))
            return;

        var fileStore = new FileWorkflowStore(WorkingDirectory);
        await fileStore.SyncAsync(ct);
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
        var statements = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                state_version INTEGER,
                status TEXT,
                active_stage TEXT,
                started_at TEXT,
                updated_at TEXT,
                checkpoint_path TEXT,
                result_path TEXT,
                crash TEXT,
                cancel_requested_at TEXT,
                cancel_requested_actor TEXT,
                cancel_requested_rationale TEXT,
                cancel_requested_source TEXT,
                auto_resume_policy TEXT,
                resume_source TEXT,
                respawn_count INTEGER,
                event_count INTEGER,
                stage_attempt_count INTEGER,
                gate_count INTEGER,
                artifact_version_count INTEGER,
                store_backend TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS stage_attempts (
                stage_attempt_id TEXT PRIMARY KEY,
                run_id TEXT,
                node_id TEXT,
                attempt_number INTEGER,
                is_current INTEGER,
                status TEXT,
                notes TEXT,
                failure_kind TEXT,
                provider TEXT,
                model TEXT,
                provider_state TEXT,
                contract_state TEXT,
                edit_state TEXT,
                verification_state TEXT,
                authoritative_validation_state TEXT,
                advance_allowed INTEGER,
                started_at TEXT,
                ended_at TEXT,
                duration_ms INTEGER,
                status_path TEXT,
                telemetry_json TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS run_events (
                event_id TEXT PRIMARY KEY,
                run_id TEXT,
                sequence INTEGER,
                event_type TEXT,
                node_id TEXT,
                stage_attempt_id TEXT,
                gate_id TEXT,
                lease_id TEXT,
                timestamp_utc TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS gate_instances (
                gate_id TEXT PRIMARY KEY,
                run_id TEXT,
                node_id TEXT,
                question TEXT,
                question_type TEXT,
                default_choice TEXT,
                created_at TEXT,
                is_pending INTEGER,
                status TEXT,
                answer_text TEXT,
                actor TEXT,
                rationale TEXT,
                source TEXT,
                answered_at TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS gate_answers (
                gate_answer_id TEXT PRIMARY KEY,
                run_id TEXT,
                gate_id TEXT,
                node_id TEXT,
                status TEXT,
                text TEXT,
                actor TEXT,
                rationale TEXT,
                source TEXT,
                answered_at TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS artifacts (
                artifact_id TEXT PRIMARY KEY,
                run_id TEXT,
                node_id TEXT,
                logical_path TEXT,
                current_version_id TEXT,
                version_count INTEGER,
                approval_state TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS artifact_versions (
                artifact_version_id TEXT PRIMARY KEY,
                artifact_id TEXT,
                run_id TEXT,
                node_id TEXT,
                stage_attempt_id TEXT,
                relative_path TEXT,
                logical_path TEXT,
                produced_at TEXT,
                size_bytes INTEGER,
                sha256 TEXT,
                media_type TEXT,
                approval_state TEXT,
                is_default INTEGER,
                actor TEXT,
                rationale TEXT,
                source TEXT,
                approved_at TEXT,
                producer_provider TEXT,
                producer_model TEXT,
                prompt_path TEXT,
                prompt_sha256 TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS artifact_lineage (
                artifact_lineage_id TEXT PRIMARY KEY,
                run_id TEXT,
                artifact_id TEXT,
                artifact_version_id TEXT,
                related_artifact_id TEXT,
                related_artifact_version_id TEXT,
                stage_attempt_id TEXT,
                relation_type TEXT,
                logical_path TEXT,
                related_logical_path TEXT,
                source_path TEXT,
                created_at TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS artifact_promotions (
                promotion_id TEXT PRIMARY KEY,
                artifact_id TEXT,
                artifact_version_id TEXT,
                action TEXT,
                actor TEXT,
                rationale TEXT,
                source TEXT,
                timestamp_utc TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS agent_sessions (
                agent_session_id TEXT PRIMARY KEY,
                run_id TEXT,
                stage_attempt_id TEXT,
                node_id TEXT,
                parent_agent_session_id TEXT,
                role TEXT,
                provider TEXT,
                model TEXT,
                lifecycle_state TEXT,
                assistant_turns INTEGER,
                tool_calls INTEGER,
                tool_errors INTEGER,
                duration_ms INTEGER,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS provider_invocations (
                provider_invocation_id TEXT PRIMARY KEY,
                run_id TEXT,
                stage_attempt_id TEXT,
                node_id TEXT,
                provider TEXT,
                model TEXT,
                stage_status TEXT,
                provider_state TEXT,
                failure_kind TEXT,
                provider_status_code INTEGER,
                provider_retryable INTEGER,
                provider_timeout_ms INTEGER,
                duration_ms INTEGER,
                verification_state TEXT,
                error_message TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS model_scorecards (
                model_scorecard_id TEXT PRIMARY KEY,
                run_id TEXT,
                provider TEXT,
                model TEXT,
                invocation_count INTEGER,
                success_count INTEGER,
                failure_count INTEGER,
                success_rate REAL,
                avg_duration_ms INTEGER,
                p95_duration_ms INTEGER,
                input_tokens INTEGER,
                output_tokens INTEGER,
                total_tokens INTEGER,
                estimated_input_cost_usd REAL,
                estimated_output_cost_usd REAL,
                estimated_total_cost_usd REAL,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS leases (
                lease_id TEXT PRIMARY KEY,
                run_id TEXT,
                owner_pid INTEGER,
                acquired_at TEXT,
                released_at TEXT,
                state TEXT,
                payload_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS replay_events (
                sequence INTEGER PRIMARY KEY,
                run_id TEXT,
                timestamp_utc TEXT,
                event_type TEXT,
                node_id TEXT,
                stage_attempt_id TEXT,
                gate_id TEXT,
                lease_id TEXT,
                summary TEXT,
                payload_json TEXT NOT NULL
            );
            """,
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
            """
        };

        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(ct);
        }

        await EnsureColumnAsync(connection, "runs", "state_version", "INTEGER", ct);
        await EnsureColumnAsync(connection, "runs", "cancel_requested_at", "TEXT", ct);
        await EnsureColumnAsync(connection, "runs", "cancel_requested_actor", "TEXT", ct);
        await EnsureColumnAsync(connection, "runs", "cancel_requested_rationale", "TEXT", ct);
        await EnsureColumnAsync(connection, "runs", "cancel_requested_source", "TEXT", ct);
        await EnsureColumnAsync(connection, "artifact_versions", "producer_provider", "TEXT", ct);
        await EnsureColumnAsync(connection, "artifact_versions", "producer_model", "TEXT", ct);
        await EnsureColumnAsync(connection, "artifact_versions", "prompt_path", "TEXT", ct);
        await EnsureColumnAsync(connection, "artifact_versions", "prompt_sha256", "TEXT", ct);
        await EnsureColumnAsync(connection, "provider_invocations", "stage_status", "TEXT", ct);
        await OperatorMutationStore.EnsureSchemaAsync(connection, ct);
        await EnsureIndexesAsync(connection, ct);
        await RecreateViewsAsync(connection, ct);
    }

    private static async Task ClearProjectionTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var tables = new[]
        {
            "runs",
            "stage_attempts",
            "run_events",
            "gate_instances",
            "gate_answers",
            "artifacts",
            "artifact_versions",
            "artifact_lineage",
            "artifact_promotions",
            "agent_sessions",
            "provider_invocations",
            "model_scorecards",
            "leases",
            "replay_events"
        };

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table};";
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportRunsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO runs (
                    run_id,
                    state_version,
                    status,
                    active_stage,
                    started_at,
                    updated_at,
                    checkpoint_path,
                    result_path,
                    crash,
                    cancel_requested_at,
                    cancel_requested_actor,
                    cancel_requested_rationale,
                    cancel_requested_source,
                    auto_resume_policy,
                    resume_source,
                    respawn_count,
                    event_count,
                    stage_attempt_count,
                    gate_count,
                    artifact_version_count,
                    store_backend,
                    payload_json
                ) VALUES (
                    $run_id,
                    $state_version,
                    $status,
                    $active_stage,
                    $started_at,
                    $updated_at,
                    $checkpoint_path,
                    $result_path,
                    $crash,
                    $cancel_requested_at,
                    $cancel_requested_actor,
                    $cancel_requested_rationale,
                    $cancel_requested_source,
                    $auto_resume_policy,
                    $resume_source,
                    $respawn_count,
                    $event_count,
                    $stage_attempt_count,
                    $gate_count,
                    $artifact_version_count,
                    $store_backend,
                    $payload_json
                );
                """;
            AddValue(command, "$run_id", GetString(item, "run_id") ?? string.Empty);
            AddValue(command, "$state_version", GetInt64(item, "state_version"));
            AddValue(command, "$status", GetString(item, "status"));
            AddValue(command, "$active_stage", GetString(item, "active_stage"));
            AddValue(command, "$started_at", GetString(item, "started_at"));
            AddValue(command, "$updated_at", GetString(item, "updated_at"));
            AddValue(command, "$checkpoint_path", GetString(item, "checkpoint_path"));
            AddValue(command, "$result_path", GetString(item, "result_path"));
            AddValue(command, "$crash", GetString(item, "crash"));
            AddValue(command, "$cancel_requested_at", GetString(item, "cancel_requested_at"));
            AddValue(command, "$cancel_requested_actor", GetString(item, "cancel_requested_actor"));
            AddValue(command, "$cancel_requested_rationale", GetString(item, "cancel_requested_rationale"));
            AddValue(command, "$cancel_requested_source", GetString(item, "cancel_requested_source"));
            AddValue(command, "$auto_resume_policy", GetString(item, "auto_resume_policy"));
            AddValue(command, "$resume_source", GetString(item, "resume_source"));
            AddValue(command, "$respawn_count", GetInt64(item, "respawn_count"));
            AddValue(command, "$event_count", GetInt64(item, "event_count"));
            AddValue(command, "$stage_attempt_count", GetInt64(item, "stage_attempt_count"));
            AddValue(command, "$gate_count", GetInt64(item, "gate_count"));
            AddValue(command, "$artifact_version_count", GetInt64(item, "artifact_version_count"));
            AddValue(command, "$store_backend", GetString(item, "store_backend"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportStageAttemptsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO stage_attempts (
                    stage_attempt_id,
                    run_id,
                    node_id,
                    attempt_number,
                    is_current,
                    status,
                    notes,
                    failure_kind,
                    provider,
                    model,
                    provider_state,
                    contract_state,
                    edit_state,
                    verification_state,
                    authoritative_validation_state,
                    advance_allowed,
                    started_at,
                    ended_at,
                    duration_ms,
                    status_path,
                    telemetry_json,
                    payload_json
                ) VALUES (
                    $stage_attempt_id,
                    $run_id,
                    $node_id,
                    $attempt_number,
                    $is_current,
                    $status,
                    $notes,
                    $failure_kind,
                    $provider,
                    $model,
                    $provider_state,
                    $contract_state,
                    $edit_state,
                    $verification_state,
                    $authoritative_validation_state,
                    $advance_allowed,
                    $started_at,
                    $ended_at,
                    $duration_ms,
                    $status_path,
                    $telemetry_json,
                    $payload_json
                );
                """;
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$attempt_number", GetInt64(item, "attempt_number"));
            AddValue(command, "$is_current", GetBoolean(item, "is_current"));
            AddValue(command, "$status", GetString(item, "status"));
            AddValue(command, "$notes", GetString(item, "notes"));
            AddValue(command, "$failure_kind", GetString(item, "failure_kind"));
            AddValue(command, "$provider", GetString(item, "provider"));
            AddValue(command, "$model", GetString(item, "model"));
            AddValue(command, "$provider_state", GetString(item, "provider_state"));
            AddValue(command, "$contract_state", GetString(item, "contract_state"));
            AddValue(command, "$edit_state", GetString(item, "edit_state"));
            AddValue(command, "$verification_state", GetString(item, "verification_state"));
            AddValue(command, "$authoritative_validation_state", GetString(item, "authoritative_validation_state"));
            AddValue(command, "$advance_allowed", GetBoolean(item, "advance_allowed"));
            AddValue(command, "$started_at", GetString(item, "started_at"));
            AddValue(command, "$ended_at", GetString(item, "ended_at"));
            AddValue(command, "$duration_ms", GetInt64(item, "duration_ms"));
            AddValue(command, "$status_path", GetString(item, "status_path"));
            AddValue(command, "$telemetry_json", GetNestedJson(item, "telemetry"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken ct)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await check.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureIndexesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var statements = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_stage_attempts_node_status ON stage_attempts (node_id, status, is_current);",
            "CREATE INDEX IF NOT EXISTS idx_run_events_type_node ON run_events (event_type, node_id, sequence);",
            "CREATE INDEX IF NOT EXISTS idx_gate_instances_pending_node ON gate_instances (is_pending, node_id);",
            "CREATE INDEX IF NOT EXISTS idx_artifacts_node_path ON artifacts (node_id, logical_path);",
            "CREATE INDEX IF NOT EXISTS idx_artifact_versions_artifact_produced ON artifact_versions (artifact_id, produced_at DESC);",
            "CREATE INDEX IF NOT EXISTS idx_provider_invocations_provider_model ON provider_invocations (provider, model, node_id);",
            "CREATE INDEX IF NOT EXISTS idx_replay_events_type_node ON replay_events (event_type, node_id, sequence);",
            "CREATE INDEX IF NOT EXISTS idx_artifact_promotions_artifact_time ON artifact_promotions (artifact_id, timestamp_utc DESC);",
            "CREATE INDEX IF NOT EXISTS idx_lease_ownership_state_time ON lease_ownership (state, acquired_at DESC);"
        };

        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task RecreateViewsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var dropStatements = new[]
        {
            "DROP VIEW IF EXISTS run_overview;",
            "DROP VIEW IF EXISTS current_stage_attempts;",
            "DROP VIEW IF EXISTS current_artifacts;",
            "DROP VIEW IF EXISTS operator_activity;"
        };

        foreach (var statement in dropStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(ct);
        }

        var createStatements = new[]
        {
            """
            CREATE VIEW current_stage_attempts AS
            SELECT *
            FROM stage_attempts
            WHERE is_current = 1;
            """,
            """
            CREATE VIEW current_artifacts AS
            SELECT
                a.artifact_id,
                a.run_id,
                a.node_id,
                a.logical_path,
                a.current_version_id,
                a.version_count,
                a.approval_state,
                av.relative_path,
                av.media_type,
                av.produced_at,
                av.size_bytes,
                av.sha256,
                av.producer_provider,
                av.producer_model,
                av.prompt_path
            FROM artifacts a
            LEFT JOIN artifact_versions av
              ON av.artifact_version_id = a.current_version_id;
            """,
            """
            CREATE VIEW operator_activity AS
            SELECT
                gate_answer_id AS activity_id,
                run_id,
                answered_at AS activity_at,
                'gate_answered' AS activity_type,
                node_id,
                actor,
                rationale,
                source,
                gate_id,
                NULL AS artifact_id,
                NULL AS artifact_version_id,
                text AS details
            FROM gate_answers
            UNION ALL
            SELECT
                promotion_id AS activity_id,
                NULL AS run_id,
                timestamp_utc AS activity_at,
                CASE
                    WHEN action = 'rollback' THEN 'artifact_rollback'
                    ELSE 'artifact_promotion'
                END AS activity_type,
                NULL AS node_id,
                actor,
                rationale,
                source,
                NULL AS gate_id,
                artifact_id,
                artifact_version_id,
                action AS details
            FROM artifact_promotions
            UNION ALL
            SELECT
                mutation_id AS activity_id,
                run_id,
                created_at AS activity_at,
                mutation_type AS activity_type,
                COALESCE(node_id, target_node_id) AS node_id,
                actor,
                rationale,
                source,
                NULL AS gate_id,
                artifact_id,
                artifact_version_id,
                message AS details
            FROM operator_mutations
            """,
            """
            CREATE VIEW run_overview AS
            SELECT
                r.run_id,
                r.state_version,
                r.status,
                CASE
                    WHEN r.status = 'completed' AND COALESCE((SELECT COUNT(*) FROM current_stage_attempts WHERE status IN ('fail', 'retry')), 0) = 0 THEN 'success'
                    WHEN r.status = 'completed' THEN 'completed_with_failures'
                    ELSE r.status
                END AS outcome_status,
                r.active_stage,
                r.started_at,
                r.updated_at,
                r.respawn_count,
                r.event_count,
                r.stage_attempt_count,
                r.gate_count,
                r.artifact_version_count,
                COALESCE((SELECT COUNT(*) FROM current_stage_attempts WHERE status IN ('fail', 'retry')), 0) AS current_failure_count,
                COALESCE((SELECT COUNT(*) FROM stage_attempts WHERE status IN ('fail', 'retry')), 0) AS failure_count,
                COALESCE((SELECT COUNT(*) FROM gate_instances WHERE is_pending = 1), 0) AS pending_gate_count,
                COALESCE((SELECT COUNT(*) FROM current_artifacts), 0) AS artifact_count,
                COALESCE((SELECT COUNT(*) FROM provider_invocations), 0) AS provider_invocation_count,
                COALESCE((SELECT COUNT(*) FROM operator_activity), 0) AS operator_activity_count,
                (SELECT owner_pid FROM lease_ownership WHERE state = 'active' ORDER BY acquired_at DESC LIMIT 1) AS active_lease_owner_pid,
                (SELECT acquired_at FROM lease_ownership WHERE state = 'active' ORDER BY acquired_at DESC LIMIT 1) AS active_lease_acquired_at
            FROM runs r
            ORDER BY r.updated_at DESC;
            """
        };

        foreach (var statement in createStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportRunEventsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadJsonLines(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO run_events (
                    event_id,
                    run_id,
                    sequence,
                    event_type,
                    node_id,
                    stage_attempt_id,
                    gate_id,
                    lease_id,
                    timestamp_utc,
                    payload_json
                ) VALUES (
                    $event_id,
                    $run_id,
                    $sequence,
                    $event_type,
                    $node_id,
                    $stage_attempt_id,
                    $gate_id,
                    $lease_id,
                    $timestamp_utc,
                    $payload_json
                );
                """;
            AddValue(command, "$event_id", GetString(item, "event_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$sequence", GetInt64(item, "sequence"));
            AddValue(command, "$event_type", GetString(item, "event_type"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id"));
            AddValue(command, "$gate_id", GetString(item, "gate_id"));
            AddValue(command, "$lease_id", GetString(item, "lease_id"));
            AddValue(command, "$timestamp_utc", GetString(item, "timestamp_utc"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportGatesAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO gate_instances (
                    gate_id,
                    run_id,
                    node_id,
                    question,
                    question_type,
                    default_choice,
                    created_at,
                    is_pending,
                    status,
                    answer_text,
                    actor,
                    rationale,
                    source,
                    answered_at,
                    payload_json
                ) VALUES (
                    $gate_id,
                    $run_id,
                    $node_id,
                    $question,
                    $question_type,
                    $default_choice,
                    $created_at,
                    $is_pending,
                    $status,
                    $answer_text,
                    $actor,
                    $rationale,
                    $source,
                    $answered_at,
                    $payload_json
                );
                """;
            AddValue(command, "$gate_id", GetString(item, "gate_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$question", GetString(item, "question"));
            AddValue(command, "$question_type", GetString(item, "question_type"));
            AddValue(command, "$default_choice", GetString(item, "default_choice"));
            AddValue(command, "$created_at", GetString(item, "created_at"));
            AddValue(command, "$is_pending", GetBoolean(item, "is_pending"));
            AddValue(command, "$status", GetString(item, "status"));
            AddValue(command, "$answer_text", GetString(item, "answer_text"));
            AddValue(command, "$actor", GetString(item, "actor"));
            AddValue(command, "$rationale", GetString(item, "rationale"));
            AddValue(command, "$source", GetString(item, "source"));
            AddValue(command, "$answered_at", GetString(item, "answered_at"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportGateAnswersAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO gate_answers (
                    gate_answer_id,
                    run_id,
                    gate_id,
                    node_id,
                    status,
                    text,
                    actor,
                    rationale,
                    source,
                    answered_at,
                    payload_json
                ) VALUES (
                    $gate_answer_id,
                    $run_id,
                    $gate_id,
                    $node_id,
                    $status,
                    $text,
                    $actor,
                    $rationale,
                    $source,
                    $answered_at,
                    $payload_json
                );
                """;
            AddValue(command, "$gate_answer_id", GetString(item, "gate_answer_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$gate_id", GetString(item, "gate_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$status", GetString(item, "status"));
            AddValue(command, "$text", GetString(item, "text"));
            AddValue(command, "$actor", GetString(item, "actor"));
            AddValue(command, "$rationale", GetString(item, "rationale"));
            AddValue(command, "$source", GetString(item, "source"));
            AddValue(command, "$answered_at", GetString(item, "answered_at"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportArtifactsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO artifacts (
                    artifact_id,
                    run_id,
                    node_id,
                    logical_path,
                    current_version_id,
                    version_count,
                    approval_state,
                    payload_json
                ) VALUES (
                    $artifact_id,
                    $run_id,
                    $node_id,
                    $logical_path,
                    $current_version_id,
                    $version_count,
                    $approval_state,
                    $payload_json
                );
                """;
            AddValue(command, "$artifact_id", GetString(item, "artifact_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$logical_path", GetString(item, "logical_path"));
            AddValue(command, "$current_version_id", GetString(item, "current_version_id"));
            AddValue(command, "$version_count", GetInt64(item, "version_count"));
            AddValue(command, "$approval_state", GetString(item, "approval_state"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportArtifactVersionsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO artifact_versions (
                    artifact_version_id,
                    artifact_id,
                    run_id,
                    node_id,
                    stage_attempt_id,
                    relative_path,
                    logical_path,
                    produced_at,
                    size_bytes,
                    sha256,
                    media_type,
                    approval_state,
                    is_default,
                    actor,
                    rationale,
                    source,
                    approved_at,
                    producer_provider,
                    producer_model,
                    prompt_path,
                    prompt_sha256,
                    payload_json
                ) VALUES (
                    $artifact_version_id,
                    $artifact_id,
                    $run_id,
                    $node_id,
                    $stage_attempt_id,
                    $relative_path,
                    $logical_path,
                    $produced_at,
                    $size_bytes,
                    $sha256,
                    $media_type,
                    $approval_state,
                    $is_default,
                    $actor,
                    $rationale,
                    $source,
                    $approved_at,
                    $producer_provider,
                    $producer_model,
                    $prompt_path,
                    $prompt_sha256,
                    $payload_json
                );
                """;
            AddValue(command, "$artifact_version_id", GetString(item, "artifact_version_id") ?? string.Empty);
            AddValue(command, "$artifact_id", GetString(item, "artifact_id"));
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id"));
            AddValue(command, "$relative_path", GetString(item, "relative_path"));
            AddValue(command, "$logical_path", GetString(item, "logical_path"));
            AddValue(command, "$produced_at", GetString(item, "produced_at"));
            AddValue(command, "$size_bytes", GetInt64(item, "size_bytes"));
            AddValue(command, "$sha256", GetString(item, "sha256"));
            AddValue(command, "$media_type", GetString(item, "media_type"));
            AddValue(command, "$approval_state", GetString(item, "approval_state"));
            AddValue(command, "$is_default", GetBoolean(item, "is_default"));
            AddValue(command, "$actor", GetString(item, "actor"));
            AddValue(command, "$rationale", GetString(item, "rationale"));
            AddValue(command, "$source", GetString(item, "source"));
            AddValue(command, "$approved_at", GetString(item, "approved_at"));
            AddValue(command, "$producer_provider", GetString(item, "producer_provider"));
            AddValue(command, "$producer_model", GetString(item, "producer_model"));
            AddValue(command, "$prompt_path", GetString(item, "prompt_path"));
            AddValue(command, "$prompt_sha256", GetString(item, "prompt_sha256"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportArtifactLineageAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO artifact_lineage (
                    artifact_lineage_id,
                    run_id,
                    artifact_id,
                    artifact_version_id,
                    related_artifact_id,
                    related_artifact_version_id,
                    stage_attempt_id,
                    relation_type,
                    logical_path,
                    related_logical_path,
                    source_path,
                    created_at,
                    payload_json
                ) VALUES (
                    $artifact_lineage_id,
                    $run_id,
                    $artifact_id,
                    $artifact_version_id,
                    $related_artifact_id,
                    $related_artifact_version_id,
                    $stage_attempt_id,
                    $relation_type,
                    $logical_path,
                    $related_logical_path,
                    $source_path,
                    $created_at,
                    $payload_json
                );
                """;
            AddValue(command, "$artifact_lineage_id", GetString(item, "artifact_lineage_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$artifact_id", GetString(item, "artifact_id"));
            AddValue(command, "$artifact_version_id", GetString(item, "artifact_version_id"));
            AddValue(command, "$related_artifact_id", GetString(item, "related_artifact_id"));
            AddValue(command, "$related_artifact_version_id", GetString(item, "related_artifact_version_id"));
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id"));
            AddValue(command, "$relation_type", GetString(item, "relation_type"));
            AddValue(command, "$logical_path", GetString(item, "logical_path"));
            AddValue(command, "$related_logical_path", GetString(item, "related_logical_path"));
            AddValue(command, "$source_path", GetString(item, "source_path"));
            AddValue(command, "$created_at", GetString(item, "created_at"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportArtifactPromotionsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return;

        var state = ArtifactRegistryStateStore.Load(Path.GetDirectoryName(path)!);
        foreach (var item in state.History)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO artifact_promotions (
                    promotion_id,
                    artifact_id,
                    artifact_version_id,
                    action,
                    actor,
                    rationale,
                    source,
                    timestamp_utc,
                    payload_json
                ) VALUES (
                    $promotion_id,
                    $artifact_id,
                    $artifact_version_id,
                    $action,
                    $actor,
                    $rationale,
                    $source,
                    $timestamp_utc,
                    $payload_json
                );
                """;
            AddValue(command, "$promotion_id", item.PromotionId);
            AddValue(command, "$artifact_id", item.ArtifactId);
            AddValue(command, "$artifact_version_id", item.ArtifactVersionId);
            AddValue(command, "$action", item.Action);
            AddValue(command, "$actor", item.Actor);
            AddValue(command, "$rationale", item.Rationale);
            AddValue(command, "$source", item.Source);
            AddValue(command, "$timestamp_utc", item.TimestampUtc);
            AddValue(command, "$payload_json", JsonSerializer.Serialize(item));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportAgentSessionsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO agent_sessions (
                    agent_session_id,
                    run_id,
                    stage_attempt_id,
                    node_id,
                    parent_agent_session_id,
                    role,
                    provider,
                    model,
                    lifecycle_state,
                    assistant_turns,
                    tool_calls,
                    tool_errors,
                    duration_ms,
                    payload_json
                ) VALUES (
                    $agent_session_id,
                    $run_id,
                    $stage_attempt_id,
                    $node_id,
                    $parent_agent_session_id,
                    $role,
                    $provider,
                    $model,
                    $lifecycle_state,
                    $assistant_turns,
                    $tool_calls,
                    $tool_errors,
                    $duration_ms,
                    $payload_json
                );
                """;
            AddValue(command, "$agent_session_id", GetString(item, "agent_session_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$parent_agent_session_id", GetString(item, "parent_agent_session_id"));
            AddValue(command, "$role", GetString(item, "role"));
            AddValue(command, "$provider", GetString(item, "provider"));
            AddValue(command, "$model", GetString(item, "model"));
            AddValue(command, "$lifecycle_state", GetString(item, "lifecycle_state"));
            AddValue(command, "$assistant_turns", GetInt64(item, "assistant_turns"));
            AddValue(command, "$tool_calls", GetInt64(item, "tool_calls"));
            AddValue(command, "$tool_errors", GetInt64(item, "tool_errors"));
            AddValue(command, "$duration_ms", GetInt64(item, "duration_ms"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportProviderInvocationsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO provider_invocations (
                    provider_invocation_id,
                    run_id,
                    stage_attempt_id,
                    node_id,
                    provider,
                    model,
                    stage_status,
                    provider_state,
                    failure_kind,
                    provider_status_code,
                    provider_retryable,
                    provider_timeout_ms,
                    duration_ms,
                    verification_state,
                    error_message,
                    payload_json
                ) VALUES (
                    $provider_invocation_id,
                    $run_id,
                    $stage_attempt_id,
                    $node_id,
                    $provider,
                    $model,
                    $stage_status,
                    $provider_state,
                    $failure_kind,
                    $provider_status_code,
                    $provider_retryable,
                    $provider_timeout_ms,
                    $duration_ms,
                    $verification_state,
                    $error_message,
                    $payload_json
                );
                """;
            AddValue(command, "$provider_invocation_id", GetString(item, "provider_invocation_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$provider", GetString(item, "provider"));
            AddValue(command, "$model", GetString(item, "model"));
            AddValue(command, "$stage_status", GetString(item, "stage_status"));
            AddValue(command, "$provider_state", GetString(item, "provider_state"));
            AddValue(command, "$failure_kind", GetString(item, "failure_kind"));
            AddValue(command, "$provider_status_code", GetInt64(item, "provider_status_code"));
            AddValue(command, "$provider_retryable", GetBoolean(item, "provider_retryable"));
            AddValue(command, "$provider_timeout_ms", GetInt64(item, "provider_timeout_ms"));
            AddValue(command, "$duration_ms", GetInt64(item, "duration_ms"));
            AddValue(command, "$verification_state", GetString(item, "verification_state"));
            AddValue(command, "$error_message", GetString(item, "error_message"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportModelScorecardsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO model_scorecards (
                    model_scorecard_id,
                    run_id,
                    provider,
                    model,
                    invocation_count,
                    success_count,
                    failure_count,
                    success_rate,
                    avg_duration_ms,
                    p95_duration_ms,
                    input_tokens,
                    output_tokens,
                    total_tokens,
                    estimated_input_cost_usd,
                    estimated_output_cost_usd,
                    estimated_total_cost_usd,
                    payload_json
                ) VALUES (
                    $model_scorecard_id,
                    $run_id,
                    $provider,
                    $model,
                    $invocation_count,
                    $success_count,
                    $failure_count,
                    $success_rate,
                    $avg_duration_ms,
                    $p95_duration_ms,
                    $input_tokens,
                    $output_tokens,
                    $total_tokens,
                    $estimated_input_cost_usd,
                    $estimated_output_cost_usd,
                    $estimated_total_cost_usd,
                    $payload_json
                );
                """;
            AddValue(command, "$model_scorecard_id", GetString(item, "model_scorecard_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$provider", GetString(item, "provider"));
            AddValue(command, "$model", GetString(item, "model"));
            AddValue(command, "$invocation_count", GetInt64(item, "invocation_count"));
            AddValue(command, "$success_count", GetInt64(item, "success_count"));
            AddValue(command, "$failure_count", GetInt64(item, "failure_count"));
            AddValue(command, "$success_rate", GetDouble(item, "success_rate"));
            AddValue(command, "$avg_duration_ms", GetInt64(item, "avg_duration_ms"));
            AddValue(command, "$p95_duration_ms", GetInt64(item, "p95_duration_ms"));
            AddValue(command, "$input_tokens", GetInt64(item, "input_tokens"));
            AddValue(command, "$output_tokens", GetInt64(item, "output_tokens"));
            AddValue(command, "$total_tokens", GetInt64(item, "total_tokens"));
            AddValue(command, "$estimated_input_cost_usd", GetDouble(item, "estimated_input_cost_usd"));
            AddValue(command, "$estimated_output_cost_usd", GetDouble(item, "estimated_output_cost_usd"));
            AddValue(command, "$estimated_total_cost_usd", GetDouble(item, "estimated_total_cost_usd"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportLeasesAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        foreach (var item in ReadArray(path))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO leases (
                    lease_id,
                    run_id,
                    owner_pid,
                    acquired_at,
                    released_at,
                    state,
                    payload_json
                ) VALUES (
                    $lease_id,
                    $run_id,
                    $owner_pid,
                    $acquired_at,
                    $released_at,
                    $state,
                    $payload_json
                );
                """;
            AddValue(command, "$lease_id", GetString(item, "lease_id") ?? string.Empty);
            AddValue(command, "$run_id", GetString(item, "run_id"));
            AddValue(command, "$owner_pid", GetInt64(item, "owner_pid"));
            AddValue(command, "$acquired_at", GetString(item, "acquired_at"));
            AddValue(command, "$released_at", GetString(item, "released_at"));
            AddValue(command, "$state", GetString(item, "state"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ImportReplayEventsAsync(SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return;

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        if (!document.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return;

        var runId = document.RootElement.TryGetProperty("run_id", out var runIdElement)
            ? runIdElement.GetString()
            : null;

        foreach (var item in events.EnumerateArray())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO replay_events (
                    sequence,
                    run_id,
                    timestamp_utc,
                    event_type,
                    node_id,
                    stage_attempt_id,
                    gate_id,
                    lease_id,
                    summary,
                    payload_json
                ) VALUES (
                    $sequence,
                    $run_id,
                    $timestamp_utc,
                    $event_type,
                    $node_id,
                    $stage_attempt_id,
                    $gate_id,
                    $lease_id,
                    $summary,
                    $payload_json
                );
                """;
            AddValue(command, "$sequence", GetInt64(item, "sequence"));
            AddValue(command, "$run_id", runId ?? GetString(item, "run_id"));
            AddValue(command, "$timestamp_utc", GetString(item, "timestamp_utc"));
            AddValue(command, "$event_type", GetString(item, "event_type"));
            AddValue(command, "$node_id", GetString(item, "node_id"));
            AddValue(command, "$stage_attempt_id", GetString(item, "stage_attempt_id"));
            AddValue(command, "$gate_id", GetString(item, "gate_id"));
            AddValue(command, "$lease_id", GetString(item, "lease_id"));
            AddValue(command, "$summary", GetString(item, "summary"));
            AddValue(command, "$payload_json", item.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task UpdateMetadataAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;

        AddValue(command, "$key", "last_synced_at_utc");
        AddValue(command, "$value", DateTimeOffset.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync(ct);

        command.Parameters.Clear();
        AddValue(command, "$key", "store_backend");
        AddValue(command, "$value", "sqlite_workflow_store");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddValue(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static async Task ImportProjectionAsync(string name, Func<Task> import)
    {
        try
        {
            await import();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to import SQLite workflow projection '{name}'. {ex.Message}", ex);
        }
    }

    private static IEnumerable<JsonElement> ReadArray(string path)
    {
        if (!File.Exists(path))
            return [];

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private static IEnumerable<JsonElement> ReadJsonLines(string path)
    {
        var items = new List<JsonElement>();
        if (!File.Exists(path))
            return items;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                items.Add(document.RootElement.Clone());
            }
            catch
            {
                // Ignore malformed lines during projection import.
            }
        }

        return items;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => value.ToString()
        };
    }

    private static long? GetInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static bool? GetBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static string? GetNestedJson(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.GetRawText() : null;
    }
}
