using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class SqliteOrchestratorStateRepository : IOrchestratorStateOutboxRepository
{
    public const string CurrentSchemaVersion = "1";
    private const int GoalMetadataTitleMaxChars = 240;
    private readonly string _dbPath;
    private readonly Action<string>? _statementObserver;
    private readonly SqliteWriteTelemetry _writeTelemetry;
    private readonly Action? _beforeOutboxCommit;
    private static readonly AsyncLocal<string?> CurrentWriteOperationTag = new();
    private static readonly string[] CoreSchemaTableNames =
    [
        "meta",
        "goals",
        "human_input_requests",
        "model_fit_history"
    ];
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public SqliteOrchestratorStateRepository(string dbPath)
        : this(dbPath, statementObserver: null, telemetryOptions: null)
    {
    }

    public static string ValidateReadOnlySchema(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();

        using var version = conn.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        var value = version.ExecuteScalar()?.ToString();
        if (!string.Equals(value, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"State schema is incompatible or has a pending migration: expected {CurrentSchemaVersion}, found {value ?? "missing"}.");
        }

        using var requiredColumns = conn.CreateCommand();
        requiredColumns.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name IN ('version', 'source_backlog_item_id')) +
                (SELECT COUNT(*) FROM pragma_table_info('model_fit_history') WHERE name IN ('outcome_rule', 'outcome_class', 'dispatch_lane'))
            """;
        // Keep the read-only successor check compatible with the immediately preceding
        // outbox schema. The successor runs the idempotent quarantine-column migration
        // only after this preflight succeeds and it opens the repository for writing.
        if (Convert.ToInt32(requiredColumns.ExecuteScalar(), CultureInfo.InvariantCulture) != 5)
        {
            throw new InvalidOperationException(
                "State schema has pending column migrations and cannot be opened safely by the successor.");
        }

        using var requiredObjects = conn.CreateCommand();
        requiredObjects.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type = 'table' AND name = 'engineering_practices')
               OR (type = 'table' AND name = 'state_outbox')
               OR (type = 'index' AND name IN (
                     'ix_goals_source_backlog_item_id',
                     'ix_goals_status',
                     'ix_model_fit_history_outcome_class',
                     'ix_state_outbox_kind'))
            """;
        if (Convert.ToInt32(requiredObjects.ExecuteScalar(), CultureInfo.InvariantCulture) != 6)
        {
            throw new InvalidOperationException(
                "State schema has pending table or index migrations and cannot be opened safely by the successor.");
        }

        return value;
    }

    internal SqliteOrchestratorStateRepository(string dbPath, Action<string>? statementObserver)
        : this(dbPath, statementObserver, telemetryOptions: null)
    {
    }

    internal SqliteOrchestratorStateRepository(
        string dbPath,
        Action<string>? statementObserver,
        SqliteWriteTelemetryOptions? telemetryOptions,
        Action? beforeOutboxCommit = null)
    {
        _dbPath = dbPath;
        _statementObserver = statementObserver;
        _writeTelemetry = new SqliteWriteTelemetry(dbPath, telemetryOptions);
        _beforeOutboxCommit = beforeOutboxCommit;
        EnsureSchema();
    }

    internal static IDisposable UseWriteOperationTag(string operationTag)
    {
        if (string.IsNullOrWhiteSpace(operationTag))
            throw new ArgumentException("Operation tag cannot be empty.", nameof(operationTag));

        var previous = CurrentWriteOperationTag.Value;
        CurrentWriteOperationTag.Value = operationTag.Trim();
        return new RestoreWriteOperationTag(previous);
    }

    private static string ResolveOperationTag(string fallback) =>
        string.IsNullOrWhiteSpace(CurrentWriteOperationTag.Value)
            ? fallback
            : CurrentWriteOperationTag.Value!;

    private static string ResolveOperationTag(string fallback, string operationName) =>
        string.IsNullOrWhiteSpace(operationName)
            ? throw new ArgumentException("Operation name cannot be empty.", nameof(operationName))
            : operationName.Trim();

    private sealed class RestoreWriteOperationTag : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public RestoreWriteOperationTag(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentWriteOperationTag.Value = _previous;
            _disposed = true;
        }
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
        RunNonQuery(conn, BusyTimeoutPragma());
        return conn;
    }

    // Cap on retrying a transient SQLITE_BUSY/LOCKED before giving up. busy_timeout (30s) handles the
    // simple lock-wait, but the deadlock-avoidance path (and pooling artifacts) can still surface an
    // immediate BUSY; this bounded retry turns that into a brief wait instead of a fatal throw that
    // would kill a conduct --loop on a concurrent writer.
    private const int MaxBusyRetries = 6;

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static async Task<T> WithBusyRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken ct,
        int maxBusyRetries = MaxBusyRetries)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (attempt < maxBusyRetries && IsTransientLock(ex))
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    // Opens a connection and acquires the write lock (BEGIN IMMEDIATE) with bounded retry. Only the
    // lock ACQUISITION is retried — once it returns, the caller runs its body exactly once, so no
    // side-effecting transaction delegate is ever re-executed.
    private async Task<WriteConnection> BeginWriteAsync(string operation, CancellationToken cancellationToken)
    {
        var acquisitionStopwatch = Stopwatch.StartNew();
        try
        {
            var conn = await WithBusyRetryAsync(async () =>
            {
                var conn = OpenConnection();
                try
                {
                    await SetBusyTimeoutAsync(conn, cancellationToken);
                    await RunNonQueryAsync(
                        conn,
                        "BEGIN IMMEDIATE",
                        cancellationToken,
                        _writeTelemetry.Options.BeginImmediateCommandTimeoutSeconds);
                    return conn;
                }
                catch
                {
                    await conn.DisposeAsync();
                    throw;
                }
            }, cancellationToken, _writeTelemetry.Options.MaxBusyRetries);
            acquisitionStopwatch.Stop();
            return new WriteConnection(
                conn,
                _writeTelemetry.StartScope(operation, acquisitionStopwatch.Elapsed));
        }
        catch (SqliteException ex) when (IsTransientLock(ex))
        {
            acquisitionStopwatch.Stop();
            _writeTelemetry.EmitBusyFailure(operation, acquisitionStopwatch.Elapsed, ex);
            throw;
        }
    }

    private string BusyTimeoutPragma() =>
        "PRAGMA busy_timeout=" + _writeTelemetry.Options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);

    private sealed record WriteConnection(
        SqliteConnection Connection,
        SqliteWriteTelemetry.WriteTelemetryScope Telemetry);

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, BusyTimeoutPragma());
        if (CoreSchemaTablesAlreadyExist(conn))
        {
            MigrateVersionColumn(conn);
            if (!PracticeRegistrySchemaExists(conn))
                PracticeRegistryStore.EnsureSchemaAndSeed(conn);
            if (!StateOutboxSchemaExists(conn))
                EnsureStateOutboxSchema(conn);
            MigrateStateOutboxColumns(conn);
            BackfillModelFitHistoryOutcomeColumns(conn);
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
                outcome_rule  TEXT NULL,
                outcome_class TEXT NOT NULL DEFAULT 'unknown-era',
                dispatch_lane TEXT NULL,
                PRIMARY KEY (goal_id, task_id, timestamp)
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_role ON model_fit_history(role)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_model ON model_fit_history(provider_name, model_name)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_outcome_class ON model_fit_history(outcome_class)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_status ON goals(status)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_goals_source_backlog_item_id ON goals(source_backlog_item_id)");
        EnsureStateOutboxSchema(conn);
        RunNonQuery(conn, $"INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '{CurrentSchemaVersion}')");
        PracticeRegistryStore.EnsureSchemaAndSeed(conn);
    }

    private void EnsureStateOutboxSchema(SqliteConnection conn)
    {
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS state_outbox (
                id                TEXT PRIMARY KEY,
                kind              TEXT NOT NULL,
                payload_json      TEXT NOT NULL,
                created_at        TEXT NOT NULL,
                quarantined_at    TEXT NULL,
                quarantine_reason TEXT NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_state_outbox_kind ON state_outbox(kind)");
    }

    private void MigrateStateOutboxColumns(SqliteConnection conn)
    {
        AddColumnIfMissing(
            conn,
            "state_outbox",
            "quarantined_at",
            "ALTER TABLE state_outbox ADD COLUMN quarantined_at TEXT NULL");
        AddColumnIfMissing(
            conn,
            "state_outbox",
            "quarantine_reason",
            "ALTER TABLE state_outbox ADD COLUMN quarantine_reason TEXT NULL");
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

        AddColumnIfMissing(conn, "model_fit_history", "outcome_rule", "ALTER TABLE model_fit_history ADD COLUMN outcome_rule TEXT NULL");
        AddColumnIfMissing(conn, "model_fit_history", "outcome_class", "ALTER TABLE model_fit_history ADD COLUMN outcome_class TEXT NOT NULL DEFAULT 'unknown-era'");
        AddColumnIfMissing(conn, "model_fit_history", "dispatch_lane", "ALTER TABLE model_fit_history ADD COLUMN dispatch_lane TEXT NULL");

        if (!IndexExists(conn, "ix_model_fit_history_outcome_class"))
            RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_model_fit_history_outcome_class ON model_fit_history(outcome_class)");
    }

    private void BackfillModelFitHistoryOutcomeColumns(SqliteConnection conn)
    {
        var kernel = LoadFromConnectionAsync(conn, goalIds: null, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE model_fit_history
                SET outcome_rule = $outcome_rule,
                    outcome_class = $outcome_class
                WHERE goal_id = $goal_id
                  AND task_id = $task_id
                  AND timestamp = $timestamp
                """;
            cmd.Parameters.AddWithValue("$outcome_rule", row.OutcomeRule ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome_class", TaskOutcomeClassifier.FormatClass(row.OutcomeClass));
            cmd.Parameters.AddWithValue("$goal_id", row.GoalId);
            cmd.Parameters.AddWithValue("$task_id", row.TaskId);
            cmd.Parameters.AddWithValue("$timestamp", row.Timestamp.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    private void AddColumnIfMissing(SqliteConnection conn, string tableName, string columnName, string alterSql)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $name";
        check.Parameters.AddWithValue("$name", columnName);
        if (Convert.ToInt32(check.ExecuteScalar()) == 0)
            RunNonQuery(conn, alterSql);
    }

    private static bool CoreSchemaTablesAlreadyExist(SqliteConnection conn)
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
        return Convert.ToInt32(cmd.ExecuteScalar()) == CoreSchemaTableNames.Length;
    }

    private static bool PracticeRegistrySchemaExists(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'engineering_practices'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool StateOutboxSchemaExists(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type = 'table' AND name = 'state_outbox')
               OR (type = 'index' AND name = 'ix_state_outbox_kind')
            """;
        return Convert.ToInt32(cmd.ExecuteScalar()) == 2;
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
        await SaveAsync(ResolveOperationTag(nameof(SaveAsync)), kernel, cancellationToken);
    }

    public async Task SaveAsync(
        string operationName,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(SaveAsync), operationName), cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task SaveGoalSnapshotsAsync(
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        await SaveGoalSnapshotsAsync(
            ResolveOperationTag($"{nameof(SaveGoalSnapshotsAsync)}({goals.Count})"),
            goals,
            cancellationToken);
    }

    public async Task SaveGoalSnapshotsAsync(
        string operationName,
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goals);

        if (goals.Count == 0)
            return;

        var write = await BeginWriteAsync(
            ResolveOperationTag($"{nameof(SaveGoalSnapshotsAsync)}({goals.Count})", operationName),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            // SQLite has no provider-level array/upsert binding here, so each row is still a
            // statement; the bulk boundary is one connection, one BEGIN IMMEDIATE, one COMMIT.
            foreach (var goal in goals)
            {
                var goalWrite = await UpsertGoalRowAsync(
                    conn,
                    goal,
                    updatedAt,
                    versionSql: "goals.version + 1",
                    cancellationToken,
                    skipUnchanged: true);
                telemetry.AddWrite(goalWrite);
                if (goalWrite.RowsWritten > 0)
                {
                    telemetry.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, goal, cancellationToken));
                }
            }

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
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

                    var normalized = NormalizeStoredVerificationStatus(merged, out var normalizedReason);
                    var resultReason = normalizedReason is null ? reason : $"{reason}; {normalizedReason}";
                    return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, GoalSnapshotSaveResult Result)>(
                        (true, normalized, new GoalSnapshotSaveResult(
                            request.Current.Id,
                            GoalSnapshotSaveDisposition.Merged,
                            normalized,
                            resultReason)));
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
            ResolveOperationTag(nameof(TransactAsync)),
            transaction,
            cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        string operationName,
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(
            ResolveOperationTag(nameof(TransactAsync), operationName),
            async (kernel, _, token) => await transaction(kernel, token),
            cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactAsync(ResolveOperationTag(nameof(TransactAsync)), transaction, cancellationToken);
    }

    public async Task<T> TransactAsync<T>(
        string operationName,
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(ResolveOperationTag(nameof(TransactAsync), operationName), cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);

            async Task CheckpointAsync()
            {
                await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);
            }

            var (shouldSave, result) = await transaction(kernel, CheckpointAsync, cancellationToken);

            if (shouldSave)
                await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return result;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task<T> TransactWithOutboxAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(
            bool ShouldSave,
            T Result,
            IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TransactWithOutboxAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            var kernel = await LoadFromConnectionAsync(conn, goalIds: null, cancellationToken);
            var (shouldSave, result, outboxMessages) = await transaction(kernel, cancellationToken);

            if (shouldSave)
                await WriteSnapshotAsync(conn, kernel, telemetry, cancellationToken);

            foreach (var message in outboxMessages)
            {
                await InsertOutboxMessageAsync(conn, message, cancellationToken);
            }

            _beforeOutboxCommit?.Invoke();
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return result;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<OrchestratorStateOutboxMessage>> ListOutboxMessagesAsync(
        string kind,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, payload_json, created_at
            FROM state_outbox
            WHERE kind = $kind AND quarantined_at IS NULL
            ORDER BY created_at, id
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var messages = new List<OrchestratorStateOutboxMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OrchestratorStateOutboxMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return messages;
    }

    public async Task<bool> TryProcessOutboxMessageAsync(
        string id,
        Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> processor,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TryProcessOutboxMessageAsync)),
            cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        try
        {
            OrchestratorStateOutboxMessage? message;
            await using (var select = conn.CreateCommand())
            {
                select.CommandText = """
                    SELECT id, kind, payload_json, created_at
                    FROM state_outbox
                    WHERE id = $id AND quarantined_at IS NULL
                    """;
                select.Parameters.AddWithValue("$id", id);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                message = await reader.ReadAsync(cancellationToken)
                    ? new OrchestratorStateOutboxMessage(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        DateTimeOffset.Parse(
                            reader.GetString(3),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind))
                    : null;
            }

            if (message is null)
            {
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                telemetry.Emit("commit");
                return false;
            }

            var result = await processor(message, cancellationToken);
            await using var cmd = conn.CreateCommand();
            if (result.Disposition == OrchestratorStateOutboxDisposition.Complete)
            {
                cmd.CommandText = "DELETE FROM state_outbox WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(result.Detail))
                {
                    throw new InvalidOperationException(
                        $"Outbox message '{id}' cannot be quarantined without a reason.");
                }

                cmd.CommandText = """
                    UPDATE state_outbox
                    SET quarantined_at = $quarantined_at,
                        quarantine_reason = $quarantine_reason
                    WHERE id = $id
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue(
                    "$quarantined_at",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$quarantine_reason", result.Detail);
            }

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return true;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private static async Task InsertOutboxMessageAsync(
        SqliteConnection conn,
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO state_outbox (id, kind, payload_json, created_at)
            VALUES ($id, $kind, $payload_json, $created_at)
            ON CONFLICT(id) DO UPDATE SET
                kind = excluded.kind,
                payload_json = excluded.payload_json,
                created_at = excluded.created_at
            """;
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.Parameters.AddWithValue("$kind", message.Kind);
        cmd.Parameters.AddWithValue("$payload_json", message.PayloadJson);
        cmd.Parameters.AddWithValue("$created_at", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        cmd.CommandText = $"""
            SELECT
                id,
                status,
                {GoalMetadataTitleSql()},
                updated_at,
                (
                    SELECT json_extract(task.value, '$.LastDispatch.ResultCommit')
                    FROM json_each(goals.snapshot_json, '$.Tasks') AS task
                    WHERE COALESCE(json_extract(task.value, '$.LastDispatch.ResultCommit'), '') <> ''
                    ORDER BY CAST(task.key AS INTEGER) DESC
                    LIMIT 1
                ) AS result_commit,
                (
                    SELECT json_extract(evt.value, '$.OccurredAt')
                    FROM json_each(goals.snapshot_json, '$.Timeline') AS evt
                    ORDER BY CAST(evt.key AS INTEGER) ASC
                    LIMIT 1
                ) AS created_at,
                (
                    SELECT json_extract(evt.value, '$.OccurredAt')
                    FROM json_each(goals.snapshot_json, '$.Timeline') AS evt
                    ORDER BY CAST(evt.key AS INTEGER) DESC
                    LIMIT 1
                ) AS terminated_at
            FROM goals
            ORDER BY updated_at DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GoalSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }

        return results;
    }

    public async Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goalIds);
        if (goalIds.Count == 0)
        {
            return [];
        }

        await using var conn = OpenConnection();
        var results = new HashSet<string>(StringComparer.Ordinal);
        var parameterNames = goalIds.Select((_, index) => $"$goal_id{index}").ToArray();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT goal_id, snapshot_json
            FROM human_input_requests
            WHERE goal_id IN ({string.Join(", ", parameterNames)})
            """;
        var index = 0;
        foreach (var goalId in goalIds)
        {
            cmd.Parameters.AddWithValue(parameterNames[index], goalId.Value);
            index++;
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var snapshot = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(reader.GetString(1), SerializerOptions);
            if (snapshot?.IsCompleted == true &&
                !IsSyntheticParkedHumanWaitCompletion(snapshot))
            {
                results.Add(reader.GetString(0));
            }
        }

        return results.Select(id => new GoalId(id)).ToArray();
    }

    private static bool IsSyntheticParkedHumanWaitCompletion(HumanInputRequestSnapshot snapshot) =>
        !snapshot.WasDismissed &&
        snapshot.Answer?.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase) == true;

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
            SELECT goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp, outcome_rule, outcome_class, dispatch_lane
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
            SELECT goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp, outcome_rule, outcome_class, dispatch_lane
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

        if (goalIds is null || goalIds.Count > 0)
        {
            await using var cmd = conn.CreateCommand();
            if (goalIds is null)
            {
                cmd.CommandText = "SELECT snapshot_json FROM human_input_requests";
            }
            else
            {
                var parameterNames = goalIds.Select((_, index) => $"$goal_id{index}").ToArray();
                cmd.CommandText = $"SELECT snapshot_json FROM human_input_requests WHERE goal_id IN ({string.Join(", ", parameterNames)})";
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
                var snap = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(reader.GetString(0), SerializerOptions);
                if (snap != null) humanInputSnapshots.Add(snap);
            }
        }

        var kernel = AgentOrchestratorKernel.FromSnapshot(
            new OrchestratorSnapshot(goalSnapshots, humanInputSnapshots));
        kernel.SetEngineeringPractices(PracticeRegistryStore.ListActive(conn));
        return kernel;
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
            LatestAcceptanceFailure = PickStoreOwned(baseline.LatestAcceptanceFailure, stored.LatestAcceptanceFailure, current.LatestAcceptanceFailure),
            AutomaticAcceptanceRetryCount = PickStoreOwned(
                baseline.AutomaticAcceptanceRetryCount,
                stored.AutomaticAcceptanceRetryCount,
                current.AutomaticAcceptanceRetryCount),
            OperatorAcceptanceRegateCount = PickStoreOwned(
                baseline.OperatorAcceptanceRegateCount,
                stored.OperatorAcceptanceRegateCount,
                current.OperatorAcceptanceRegateCount),
            EffectiveAcceptanceCriteriaCorrections = PickStoreOwnedList(
                baseline.EffectiveAcceptanceCriteriaCorrections,
                stored.EffectiveAcceptanceCriteriaCorrections,
                current.EffectiveAcceptanceCriteriaCorrections)
        };
        reason = "stored version advanced during tick; reapplied tick snapshot delta onto fresh goal row";
        return true;
    }

    private static GoalSnapshot NormalizeStoredVerificationStatus(GoalSnapshot snapshot, out string? reason)
    {
        reason = null;
        if (snapshot.Status is GoalStatus.Verified or GoalStatus.Parked or GoalStatus.WaitingForHuman)
        {
            return snapshot;
        }

        if (snapshot.Status is GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            return snapshot;
        }

        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        var goalId = new GoalId(snapshot.Id);
        if (!kernel.ReconcileGoalVerificationStatus(
            goalId,
            "Tick merge reconciled stored all-task verification gates to Verified."))
        {
            return snapshot;
        }

        reason = "reconciled stored all-task verification gates to Verified";
        return kernel.ExportSnapshot().Goals.Single();
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
            EmptyOutputRetryCount = PickStoreOwned(baseline.EmptyOutputRetryCount, stored.EmptyOutputRetryCount, current.EmptyOutputRetryCount),
            LatestRetryAt = PickStoreOwned(baseline.LatestRetryAt, stored.LatestRetryAt, current.LatestRetryAt),
            PendingRetryRoundKind = PickStoreOwned(baseline.PendingRetryRoundKind, stored.PendingRetryRoundKind, current.PendingRetryRoundKind)
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
        SqliteWriteTelemetry.WriteTelemetryScope? telemetry,
        CancellationToken cancellationToken)
    {
        var snapshot = kernel.ExportSnapshot();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");

        foreach (var goal in snapshot.Goals)
        {
            var goalWrite = await UpsertGoalRowAsync(
                conn,
                goal,
                updatedAt,
                versionSql: "goals.version + 1",
                cancellationToken,
                skipUnchanged: true);
            telemetry?.AddWrite(goalWrite);
            if (goalWrite.RowsWritten > 0)
            {
                telemetry?.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, goal, cancellationToken));
            }
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
            telemetry?.AddWrite(await cmd.ExecuteNonQueryAsync(cancellationToken), Encoding.UTF8.GetByteCount(json));
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

    public static async Task<long?> TryLoadGoalStateVersionAsync(
        string dbPath,
        string goalId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dbPath) ||
            string.IsNullOrWhiteSpace(goalId) ||
            !File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False;");
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(version, 0) FROM goals WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", goalId);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public async Task<T> TransactGoalAsync<T>(
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactGoalAsync(
            ResolveOperationTag($"{nameof(TransactGoalAsync)}({ShortGoalId(goalId.Value)})"),
            goalId,
            transaction,
            cancellationToken);
    }

    public async Task<T> TransactGoalAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var versionMismatchDelay = 50;
        var resolvedOperation = ResolveOperationTag($"{nameof(TransactGoalAsync)}({ShortGoalId(goalId.Value)})", operationName);
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
            var casSucceeded = await TryCasWriteGoalRowAsync(
                goalId,
                newSnapshot,
                humanInputRequests: null,
                loadedVersion,
                resolvedOperation,
                cancellationToken);
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

    public async Task<T> TransactGoalStateAsync<T>(
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await TransactGoalStateAsync(
            ResolveOperationTag($"{nameof(TransactGoalStateAsync)}({ShortGoalId(goalId.Value)})"),
            goalId,
            transaction,
            cancellationToken);
    }

    public async Task<T> TransactGoalStateAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var versionMismatchDelay = 50;
        var resolvedOperation = ResolveOperationTag($"{nameof(TransactGoalStateAsync)}({ShortGoalId(goalId.Value)})", operationName);
        for (var attempt = 1; ; attempt++)
        {
            var (loadedState, loadedVersion) = await LoadGoalStateAndVersionAsync(goalId, cancellationToken);
            var (shouldSave, newState, result) = await transaction(loadedState, cancellationToken);

            if (!shouldSave || newState is null)
                return result;

            if (!string.Equals(newState.Goal.Id, goalId.Value, StringComparison.Ordinal) ||
                newState.HumanInputRequests.Any(request =>
                    !string.Equals(request.GoalId, goalId.Value, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"TransactGoalStateAsync for goal {goalId.Value[..8]} cannot persist state owned by another goal.");
            }

            var casSucceeded = await TryCasWriteGoalRowAsync(
                goalId,
                newState.Goal,
                newState.HumanInputRequests,
                loadedVersion,
                resolvedOperation,
                cancellationToken);
            if (casSucceeded)
                return result;

            if (attempt >= MaxBusyRetries)
                throw new InvalidOperationException(
                    $"TransactGoalStateAsync: optimistic concurrency retries exhausted for goal {goalId.Value[..8]}");

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

    private async Task<(GoalStateSnapshot? State, int Version)> LoadGoalStateAndVersionAsync(
        GoalId goalId,
        CancellationToken cancellationToken)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            GoalSnapshot? goalSnapshot;
            int version;
            await using (var goalCommand = conn.CreateCommand())
            {
                goalCommand.CommandText = "SELECT snapshot_json, COALESCE(version, 0) FROM goals WHERE id = $id";
                goalCommand.Parameters.AddWithValue("$id", goalId.Value);
                await using var reader = await goalCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return ((GoalStateSnapshot?)null, 0);

                goalSnapshot = JsonSerializer.Deserialize<GoalSnapshot>(reader.GetString(0), SerializerOptions);
                version = reader.GetInt32(1);
            }

            var humanInputRequests = new List<HumanInputRequestSnapshot>();
            await using (var inputCommand = conn.CreateCommand())
            {
                inputCommand.CommandText = "SELECT snapshot_json FROM human_input_requests WHERE goal_id = $goal_id";
                inputCommand.Parameters.AddWithValue("$goal_id", goalId.Value);
                await using var reader = await inputCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var request = JsonSerializer.Deserialize<HumanInputRequestSnapshot>(
                        reader.GetString(0),
                        SerializerOptions);
                    if (request is not null)
                        humanInputRequests.Add(request);
                }
            }

            return (new GoalStateSnapshot(goalSnapshot!, humanInputRequests), version);
        }, cancellationToken);
    }

    // Attempts a short CAS write for one goal row. Acquires BEGIN IMMEDIATE, re-reads the version,
    // writes only if it matches expectedVersion, increments version, then commits.
    // Returns true on success, false when the version has changed (caller should retry).
    private async Task<bool> TryCasWriteGoalRowAsync(
        GoalId goalId,
        GoalSnapshot snapshot,
        IReadOnlyList<HumanInputRequestSnapshot>? humanInputRequests,
        int expectedVersion,
        string operationName,
        CancellationToken cancellationToken)
    {
        var write = await BeginWriteAsync(operationName, cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
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
                telemetry.Emit("rollback");
                return false;
            }

            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            telemetry.AddWrite(await UpsertGoalRowAsync(conn, snapshot, updatedAt, versionSql: "$version", cancellationToken, currentVersion + 1));
            telemetry.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, snapshot, cancellationToken));
            if (humanInputRequests is not null)
            {
                foreach (var request in humanInputRequests)
                {
                    var json = JsonSerializer.Serialize(request, SerializerOptions);
                    await using var inputCommand = conn.CreateCommand();
                    inputCommand.CommandText = """
                        INSERT INTO human_input_requests (id, goal_id, snapshot_json)
                        VALUES ($id, $goal_id, $json)
                        ON CONFLICT(id) DO UPDATE SET
                            goal_id       = excluded.goal_id,
                            snapshot_json = excluded.snapshot_json
                        """;
                    inputCommand.Parameters.AddWithValue("$id", request.Id);
                    inputCommand.Parameters.AddWithValue("$goal_id", request.GoalId);
                    inputCommand.Parameters.AddWithValue("$json", json);
                    telemetry.AddWrite(
                        await inputCommand.ExecuteNonQueryAsync(cancellationToken),
                        Encoding.UTF8.GetByteCount(json));
                }
            }

            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
            return true;
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
    }

    private static async Task<(int RowsWritten, long SerializedBytes)> UpsertGoalRowAsync(
        SqliteConnection conn,
        GoalSnapshot goal,
        string updatedAt,
        string versionSql,
        CancellationToken cancellationToken,
        int? version = null,
        bool skipUnchanged = false)
    {
        if (goal.IsMetadataOnly)
        {
            throw new InvalidOperationException($"Refusing to persist metadata-only goal snapshot '{ShortGoalId(goal.Id)}'. Hydrate the full aggregate before saving.");
        }

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
            WHERE $skip_unchanged = 0 OR excluded.snapshot_json != goals.snapshot_json
            """;
        cmd.Parameters.AddWithValue("$id", goal.Id);
        cmd.Parameters.AddWithValue("$status", goal.Status.ToString());
        cmd.Parameters.AddWithValue("$objective", goal.Objective);
        cmd.Parameters.AddWithValue("$source_backlog_item_id", (object?)goal.SourceBacklogItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$version", version is null ? DBNull.Value : version.Value);
        cmd.Parameters.AddWithValue("$skip_unchanged", skipUnchanged ? 1 : 0);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return (rows, rows > 0 ? Encoding.UTF8.GetByteCount(json) : 0);
    }

    private static string GoalMetadataTitleSql()
    {
        var normalized = "replace(objective, char(13), char(10))";
        var firstLine = $"CASE WHEN instr({normalized}, char(10)) > 0 THEN substr({normalized}, 1, instr({normalized}, char(10)) - 1) ELSE objective END";
        return $"substr(({firstLine}), 1, {GoalMetadataTitleMaxChars})";
    }

    private static async Task<(int RowsWritten, long SerializedBytes)> UpsertModelFitHistoryRowAsync(
        SqliteConnection conn,
        ModelFitHistoryRow row,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_fit_history (
                goal_id, task_id, role, provider_name, model_name, complexity, task_shape, outcome, self_rating, timestamp, outcome_rule, outcome_class, dispatch_lane)
            VALUES (
                $goal_id, $task_id, $role, $provider_name, $model_name, $complexity, $task_shape, $outcome, $self_rating, $timestamp, $outcome_rule, $outcome_class, $dispatch_lane)
            ON CONFLICT(goal_id, task_id, timestamp) DO UPDATE SET
                role          = excluded.role,
                provider_name = excluded.provider_name,
                model_name    = excluded.model_name,
                complexity    = excluded.complexity,
                task_shape    = excluded.task_shape,
                outcome       = excluded.outcome,
                self_rating   = excluded.self_rating,
                outcome_rule  = excluded.outcome_rule,
                outcome_class = excluded.outcome_class,
                dispatch_lane = excluded.dispatch_lane
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
        cmd.Parameters.AddWithValue("$outcome_rule", row.OutcomeRule ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$outcome_class", TaskOutcomeClassifier.FormatClass(row.OutcomeClass));
        cmd.Parameters.AddWithValue("$dispatch_lane", row.DispatchLane ?? (object)DBNull.Value);
        return (await cmd.ExecuteNonQueryAsync(cancellationToken), 0);
    }

    private static async Task<(int RowsWritten, long SerializedBytes)> UpsertModelFitHistoryRowsAsync(
        SqliteConnection conn,
        GoalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var rowsWritten = 0;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        foreach (var row in ModelFitHistory.FromGoals(kernel.Goals))
        {
            var written = await UpsertModelFitHistoryRowAsync(conn, row, cancellationToken);
            rowsWritten += written.RowsWritten;
        }

        return (rowsWritten, 0);
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
            DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            TaskOutcomeClassifier.ParseClass(reader.IsDBNull(11) ? null : reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private async Task SetBusyTimeoutAsync(SqliteConnection conn, CancellationToken cancellationToken)
        => await RunNonQueryAsync(conn, BusyTimeoutPragma(), cancellationToken);

    private void RunNonQuery(SqliteConnection conn, string sql)
    {
        _statementObserver?.Invoke(sql);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task RunNonQueryAsync(
        SqliteConnection conn,
        string sql,
        CancellationToken cancellationToken,
        int? commandTimeoutSeconds = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (commandTimeoutSeconds is { } timeout)
            cmd.CommandTimeout = timeout;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record GoalSummary(
    string Id,
    string Status,
    string Objective,
    string UpdatedAt,
    string? ResultCommit = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? TerminatedAt = null);

public sealed record QuarantinedGoalSummary(string Id, string Error);
