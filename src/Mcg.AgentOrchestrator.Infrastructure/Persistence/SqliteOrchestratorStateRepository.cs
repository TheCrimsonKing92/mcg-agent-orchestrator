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

    public async Task SaveGoalSnapshotsAsync(
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        if (goals.Count == 0)
            return;

        await using var conn = await BeginWriteAsync(cancellationToken);
        try
        {
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            // SQLite has no provider-level array/upsert binding here, so each row is still a
            // statement; the bulk boundary is one connection, one BEGIN IMMEDIATE, one COMMIT.
            foreach (var goal in goals)
            {
                await UpsertGoalRowAsync(conn, goal, updatedAt, versionSql: "goals.version + 1", cancellationToken);
                await UpsertModelFitHistoryRowsAsync(conn, goal, cancellationToken);
            }

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
        IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        if (goals.Count == 0)
            return [];

        var results = new List<GoalSnapshotSaveResult>(goals.Count);
        foreach (var request in goals)
        {
            if (!string.Equals(request.Baseline.Id, request.Current.Id, StringComparison.Ordinal))
            {
                results.Add(new GoalSnapshotSaveResult(
                    request.Current.Id,
                    GoalSnapshotSaveDisposition.Skipped,
                    null,
                    "baseline/current goal id mismatch"));
                continue;
            }

            var result = await TransactGoalAsync(
                new GoalId(request.Current.Id),
                (storedSnapshot, _) =>
                {
                    if (storedSnapshot is null)
                    {
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, GoalSnapshotSaveResult Result)>(
                            (false, null, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Skipped,
                                null,
                                "goal row no longer exists")));
                    }

                    if (SnapshotEquals(storedSnapshot, request.Baseline))
                    {
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, GoalSnapshotSaveResult Result)>(
                            (true, request.Current, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Saved,
                                request.Current,
                                "stored version matched tick baseline")));
                    }

                    if (!TryMergeGoalSnapshots(request.Baseline, storedSnapshot, request.Current, out var merged, out var reason))
                    {
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, GoalSnapshotSaveResult Result)>(
                            (false, null, new GoalSnapshotSaveResult(
                                request.Current.Id,
                                GoalSnapshotSaveDisposition.Skipped,
                                storedSnapshot,
                                reason)));
                    }

                    return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, GoalSnapshotSaveResult Result)>(
                        (true, merged, new GoalSnapshotSaveResult(
                            request.Current.Id,
                            GoalSnapshotSaveDisposition.Merged,
                            merged,
                            reason)));
                },
                cancellationToken);
            results.Add(result);
        }

        return results;
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

    public async Task<IReadOnlyList<QuarantinedGoalSummary>> ListQuarantinedGoalRowsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<QuarantinedGoalSummary>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, snapshot_json FROM goals ORDER BY updated_at DESC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            TryReadGoalSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                results,
                logQuarantine: false,
                out _);
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
                cmd.CommandText = "SELECT id, snapshot_json FROM goals";
            }
            else
            {
                var parameterNames = goalIds.Select((_, index) => $"$id{index}").ToArray();
                cmd.CommandText = $"SELECT id, snapshot_json FROM goals WHERE id IN ({string.Join(", ", parameterNames)})";
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
                if (TryReadGoalSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    null,
                    logQuarantine: true,
                    out var snap))
                {
                    goalSnapshots.Add(snap);
                }
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

    private static bool TryReadGoalSnapshot(
        string goalId,
        string json,
        List<QuarantinedGoalSummary>? quarantined,
        bool logQuarantine,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GoalSnapshot? snapshot)
    {
        try
        {
            snapshot = JsonSerializer.Deserialize<GoalSnapshot>(json, SerializerOptions);
            if (snapshot is not null)
                return true;

            var error = "JsonException: deserialized goal snapshot was null";
            quarantined?.Add(new QuarantinedGoalSummary(goalId, error));
            if (logQuarantine)
                Console.Error.WriteLine($"QUARANTINED goal {ShortGoalId(goalId)}: {error}");
            return false;
        }
        catch (Exception ex) when (IsSnapshotDeserializeException(ex))
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            quarantined?.Add(new QuarantinedGoalSummary(goalId, error));
            if (logQuarantine)
                Console.Error.WriteLine($"QUARANTINED goal {ShortGoalId(goalId)}: {error}");
            snapshot = null;
            return false;
        }
    }

    private static bool IsSnapshotDeserializeException(Exception exception) =>
        exception is JsonException or NotSupportedException or ArgumentException;

    private static bool TryMergeGoalSnapshots(
        GoalSnapshot baseline,
        GoalSnapshot stored,
        GoalSnapshot current,
        out GoalSnapshot merged,
        out string reason)
    {
        if (!string.Equals(baseline.Id, stored.Id, StringComparison.Ordinal) ||
            !string.Equals(baseline.Id, current.Id, StringComparison.Ordinal))
        {
            merged = stored;
            reason = "goal id changed during tick";
            return false;
        }

        if (!TryMergeTaskSnapshots(baseline.Tasks, stored.Tasks, current.Tasks, out var mergedTasks, out reason))
        {
            merged = stored;
            return false;
        }

        merged = stored with
        {
            Objective = PickStoreOwned(baseline.Objective, stored.Objective, current.Objective),
            Status = PickTickOwned(baseline.Status, stored.Status, current.Status),
            Tasks = mergedTasks,
            Timeline = MergeTimeline(baseline.Timeline, stored.Timeline, current.Timeline),
            DependsOn = PickStoreOwnedList(baseline.DependsOn, stored.DependsOn, current.DependsOn),
            SourceBacklogItemId = PickStoreOwned(baseline.SourceBacklogItemId, stored.SourceBacklogItemId, current.SourceBacklogItemId),
            RefinedSpec = PickStoreOwned(baseline.RefinedSpec, stored.RefinedSpec, current.RefinedSpec),
            LatestAcceptanceFailure = PickStoreOwned(baseline.LatestAcceptanceFailure, stored.LatestAcceptanceFailure, current.LatestAcceptanceFailure)
        };
        reason = "stored version advanced during tick; reapplied tick snapshot delta onto fresh goal row";
        return true;
    }

    private static bool TryMergeTaskSnapshots(
        IReadOnlyList<TaskSnapshot> baseline,
        IReadOnlyList<TaskSnapshot> stored,
        IReadOnlyList<TaskSnapshot> current,
        out IReadOnlyList<TaskSnapshot> merged,
        out string reason)
    {
        var baselineById = baseline.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var storedById = stored.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var currentById = current.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var taskIds = stored.Select(task => task.Id)
            .Concat(current.Select(task => task.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var results = new List<TaskSnapshot>(taskIds.Length);

        foreach (var taskId in taskIds)
        {
            var hasStored = storedById.TryGetValue(taskId, out var storedTask);
            var hasCurrent = currentById.TryGetValue(taskId, out var currentTask);
            var hasBaseline = baselineById.TryGetValue(taskId, out var baselineTask);

            if (!hasCurrent)
            {
                if (hasStored && baselineById.ContainsKey(taskId))
                {
                    results.Add(storedTask!);
                    continue;
                }

                reason = $"tick snapshot no longer contains task {ShortGoalId(taskId)}";
                merged = stored;
                return false;
            }

            if (!hasBaseline)
            {
                results.Add(hasStored ? storedTask! : currentTask!);
                continue;
            }

            if (!hasStored)
            {
                if (SnapshotEquals(baselineTask, currentTask))
                    continue;

                reason = $"stored goal no longer contains task {ShortGoalId(taskId)} changed by tick";
                merged = stored;
                return false;
            }

            results.Add(MergeTaskSnapshot(baselineTask!, storedTask!, currentTask!));
        }

        merged = results;
        reason = "stored version advanced during tick; merged task deltas";
        return true;
    }

    private static TaskSnapshot MergeTaskSnapshot(TaskSnapshot baseline, TaskSnapshot stored, TaskSnapshot current) =>
        stored with
        {
            Description = PickStoreOwned(baseline.Description, stored.Description, current.Description),
            RequiredRole = PickStoreOwned(baseline.RequiredRole, stored.RequiredRole, current.RequiredRole),
            Status = PickTickOwned(baseline.Status, stored.Status, current.Status),
            AssignedAgentId = PickTickOwned(baseline.AssignedAgentId, stored.AssignedAgentId, current.AssignedAgentId),
            LastExecution = PickTickOwned(baseline.LastExecution, stored.LastExecution, current.LastExecution),
            LastVerification = PickStoreOwned(baseline.LastVerification, stored.LastVerification, current.LastVerification),
            VerificationHistory = PickStoreOwnedList(baseline.VerificationHistory, stored.VerificationHistory, current.VerificationHistory),
            LastDispatch = PickTickOwned(baseline.LastDispatch, stored.LastDispatch, current.LastDispatch),
            LastProcess = PickTickOwned(baseline.LastProcess, stored.LastProcess, current.LastProcess),
            VerificationPlan = PickStoreOwned(baseline.VerificationPlan, stored.VerificationPlan, current.VerificationPlan),
            SubscriptionRetryAfter = PickStoreOwned(baseline.SubscriptionRetryAfter, stored.SubscriptionRetryAfter, current.SubscriptionRetryAfter),
            SubscriptionLimitReviewNote = PickStoreOwned(baseline.SubscriptionLimitReviewNote, stored.SubscriptionLimitReviewNote, current.SubscriptionLimitReviewNote),
            SubscriptionLimitReviewedAt = PickStoreOwned(baseline.SubscriptionLimitReviewedAt, stored.SubscriptionLimitReviewedAt, current.SubscriptionLimitReviewedAt),
            SubscriptionLimitReviewedFailureCount = PickStoreOwned(baseline.SubscriptionLimitReviewedFailureCount, stored.SubscriptionLimitReviewedFailureCount, current.SubscriptionLimitReviewedFailureCount),
            CriterionRetryCount = PickStoreOwned(baseline.CriterionRetryCount, stored.CriterionRetryCount, current.CriterionRetryCount),
            CriterionRetryFeedback = PickStoreOwnedList(baseline.CriterionRetryFeedback, stored.CriterionRetryFeedback, current.CriterionRetryFeedback),
            EmptyOutputRetryCount = PickStoreOwned(baseline.EmptyOutputRetryCount, stored.EmptyOutputRetryCount, current.EmptyOutputRetryCount)
        };

    private static IReadOnlyList<ProgressEventSnapshot> MergeTimeline(
        IReadOnlyList<ProgressEventSnapshot> baseline,
        IReadOnlyList<ProgressEventSnapshot> stored,
        IReadOnlyList<ProgressEventSnapshot> current)
    {
        var baselineKeys = baseline.Select(SnapshotKey).ToHashSet(StringComparer.Ordinal);
        var merged = stored.ToList();
        var mergedKeys = merged.Select(SnapshotKey).ToHashSet(StringComparer.Ordinal);
        foreach (var evt in current)
        {
            var key = SnapshotKey(evt);
            if (baselineKeys.Contains(key) || !mergedKeys.Add(key))
                continue;

            merged.Add(evt);
        }

        return merged.OrderBy(evt => evt.OccurredAt).ToList();
    }

    private static T PickTickOwned<T>(T baseline, T stored, T current) =>
        PickWithSameFieldPrecedence(baseline, stored, current, preferStoredOnConflict: false);

    private static T PickStoreOwned<T>(T baseline, T stored, T current) =>
        PickWithSameFieldPrecedence(baseline, stored, current, preferStoredOnConflict: true);

    private static T PickWithSameFieldPrecedence<T>(T baseline, T stored, T current, bool preferStoredOnConflict)
    {
        var storedChanged = !SnapshotEquals(baseline, stored);
        var currentChanged = !SnapshotEquals(baseline, current);

        return (storedChanged, currentChanged) switch
        {
            (true, true) => preferStoredOnConflict ? stored : current,
            (true, false) => stored,
            (false, true) => current,
            _ => stored
        };
    }

    private static IReadOnlyList<T>? PickStoreOwnedList<T>(
        IReadOnlyList<T>? baseline,
        IReadOnlyList<T>? stored,
        IReadOnlyList<T>? current) =>
        PickStoreOwned(baseline, stored, current);

    private static bool SnapshotEquals<T>(T? left, T? right) =>
        JsonSerializer.Serialize(left, SerializerOptions) == JsonSerializer.Serialize(right, SerializerOptions);

    private static string SnapshotKey<T>(T snapshot) =>
        JsonSerializer.Serialize(snapshot, SerializerOptions);

    private static string ShortGoalId(string goalId) =>
        goalId.Length <= 8 ? goalId : goalId[..8];

    private static async Task WriteSnapshotAsync(
        SqliteConnection conn,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken)
    {
        var snapshot = kernel.ExportSnapshot();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        foreach (var goal in snapshot.Goals)
        {
            await UpsertGoalRowAsync(conn, goal, updatedAt, versionSql: "goals.version + 1", cancellationToken);
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

            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            await UpsertGoalRowAsync(conn, snapshot, updatedAt, versionSql: "$version", cancellationToken, currentVersion + 1);
            await UpsertModelFitHistoryRowsAsync(conn, snapshot, cancellationToken);

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return true;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    private static async Task UpsertGoalRowAsync(
        SqliteConnection conn,
        GoalSnapshot goal,
        string updatedAt,
        string versionSql,
        CancellationToken cancellationToken,
        int? version = null)
    {
        var json = JsonSerializer.Serialize(goal, SerializerOptions);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO goals (id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version)
            VALUES ($id, $status, $objective, $source_backlog_item_id, $updated_at, $json, COALESCE($version, 1))
            ON CONFLICT(id) DO UPDATE SET
                status        = excluded.status,
                objective     = excluded.objective,
                source_backlog_item_id = excluded.source_backlog_item_id,
                snapshot_json = excluded.snapshot_json,
                version       = {{versionSql}},
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
        cmd.Parameters.AddWithValue("$version", version is null ? DBNull.Value : version.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task UpsertModelFitHistoryRowsAsync(
        SqliteConnection conn,
        GoalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            await UpsertModelFitHistoryRowAsync(conn, row, cancellationToken);
        }
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

public sealed record QuarantinedGoalSummary(string Id, string Error);
