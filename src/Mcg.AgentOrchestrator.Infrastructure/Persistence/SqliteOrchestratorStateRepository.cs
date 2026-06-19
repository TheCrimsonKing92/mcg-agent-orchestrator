using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteOrchestratorStateRepository : ITransactionalOrchestratorStateRepository
{
    private readonly string _dbPath;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public SqliteOrchestratorStateRepository(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;";

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        // busy_timeout is PER-CONNECTION (unlike WAL, which is a persistent DB property set once in
        // EnsureSchema). Without it a connection that meets a held lock fails IMMEDIATELY with
        // "database is locked" — so a `backlog-add`/`status`/etc. issued while the conductor holds a
        // brief per-tick write lock errors out instead of waiting. Setting it lets concurrent commands
        // (and concurrent goal drivers) wait out the short write window, which WAL already keeps small.
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        return conn;
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS goals (
                id            TEXT PRIMARY KEY,
                status        TEXT NOT NULL,
                objective     TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS human_input_requests (
                id            TEXT PRIMARY KEY,
                goal_id       TEXT NOT NULL,
                snapshot_json TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS model_fit_history (
                goal_id       TEXT NOT NULL,
                task_id       TEXT NOT NULL,
                role          TEXT NOT NULL,
                provider_name TEXT NOT NULL,
                model_name    TEXT NOT NULL,
                complexity    TEXT NULL,
                task_shape    TEXT NULL,
                outcome       TEXT NOT NULL,
                self_rating   TEXT NOT NULL,
                timestamp     TEXT NOT NULL,
                PRIMARY KEY (goal_id, task_id, timestamp)
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_role ON model_fit_history(role)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_model ON model_fit_history(provider_name, model_name)");
        RunNonQuery(conn, "INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '1')");
    }

    public async Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await LoadFromConnectionAsync(conn, cancellationToken);
    }

    public async Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await SetBusyTimeoutAsync(conn, cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            await WriteSnapshotAsync(conn, kernel, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(
            async (kernel, _, token) => await transaction(kernel, token),
            cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await SetBusyTimeoutAsync(conn, cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, cancellationToken);

            async Task CheckpointAsync()
            {
                await WriteSnapshotAsync(conn, kernel, cancellationToken);
            }

            var (shouldSave, result) = await transaction(kernel, CheckpointAsync, cancellationToken);

            if (shouldSave)
                await WriteSnapshotAsync(conn, kernel, cancellationToken);

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return result;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<GoalSummary>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, status, objective, updated_at FROM goals ORDER BY updated_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GoalSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return results;
    }

    public async Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<ModelFitHistoryRow>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp
            FROM model_fit_history
            ORDER BY timestamp DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadModelFitHistoryRow(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
        int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
        CancellationToken cancellationToken = default)
    {
        return ModelOutcomeScorecard.Build(await ListModelFitHistoryAsync(cancellationToken), windowSize);
    }

    public async Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var rows = new List<ModelFitHistoryRow>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp
            FROM model_fit_history
            WHERE role = $role
            ORDER BY timestamp DESC
            """;
        cmd.Parameters.AddWithValue("$role", role.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadModelFitHistoryRow(reader));
        }

        return ModelFitHistory.QueryBestFitForRole(rows, role);
    }

    private static async Task<AgentOrchestratorKernel> LoadFromConnectionAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        var goalSnapshots = new List<GoalSnapshot>();
        var humanInputSnapshots = new List<HumanInputRequestSnapshot>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT snapshot_json FROM goals";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var snap = JsonSerializer.Deserialize<GoalSnapshot>(reader.GetString(0), SerializerOptions);
                if (snap != null) goalSnapshots.Add(snap);
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT snapshot_json FROM human_input_requests";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var snap = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(reader.GetString(0), SerializerOptions);
                if (snap != null) humanInputSnapshots.Add(snap);
            }
        }

        return AgentOrchestratorKernel.FromSnapshot(
            new OrchestratorSnapshot(goalSnapshots, humanInputSnapshots));
    }

    private static async Task WriteSnapshotAsync(
        SqliteConnection conn,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken)
    {
        var snapshot = kernel.ExportSnapshot();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        foreach (var goal in snapshot.Goals)
        {
            var json = JsonSerializer.Serialize(goal, SerializerOptions);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO goals (id, status, objective, updated_at, snapshot_json)
                VALUES ($id, $status, $objective, $updated_at, $json)
                ON CONFLICT(id) DO UPDATE SET
                    status        = excluded.status,
                    objective     = excluded.objective,
                    snapshot_json = excluded.snapshot_json,
                    updated_at    = CASE WHEN excluded.snapshot_json != goals.snapshot_json
                                         THEN excluded.updated_at
                                         ELSE goals.updated_at END
                """;
            cmd.Parameters.AddWithValue("$id", goal.Id);
            cmd.Parameters.AddWithValue("$status", goal.Status.ToString());
            cmd.Parameters.AddWithValue("$objective", goal.Objective);
            cmd.Parameters.AddWithValue("$updated_at", updatedAt);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var request in snapshot.HumanInputRequests)
        {
            var json = JsonSerializer.Serialize(request, SerializerOptions);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO human_input_requests (id, goal_id, snapshot_json)
                VALUES ($id, $goal_id, $json)
                ON CONFLICT(id) DO UPDATE SET
                    goal_id       = excluded.goal_id,
                    snapshot_json = excluded.snapshot_json
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$goal_id", request.GoalId);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            await UpsertModelFitHistoryRowAsync(conn, row, cancellationToken);
        }
    }

    private static async Task UpsertModelFitHistoryRowAsync(
        SqliteConnection conn,
        ModelFitHistoryRow row,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_fit_history (
                goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp)
            VALUES (
                $goal_id, $task_id, $role, $provider_name, $model_name, $complexity, $task_shape, $outcome, $self_rating, $timestamp)
            ON CONFLICT(goal_id, task_id, timestamp) DO UPDATE SET
                role          = excluded.role,
                provider_name = excluded.provider_name,
                model_name    = excluded.model_name,
                complexity    = excluded.complexity,
                task_shape    = excluded.task_shape,
                outcome       = excluded.outcome,
                self_rating   = excluded.self_rating
            """;
        cmd.Parameters.AddWithValue("$goal_id", row.GoalId);
        cmd.Parameters.AddWithValue("$task_id", row.TaskId);
        cmd.Parameters.AddWithValue("$role", row.Role.ToString());
        cmd.Parameters.AddWithValue("$provider_name", row.ProviderName);
        cmd.Parameters.AddWithValue("$model_name", row.ModelName);
        cmd.Parameters.AddWithValue("$complexity", row.Complexity?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$task_shape", row.TaskShape ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome", row.Outcome.ToString());
        cmd.Parameters.AddWithValue("$self_rating", ModelFitHistory.NormalizeSelfRating(row.SelfRating));
        cmd.Parameters.AddWithValue("$timestamp", row.Timestamp.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ModelFitHistoryRow ReadModelFitHistoryRow(SqliteDataReader reader)
    {
        return new ModelFitHistoryRow(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<AgentRole>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : Enum.Parse<TaskComplexity>(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            Enum.Parse<WorkTaskStatus>(reader.GetString(7)),
            ModelFitHistory.NormalizeSelfRating(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static async Task SetBusyTimeoutAsync(SqliteConnection conn, CancellationToken cancellationToken)
        => await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task RunNonQueryAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record GoalSummary(string Id, string Status, string Objective, string UpdatedAt);
