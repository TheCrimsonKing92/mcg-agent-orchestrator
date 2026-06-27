using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteOrchestratorStateRepository : ITransactionalOrchestratorStateRepository
{
    private readonly string _dbPath;
    private readonly Action<string>? _statementObserver;
    private static readonly string[] SchemaTableNames =
    [
        "meta",
        "goals",
        "human_input_requests",
        "model_fit_history"
    ];
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public SqliteOrchestratorStateRepository(string dbPath)
        : this(dbPath, statementObserver: null)
    {
    }

    internal SqliteOrchestratorStateRepository(string dbPath, Action<string>? statementObserver)
    {
        _dbPath = dbPath;
        _statementObserver = statementObserver;
        EnsureSchema();
    }

    // Pooling=False matters under WAL: a POOLED connection can be returned to the pool still holding
    // a WAL read/lock slot, so a later writer meets "database is locked" that busy_timeout cannot wait
    // out (it is not a plain lock-wait). The sibling CollaborationItemStore already does this; the
    // state repo did not, which let a goal-create issued during a conduct --loop tick crash the loop.
    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

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

    // Cap on retrying a transient SQLITE_BUSY/LOCKED before giving up. busy_timeout (30s) handles the
    // simple lock-wait, but the deadlock-avoidance path (and pooling artifacts) can still surface an
    // immediate BUSY; this bounded retry turns that into a brief wait instead of a fatal throw that
    // would kill a conduct --loop on a concurrent writer.
    private const int MaxBusyRetries = 6;

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static async Task<T> WithBusyRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (attempt < MaxBusyRetries && IsTransientLock(ex))
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    // Opens a connection and acquires the write lock (BEGIN IMMEDIATE) with bounded retry. Only the
    // lock ACQUISITION is retried — once it returns, the caller runs its body exactly once, so no
    // side-effecting transaction delegate is ever re-executed.
    private async Task<SqliteConnection> BeginWriteAsync(CancellationToken cancellationToken)
    {
        return await WithBusyRetryAsync(async () =>
        {
            var conn = OpenConnection();
            try
            {
                await SetBusyTimeoutAsync(conn, cancellationToken);
                await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
                return conn;
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }, cancellationToken);
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        if (SchemaTablesAlreadyExist(conn))
        {
            MigrateVersionColumn(conn);
            return;
        }

        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
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
                source_backlog_item_id TEXT NULL,
                updated_at    TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                version       INTEGER NOT NULL DEFAULT 0
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
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_status ON goals(status)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");
        RunNonQuery(conn, "INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '1')");
    }

    // Idempotent migration: adds metadata columns to existing schemas that pre-date them.
    // Old binaries ignore the extra columns; this binary treats absent version as 0.
    private void MigrateVersionColumn(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name = 'version'";
        if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            RunNonQuery(conn, "ALTER TABLE goals ADD COLUMN version INTEGER NOT NULL DEFAULT 0");

        using var sourceCheck = conn.CreateCommand();
        sourceCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name = 'source_backlog_item_id'";
        if (Convert.ToInt32(sourceCheck.ExecuteScalar()) == 0)
            RunNonQuery(conn, "ALTER TABLE goals ADD COLUMN source_backlog_item_id TEXT NULL");

        if (!IndexExists(conn, "ix_goals_source_backlog_item_id"))
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");

        if (!IndexExists(conn, "ix_goals_status"))
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_status ON goals(status)");
    }

    private static bool SchemaTablesAlreadyExist(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ($meta, $goals, $human_input_requests, $model_fit_history)
            """;
        cmd.Parameters.AddWithValue("$meta", "meta");
        cmd.Parameters.AddWithValue("$goals", "goals");
        cmd.Parameters.AddWithValue("$human_input_requests", "human_input_requests");
        cmd.Parameters.AddWithValue("$model_fit_history", "model_fit_history");
        return Convert.ToInt32(cmd.ExecuteScalar()) == SchemaTableNames.Length;
    }

    private static bool IndexExists(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name";
        cmd.Parameters.AddWithValue("$name", indexName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public async Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            return await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);
        }, cancellationToken);
    }

    public async Task<AgentOrchestratorKernel> LoadGoalsAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            return await LoadFromConnectionAsync(conn, goalIds, cancellationToken);
        }, cancellationToken);
    }

    public async Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        await using var conn = await BeginWriteAsync(cancellationToken);
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
        await using var conn = await BeginWriteAsync(cancellationToken);
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);

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

    public async Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<GoalSummary>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, status, objective, updated_at
            FROM goals
            WHERE status <> $cleanedUp
            ORDER BY updated_at DESC
            """;
        cmd.Parameters.AddWithValue("$cleanedUp", "CleanedUp");
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
        IReadOnlyCollection<GoalId>? goalIds,
        CancellationToken cancellationToken)
    {
        var goalSnapshots = new List<GoalSnapshot>();
        var humanInputSnapshots = new List<HumanInputRequestSnapshot>();

        if (goalIds is null || goalIds.Count > 0)
        {
            await using var cmd = conn.CreateCommand();
            if (goalIds is null)
            {
                cmd.CommandText = "SELECT snapshot_json FROM goals";
            }
            else
            {
                var parameterNames = goalIds.Select((_, index) => $"$id{index}").ToArray();
                cmd.CommandText = $"SELECT snapshot_json FROM goals WHERE id IN ({string.Join(", ", parameterNames)})";
                var index = 0;
                foreach (var goalId in goalIds)
                {
                    cmd.Parameters.AddWithValue(parameterNames[index], goalId.Value);
                    index++;
                }
            }

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
                INSERT INTO goals (id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version)
                VALUES ($id, $status, $objective, $source_backlog_item_id, $updated_at, $json, 1)
                ON CONFLICT(id) DO UPDATE SET
                    status        = excluded.status,
                    objective     = excluded.objective,
                    source_backlog_item_id = excluded.source_backlog_item_id,
                    snapshot_json = excluded.snapshot_json,
                    version       = goals.version + 1,
                    updated_at    = CASE WHEN excluded.snapshot_json != goals.snapshot_json
                                         THEN excluded.updated_at
                                         ELSE goals.updated_at END
                """;
            cmd.Parameters.AddWithValue("$id", goal.Id);
            cmd.Parameters.AddWithValue("$status", goal.Status.ToString());
            cmd.Parameters.AddWithValue("$objective", goal.Objective);
            cmd.Parameters.AddWithValue("$source_backlog_item_id", (object?)goal.SourceBacklogItemId ?? DBNull.Value);
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

    public async Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", goalId.Value);
            var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
            if (json is null) return (GoalSnapshot?)null;
            return JsonSerializer.Deserialize<GoalSnapshot>(json, SerializerOptions);
        }, cancellationToken);
    }

    public async Task<T> TransactGoalAsync<T>(
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var versionMismatchDelay = 50;
        for (var attempt = 1; ; attempt++)
        {
            // Load this goal's snapshot and version outside the write transaction.
            // WAL mode provides a consistent reader snapshot without an explicit read lock.
            var (loadedSnapshot, loadedVersion) = await LoadGoalSnapshotAndVersionAsync(goalId, cancellationToken);

            // Delegate produces the new snapshot to write (runs outside the write lock).
            var (shouldSave, newSnapshot, result) = await transaction(loadedSnapshot, cancellationToken);

            if (!shouldSave || newSnapshot is null)
                return result;

            // Short BEGIN IMMEDIATE: re-read version, write one row if version unchanged.
            var casSucceeded = await TryCasWriteGoalRowAsync(goalId, newSnapshot, loadedVersion, cancellationToken);
            if (casSucceeded)
                return result;

            // Version mismatch detected: concurrent writer incremented the version between our
            // load and our CAS write. Treat this as a transient error and retry the full
            // load-mutate-CAS cycle — same retry budget as SQLITE_BUSY.
            if (attempt >= MaxBusyRetries)
                throw new InvalidOperationException(
                    $"TransactGoalAsync: optimistic concurrency retries exhausted for goal {goalId.Value[..8]}");

            await Task.Delay(versionMismatchDelay, cancellationToken);
            versionMismatchDelay = Math.Min(versionMismatchDelay * 2, 1000);
        }
    }

    private async Task<(GoalSnapshot? Snapshot, int Version)> LoadGoalSnapshotAndVersionAsync(
        GoalId goalId, CancellationToken cancellationToken)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT snapshot_json, COALESCE(version, 0) FROM goals WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", goalId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return ((GoalSnapshot?)null, 0);
            var json = reader.GetString(0);
            var version = reader.GetInt32(1);
            var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(json, SerializerOptions);
            return (snapshot, version);
        }, cancellationToken);
    }

    // Attempts a short CAS write for one goal row. Acquires BEGIN IMMEDIATE, re-reads the version,
    // writes only if it matches expectedVersion, increments version, then commits.
    // Returns true on success, false when the version has changed (caller should retry).
    private async Task<bool> TryCasWriteGoalRowAsync(
        GoalId goalId, GoalSnapshot snapshot, int expectedVersion, CancellationToken cancellationToken)
    {
        await using var conn = await BeginWriteAsync(cancellationToken);
        try
        {
            int currentVersion;
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT COALESCE(version, 0) FROM goals WHERE id = $id";
                checkCmd.Parameters.AddWithValue("$id", goalId.Value);
                var val = await checkCmd.ExecuteScalarAsync(cancellationToken);
                currentVersion = val is null or DBNull ? 0 : Convert.ToInt32(val);
            }

            if (currentVersion != expectedVersion)
            {
                await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken);
                return false;
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            await using (var writeCmd = conn.CreateCommand())
            {
                writeCmd.CommandText = """
                    INSERT INTO goals (id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version)
                    VALUES ($id, $status, $objective, $source_backlog_item_id, $updated_at, $json, $version)
                    ON CONFLICT(id) DO UPDATE SET
                        status        = excluded.status,
                        objective     = excluded.objective,
                        source_backlog_item_id = excluded.source_backlog_item_id,
                        snapshot_json = excluded.snapshot_json,
                        version       = excluded.version,
                        updated_at    = CASE WHEN excluded.snapshot_json != goals.snapshot_json
                                             THEN excluded.updated_at
                                             ELSE goals.updated_at END
                    """;
                writeCmd.Parameters.AddWithValue("$id", goalId.Value);
                writeCmd.Parameters.AddWithValue("$status", snapshot.Status.ToString());
                writeCmd.Parameters.AddWithValue("$objective", snapshot.Objective);
                writeCmd.Parameters.AddWithValue("$source_backlog_item_id", (object?)snapshot.SourceBacklogItemId ?? DBNull.Value);
                writeCmd.Parameters.AddWithValue("$updated_at", updatedAt);
                writeCmd.Parameters.AddWithValue("$json", json);
                writeCmd.Parameters.AddWithValue("$version", currentVersion + 1);
                await writeCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return true;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
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

    private void RunNonQuery(SqliteConnection conn, string sql)
    {
        _statementObserver?.Invoke(sql);
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
