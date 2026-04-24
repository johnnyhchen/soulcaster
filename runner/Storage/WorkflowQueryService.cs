namespace Soulcaster.Runner.Storage;

using System.Globalization;
using Microsoft.Data.Sqlite;

internal sealed record WorkflowSavedQuery(
    string Name,
    string Description);

internal sealed record WorkflowQueryRequest(
    string View,
    int Limit = 50,
    string? NodeId = null,
    string? Status = null,
    string? EventType = null,
    string? Provider = null,
    string? Model = null,
    string? Actor = null,
    string? ArtifactId = null,
    string? ApprovalState = null,
    string? Search = null);

internal sealed record WorkflowQueryResult(
    string View,
    string Description,
    IReadOnlyList<Dictionary<string, object?>> Rows);

internal static class WorkflowQueryService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private static readonly IReadOnlyDictionary<string, WorkflowSavedQuery> SavedQueries =
        new Dictionary<string, WorkflowSavedQuery>(StringComparer.OrdinalIgnoreCase)
        {
            ["overview"] = new("overview", "One-row durable summary for the current run state, failures, gates, leases, and artifacts."),
            ["attempts"] = new("attempts", "Stage attempt history with status, failure kind, provider, and duration."),
            ["failures"] = new("failures", "Current failing or retrying stage attempts."),
            ["gates"] = new("gates", "Gate creation and answer state, including pending gates."),
            ["artifacts"] = new("artifacts", "Current artifact selections joined with the active version metadata."),
            ["operators"] = new("operators", "Audited operator activity from gate answers, promotions, and control-plane events."),
            ["providers"] = new("providers", "Provider invocations with model, failure, and timing details."),
            ["events"] = new("events", "Replay events ordered by durable sequence."),
            ["scorecards"] = new("scorecards", "Per-model scorecards from projected provider telemetry."),
            ["hotspots"] = new("hotspots", "Node-level activity summary highlighting retries, failures, and slow stages."),
            ["lineage"] = new("lineage", "Artifact lineage edges across source and derived versions."),
            ["leases"] = new("leases", "Direct lease ownership history from the authoritative SQLite ownership table."),
            ["mutations"] = new("mutations", "Direct operator mutation journal written to SQLite during control-plane actions.")
        };

    public static IReadOnlyList<WorkflowSavedQuery> ListSavedQueries() =>
        SavedQueries.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static async Task<WorkflowQueryResult> ExecuteAsync(
        string pipelineDir,
        WorkflowQueryRequest request,
        CancellationToken ct = default)
    {
        var normalizedView = NormalizeView(request.View);
        if (!SavedQueries.TryGetValue(normalizedView, out var definition))
            throw new InvalidOperationException($"Unknown query view '{request.View}'. Use 'attractor query list' to see supported views.");

        var dbPath = Path.Combine(Path.GetFullPath(pipelineDir), "store", "workflow.sqlite");
        if (!File.Exists(dbPath))
            throw new InvalidOperationException($"SQLite projection not found at '{dbPath}'. Run the workflow or sync the store first.");

        var (sql, binder) = BuildSql(normalizedView, request);

        await using var connection = CreateConnection(dbPath);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        binder(command);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadRow(reader));

        return new WorkflowQueryResult(definition.Name, definition.Description, rows);
    }

    public static string Format(WorkflowQueryResult result)
    {
        if (result.Rows.Count == 0)
            return $"View: {result.View}\nRows: 0";

        if (string.Equals(result.View, "overview", StringComparison.OrdinalIgnoreCase) && result.Rows.Count == 1)
        {
            var row = result.Rows[0];
            var lines = new List<string>
            {
                $"View: {result.View}",
                $"Description: {result.Description}"
            };

            foreach (var (key, value) in row)
                lines.Add($"{key}: {FormatScalar(value)}");

            return string.Join(Environment.NewLine, lines);
        }

        var output = new List<string>
        {
            $"View: {result.View}",
            $"Description: {result.Description}",
            $"Rows: {result.Rows.Count}"
        };

        foreach (var row in result.Rows)
        {
            var summary = string.Join(
                " | ",
                row.Select(item => $"{item.Key}={FormatScalar(item.Value)}"));
            output.Add(summary);
        }

        return string.Join(Environment.NewLine, output);
    }

    private static string NormalizeView(string rawView)
    {
        var normalized = rawView?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "" or "summary" => "overview",
            "stage-attempts" or "stage_attempts" => "attempts",
            "artifact" or "artifact-current" or "current-artifacts" => "artifacts",
            "operator" or "operator-activity" or "operator_activity" => "operators",
            "provider" or "provider-invocations" or "provider_invocations" => "providers",
            "replay" => "events",
            "scorecard" => "scorecards",
            "lease" => "leases",
            "operator-mutations" or "operator_mutations" => "mutations",
            "artifact-lineage" => "lineage",
            _ => normalized
        };
    }

    private static (string Sql, Action<SqliteCommand> Bind) BuildSql(string view, WorkflowQueryRequest request)
    {
        var limit = Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaxLimit);
        return view switch
        {
            "overview" => (
                "SELECT * FROM run_overview LIMIT 1;",
                static _ => { }),
            "attempts" => (
                $$"""
                SELECT
                    stage_attempt_id,
                    node_id,
                    attempt_number,
                    is_current,
                    status,
                    failure_kind,
                    provider,
                    model,
                    duration_ms,
                    started_at,
                    ended_at,
                    notes
                FROM stage_attempts
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($status IS NULL OR status = $status OR failure_kind = $status)
                ORDER BY COALESCE(ended_at, started_at, '') DESC, attempt_number DESC, stage_attempt_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "failures" => (
                $$"""
                SELECT
                    stage_attempt_id,
                    node_id,
                    attempt_number,
                    status,
                    failure_kind,
                    provider,
                    model,
                    duration_ms,
                    notes,
                    started_at,
                    ended_at
                FROM current_stage_attempts
                WHERE (status IN ('fail', 'retry') OR failure_kind IS NOT NULL)
                  AND ($node_id IS NULL OR node_id = $node_id)
                  AND ($status IS NULL OR status = $status OR failure_kind = $status)
                ORDER BY COALESCE(ended_at, started_at, '') DESC, stage_attempt_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "gates" => (
                $$"""
                SELECT
                    gate_id,
                    node_id,
                    is_pending,
                    status,
                    question,
                    default_choice,
                    answer_text,
                    actor,
                    rationale,
                    source,
                    created_at,
                    answered_at
                FROM gate_instances
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($status IS NULL OR status = $status)
                ORDER BY COALESCE(answered_at, created_at, '') DESC, gate_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "artifacts" => (
                $$"""
                SELECT
                    artifact_id,
                    node_id,
                    logical_path,
                    approval_state,
                    current_version_id,
                    version_count,
                    relative_path,
                    media_type,
                    produced_at,
                    producer_provider,
                    producer_model
                FROM current_artifacts
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($artifact_id IS NULL OR artifact_id = $artifact_id OR logical_path = $artifact_id)
                  AND ($approval_state IS NULL OR approval_state = $approval_state)
                  AND ($search IS NULL OR logical_path LIKE $search OR relative_path LIKE $search OR media_type LIKE $search)
                ORDER BY logical_path ASC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "operators" => (
                $$"""
                SELECT
                    activity_at,
                    activity_type,
                    node_id,
                    actor,
                    rationale,
                    source,
                    gate_id,
                    artifact_id,
                    artifact_version_id,
                    details
                FROM operator_activity
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($event_type IS NULL OR activity_type = $event_type)
                  AND ($actor IS NULL OR actor = $actor)
                  AND ($search IS NULL OR details LIKE $search OR rationale LIKE $search)
                ORDER BY COALESCE(activity_at, '') DESC, activity_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "providers" => (
                $$"""
                SELECT
                    provider_invocation_id,
                    node_id,
                    provider,
                    model,
                    stage_status,
                    failure_kind,
                    provider_state,
                    verification_state,
                    duration_ms,
                    error_message
                FROM provider_invocations
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($provider IS NULL OR provider = $provider)
                  AND ($model IS NULL OR model = $model)
                  AND ($status IS NULL OR stage_status = $status OR failure_kind = $status)
                ORDER BY provider_invocation_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "events" => (
                $$"""
                SELECT
                    sequence,
                    timestamp_utc,
                    event_type,
                    node_id,
                    stage_attempt_id,
                    gate_id,
                    lease_id,
                    summary
                FROM replay_events
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($event_type IS NULL OR event_type = $event_type)
                  AND ($search IS NULL OR summary LIKE $search)
                ORDER BY sequence DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "scorecards" => (
                $$"""
                SELECT
                    provider,
                    model,
                    invocation_count,
                    success_count,
                    failure_count,
                    success_rate,
                    avg_duration_ms,
                    p95_duration_ms,
                    total_tokens,
                    estimated_total_cost_usd
                FROM model_scorecards
                WHERE ($provider IS NULL OR provider = $provider)
                  AND ($model IS NULL OR model = $model)
                ORDER BY success_rate DESC, invocation_count DESC, provider ASC, model ASC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "hotspots" => (
                $$"""
                SELECT
                    node_id,
                    COUNT(*) AS attempt_count,
                    SUM(CASE WHEN status = 'success' THEN 1 ELSE 0 END) AS success_count,
                    SUM(CASE WHEN status IN ('fail', 'retry') OR failure_kind IS NOT NULL THEN 1 ELSE 0 END) AS failure_count,
                    ROUND(AVG(COALESCE(duration_ms, 0)), 0) AS avg_duration_ms,
                    MAX(COALESCE(ended_at, started_at, '')) AS last_activity_at,
                    GROUP_CONCAT(DISTINCT COALESCE(provider || ':' || model, provider, model, 'unknown')) AS providers
                FROM stage_attempts
                WHERE ($node_id IS NULL OR node_id = $node_id)
                  AND ($provider IS NULL OR provider = $provider)
                  AND ($model IS NULL OR model = $model)
                GROUP BY node_id
                ORDER BY failure_count DESC, attempt_count DESC, last_activity_at DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "lineage" => (
                $$"""
                SELECT
                    created_at,
                    artifact_id,
                    artifact_version_id,
                    logical_path,
                    relation_type,
                    related_artifact_id,
                    related_artifact_version_id,
                    related_logical_path,
                    source_path
                FROM artifact_lineage
                WHERE ($artifact_id IS NULL
                       OR artifact_id = $artifact_id
                       OR related_artifact_id = $artifact_id
                       OR logical_path = $artifact_id
                       OR related_logical_path = $artifact_id)
                ORDER BY COALESCE(created_at, '') DESC, artifact_lineage_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "leases" => (
                $$"""
                SELECT
                    run_id,
                    lease_id,
                    owner_pid,
                    acquired_at,
                    released_at,
                    generation,
                    state
                FROM lease_ownership
                WHERE ($status IS NULL OR state = $status)
                ORDER BY generation DESC, COALESCE(acquired_at, '') DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            "mutations" => (
                $$"""
                SELECT
                    created_at,
                    mutation_type,
                    mutation_status,
                    node_id,
                    target_node_id,
                    actor,
                    rationale,
                    source,
                    run_version,
                    artifact_id,
                    artifact_version_id,
                    message
                FROM operator_mutations
                WHERE ($node_id IS NULL OR node_id = $node_id OR target_node_id = $node_id)
                  AND ($status IS NULL OR mutation_status = $status)
                  AND ($event_type IS NULL OR mutation_type = $event_type)
                  AND ($actor IS NULL OR actor = $actor)
                  AND ($artifact_id IS NULL OR artifact_id = $artifact_id OR artifact_version_id = $artifact_id)
                  AND ($search IS NULL OR message LIKE $search OR rationale LIKE $search)
                ORDER BY COALESCE(created_at, '') DESC, mutation_id DESC
                LIMIT {{limit.ToString(CultureInfo.InvariantCulture)}};
                """,
                command => BindCommon(command, request)),
            _ => throw new InvalidOperationException($"Unsupported query view '{view}'.")
        };
    }

    private static void BindCommon(SqliteCommand command, WorkflowQueryRequest request)
    {
        AddValue(command, "$node_id", NormalizeOptional(request.NodeId));
        AddValue(command, "$status", NormalizeOptional(request.Status));
        AddValue(command, "$event_type", NormalizeOptional(request.EventType));
        AddValue(command, "$provider", NormalizeOptional(request.Provider));
        AddValue(command, "$model", NormalizeOptional(request.Model));
        AddValue(command, "$actor", NormalizeOptional(request.Actor));
        AddValue(command, "$artifact_id", NormalizeOptional(request.ArtifactId));
        AddValue(command, "$approval_state", NormalizeOptional(request.ApprovalState));
        AddValue(command, "$search", BuildLikePattern(request.Search));
    }

    private static SqliteConnection CreateConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        return new SqliteConnection(builder.ToString());
    }

    private static Dictionary<string, object?> ReadRow(SqliteDataReader reader)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i)
                ? null
                : reader.GetValue(i) switch
                {
                    long value => value,
                    int value => value,
                    double value => value,
                    float value => value,
                    decimal value => value,
                    bool value => value,
                    _ => reader.GetValue(i).ToString()
                };
        }

        return row;
    }

    private static void AddValue(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? BuildLikePattern(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null ? null : $"%{normalized}%";
    }

    private static string FormatScalar(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        _ => value.ToString() ?? string.Empty
    };
}
