using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IRunEventStore
{
    Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(
        long afterSequence = 0,
        string? goalId = null,
        int maxCount = 500,
        CancellationToken cancellationToken = default);
}

public static class RunEventTypes
{
    public const string GoalOperation = "goal.operation";
    public const string ConductorTick = "conductor.tick";
    public const string ConductorLifecycle = "conductor.lifecycle";
    public const string ConductorSupervision = "conductor.supervision";
    public const string RunEventMaintenance = "run-events.maintenance";
    public const string EvidenceRetention = "retention.reclamation";
    public const string PostLandingCanary = "acceptance.post-landing-canary";
}

public sealed record RunEventAppend(
    string EventType,
    string? GoalId,
    string? Operation,
    string? Status,
    string? Detail,
    string? PayloadJson,
    DateTimeOffset? OccurredAt = null,
    string? EventId = null);

public sealed record RunEventRecord(
    long Sequence,
    string EventId,
    DateTimeOffset OccurredAt,
    string EventType,
    string? GoalId,
    string? Operation,
    string? Status,
    string? Detail,
    string? PayloadJson);

public sealed record RunEventMaintenanceOptions(
    TimeSpan ConductorTickMaxAge,
    int MinConductorTickRowsToKeep = 750,
    bool Vacuum = false,
    DateTimeOffset? UtcNow = null,
    int MaxConductorTickPayloadBytes = 50000,
    int DeleteBatchSize = 1000,
    bool LegacyOversizedConductorTickPurge = false,
    int MaintenanceLockCommandTimeoutSeconds = 30,
    TimeSpan? TerminalGoalOperationMaxAge = null,
    IReadOnlyCollection<string>? TerminalGoalIds = null,
    long MaxDatabaseBytes = 512L * 1024 * 1024,
    TimeSpan? RecentTerminalGoalProtectionAge = null,
    int MaxDeleteBatchesPerPass = 4,
    int MaxIncrementalVacuumPagesPerPass = 4096,
    int ConsecutiveNoProgressAttempts = 0,
    int MaxNoProgressAttempts = 3,
    TimeSpan? ContinuationDelay = null,
    long MaterialGrowthToleranceBytes = 4096,
    bool OfflineVacuumAuthorized = false)
{
    public static RunEventMaintenanceOptions Default { get; } = new(TimeSpan.FromDays(7));

    public TimeSpan EffectiveTerminalGoalOperationMaxAge =>
        TerminalGoalOperationMaxAge ?? TimeSpan.FromDays(30);

    public TimeSpan EffectiveRecentTerminalGoalProtectionAge =>
        RecentTerminalGoalProtectionAge ?? TimeSpan.FromDays(7);

    public TimeSpan EffectiveContinuationDelay => ContinuationDelay ?? TimeSpan.FromMinutes(5);
}

public sealed record RunEventMaintenanceResult(
    bool Deferred,
    string? DeferredReason,
    int ConductorTickRowsDeleted,
    int AgedConductorTickRowsDeleted,
    int OversizedConductorTickRowsDeleted,
    long DeletedPayloadBytesEstimate,
    int MaxRowsDeletedInTransaction,
    TimeSpan Duration,
    long BytesBefore,
    long BytesAfter,
    bool VacuumRequested,
    bool VacuumCompleted,
    bool VacuumDeferred,
    int TerminalGoalOperationRowsDeleted = 0,
    SqliteMaintenanceDisposition Disposition = SqliteMaintenanceDisposition.Completed,
    SqliteMaintenanceReason Reason = SqliteMaintenanceReason.None,
    SqliteStorageSnapshot? StorageBefore = null,
    SqliteStorageSnapshot? StorageAfterMutation = null,
    SqliteStorageSnapshot? StorageAfterConvergence = null,
    SqliteCheckpointResult? Checkpoint = null,
    int RemainingEligibleRows = 0,
    long RemainingBytesOverBudget = 0,
    DateTimeOffset? NextAttemptAt = null,
    int ConsecutiveNoProgressAttempts = 0,
    int DeleteBatchesCompleted = 0);

public sealed class SqliteRunEventStore : IRunEventStore
{
    private const int DefaultMaxBusyRetries = 6;
    private static readonly TimeSpan MinimumRecentTerminalGoalProtectionAge = TimeSpan.FromDays(7);
    private readonly string _dbPath;
    private readonly int _busyTimeoutMilliseconds;
    private readonly int _maxBusyRetries;
    private readonly Func<CancellationToken, Task>? _beforeVacuumConvergenceCheckpoint;

    public SqliteRunEventStore(
        string dbPath,
        bool ensureSchema = true,
        int busyTimeoutMilliseconds = 30000,
        int maxBusyRetries = DefaultMaxBusyRetries)
    {
        _dbPath = dbPath;
        _busyTimeoutMilliseconds = Math.Max(0, busyTimeoutMilliseconds);
        _maxBusyRetries = Math.Max(1, maxBusyRetries);
        if (ensureSchema)
        {
            EnsureSchema();
        }
    }

    internal SqliteRunEventStore(
        string dbPath,
        Func<CancellationToken, Task> beforeVacuumConvergenceCheckpoint)
        : this(dbPath)
    {
        _beforeVacuumConvergenceCheckpoint = beforeVacuumConvergenceCheckpoint
            ?? throw new ArgumentNullException(nameof(beforeVacuumConvergenceCheckpoint));
    }

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public async Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            var eventId = string.IsNullOrWhiteSpace(evt.EventId) ? Guid.NewGuid().ToString("N") : evt.EventId;
            var occurredAt = evt.OccurredAt ?? DateTimeOffset.UtcNow;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO run_events (
                    event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                ) VALUES (
                    $event_id, $occurred_at, $event_type, $goal_id, $operation, $status, $detail, $payload_json
                );
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$event_id", eventId);
            cmd.Parameters.AddWithValue("$occurred_at", occurredAt.ToString("O"));
            cmd.Parameters.AddWithValue("$event_type", evt.EventType);
            cmd.Parameters.AddWithValue("$goal_id", (object?)evt.GoalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$operation", (object?)evt.Operation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)evt.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)evt.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$payload_json", (object?)evt.PayloadJson ?? DBNull.Value);
            var sequence = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
            await tx.CommitAsync(cancellationToken);
            return new RunEventRecord(
                sequence,
                eventId!,
                occurredAt,
                evt.EventType,
                evt.GoalId,
                evt.Operation,
                evt.Status,
                evt.Detail,
                evt.PayloadJson);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(
        long afterSequence = 0,
        string? goalId = null,
        int maxCount = 500,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                FROM run_events
                WHERE seq > $after_sequence
                  AND ($goal_id IS NULL OR goal_id = $goal_id)
                ORDER BY seq ASC
                LIMIT $max_count
                """;
            cmd.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
            cmd.Parameters.AddWithValue("$goal_id", (object?)goalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$max_count", Math.Clamp(maxCount, 1, 5000));
            var results = new List<RunEventRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new RunEventRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return results;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RunEventRecord>> ReadByTypeSinceAsync(
        string eventType,
        string? operation = null,
        long afterSequence = 0,
        int maxCount = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = operation is null
                ? """
                  SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                  FROM run_events
                  WHERE event_type = $event_type
                    AND seq > $after_sequence
                  ORDER BY seq ASC
                  LIMIT $max_count
                  """
                : """
                  SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                  FROM run_events
                  WHERE event_type = $event_type
                    AND operation = $operation
                    AND seq > $after_sequence
                  ORDER BY seq ASC
                  LIMIT $max_count
                  """;
            cmd.Parameters.AddWithValue("$event_type", eventType);
            if (operation is not null)
            {
                cmd.Parameters.AddWithValue("$operation", operation);
            }
            cmd.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
            cmd.Parameters.AddWithValue("$max_count", Math.Clamp(maxCount, 1, 5000));
            return await ReadRecordsAsync(cmd, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<RunEventRecord?> ReadByEventIdAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                FROM run_events
                WHERE event_id = $event_id
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$event_id", eventId);
            var records = await ReadRecordsAsync(cmd, cancellationToken).ConfigureAwait(false);
            return records.Count == 0 ? null : records[0];
        }, cancellationToken);
    }

    public async Task<RunEventRecord?> ReadBySequenceAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        if (sequence < 1)
        {
            return null;
        }

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                FROM run_events
                WHERE seq = $sequence
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$sequence", sequence);
            var records = await ReadRecordsAsync(cmd, cancellationToken).ConfigureAwait(false);
            return records.Count == 0 ? null : records[0];
        }, cancellationToken);
    }

    public async Task<RunEventRecord?> ReadLatestAsync(
        string eventType,
        string? operation = null,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = operation is null
                ? """
                  SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                  FROM run_events
                  WHERE event_type = $event_type
                  ORDER BY seq DESC
                  LIMIT 1
                  """
                : """
                  SELECT seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json
                  FROM run_events
                  WHERE event_type = $event_type
                    AND operation = $operation
                  ORDER BY seq DESC
                  LIMIT 1
                  """;
            cmd.Parameters.AddWithValue("$event_type", eventType);
            if (operation is not null)
            {
                cmd.Parameters.AddWithValue("$operation", operation);
            }

            var records = await ReadRecordsAsync(cmd, cancellationToken).ConfigureAwait(false);
            return records.Count == 0 ? null : records[0];
        }, cancellationToken);
    }

    public async Task<RunEventMaintenanceResult> MaintainAsync(
        RunEventMaintenanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= RunEventMaintenanceOptions.Default;
        if (options.MaintenanceLockCommandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaintenanceLockCommandTimeoutSeconds),
                options.MaintenanceLockCommandTimeoutSeconds,
                "The maintenance lock command timeout must be positive.");
        }

        if (options.MaxDatabaseBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxDatabaseBytes));
        if (options.MaxDeleteBatchesPerPass <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxDeleteBatchesPerPass));
        if (options.MaxIncrementalVacuumPagesPerPass <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxIncrementalVacuumPagesPerPass));
        if (options.MaxNoProgressAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxNoProgressAttempts));
        if (options.Vacuum && !options.OfflineVacuumAuthorized)
            throw new InvalidOperationException("Full VACUUM requires an exclusive offline maintenance lease.");

        var terminalGoalOperationMaxAge = options.EffectiveTerminalGoalOperationMaxAge;
        var recentTerminalGoalProtectionAge = options.EffectiveRecentTerminalGoalProtectionAge;
        if (terminalGoalOperationMaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.TerminalGoalOperationMaxAge));
        if (recentTerminalGoalProtectionAge < MinimumRecentTerminalGoalProtectionAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RecentTerminalGoalProtectionAge),
                recentTerminalGoalProtectionAge,
                $"Recent terminal goal evidence must be protected for at least {MinimumRecentTerminalGoalProtectionAge.TotalDays:0} days.");
        }
        if (recentTerminalGoalProtectionAge > terminalGoalOperationMaxAge)
        {
            throw new ArgumentException(
                "Recent terminal goal protection cannot exceed the terminal goal operation maximum age.",
                nameof(options.RecentTerminalGoalProtectionAge));
        }

        var clock = Stopwatch.StartNew();
        var now = options.UtcNow ?? DateTimeOffset.UtcNow;
        await using var conn = OpenConnection(busyTimeoutMilliseconds: 0);
        await PopulateTerminalGoalsAsync(conn, options.TerminalGoalIds, cancellationToken).ConfigureAwait(false);
        var storageBefore = SqliteStorageSnapshot.Measure(_dbPath);
        var remainingBatchBudget = Math.Max(1, options.MaxDeleteBatchesPerPass);
        var oversized = await PruneOversizedConductorTicksAsync(
            conn, options, remainingBatchBudget, cancellationToken).ConfigureAwait(false);
        remainingBatchBudget = Math.Max(0, remainingBatchBudget - oversized.BatchesCompleted);
        var aged = options.LegacyOversizedConductorTickPurge
            ? ConductorTickPruneResult.Empty
            : await PruneAgedConductorTicksAsync(
                conn, options, remainingBatchBudget, cancellationToken).ConfigureAwait(false);
        remainingBatchBudget = Math.Max(0, remainingBatchBudget - aged.BatchesCompleted);
        var goalOperations = await PruneAgedTerminalGoalOperationsAsync(
            conn, options, remainingBatchBudget, cancellationToken).ConfigureAwait(false);
        remainingBatchBudget = Math.Max(0, remainingBatchBudget - goalOperations.BatchesCompleted);
        var pressureCheckpoint = await ReadCheckpointAsync(conn, cancellationToken).ConfigureAwait(false);
        var storageBeforeBytePressure = SqliteStorageSnapshot.Measure(_dbPath);
        var bytePressureRequired = pressureCheckpoint.Completed &&
            storageBeforeBytePressure.TotalBytes > options.MaxDatabaseBytes;
        var bytePressureProbeDeferredByWorkCap = bytePressureRequired &&
            remainingBatchBudget == 0 &&
            !goalOperations.HasMore;
        var bytePressureOperations = bytePressureRequired &&
            remainingBatchBudget > 0 &&
            !goalOperations.HasMore
            ? await PruneTerminalGoalOperationsUnderBytePressureAsync(
                conn, options, remainingBatchBudget, cancellationToken).ConfigureAwait(false)
            : ConductorTickPruneResult.Empty;
        remainingBatchBudget = Math.Max(0, remainingBatchBudget - bytePressureOperations.BatchesCompleted);
        var allGoalOperations = goalOperations.Combine(bytePressureOperations);
        var deferred = oversized.Deferred || aged.Deferred || allGoalOperations.Deferred;
        var afterMutation = SqliteStorageSnapshot.Measure(_dbPath);
        if (deferred)
        {
            var deferredAttempts = options.ConsecutiveNoProgressAttempts + 1;
            var retryLimitReached = deferredAttempts >= options.MaxNoProgressAttempts;
            clock.Stop();
            return new RunEventMaintenanceResult(
                Deferred: !retryLimitReached,
                DeferredReason: retryLimitReached ? "database-busy-limit" : "database-busy",
                ConductorTickRowsDeleted: oversized.RowsDeleted + aged.RowsDeleted,
                AgedConductorTickRowsDeleted: aged.RowsDeleted,
                OversizedConductorTickRowsDeleted: oversized.RowsDeleted,
                DeletedPayloadBytesEstimate: oversized.DeletedPayloadBytesEstimate + aged.DeletedPayloadBytesEstimate + allGoalOperations.DeletedPayloadBytesEstimate,
                MaxRowsDeletedInTransaction: Math.Max(Math.Max(oversized.MaxRowsDeletedInTransaction, aged.MaxRowsDeletedInTransaction), allGoalOperations.MaxRowsDeletedInTransaction),
                Duration: clock.Elapsed,
                BytesBefore: storageBefore.TotalBytes,
                BytesAfter: afterMutation.TotalBytes,
                VacuumRequested: options.Vacuum,
                VacuumCompleted: false,
                VacuumDeferred: false,
                TerminalGoalOperationRowsDeleted: allGoalOperations.RowsDeleted,
                Disposition: retryLimitReached
                    ? SqliteMaintenanceDisposition.Stalled
                    : SqliteMaintenanceDisposition.Deferred,
                Reason: retryLimitReached
                    ? SqliteMaintenanceReason.DatabaseBusyLimit
                    : SqliteMaintenanceReason.DatabaseBusy,
                StorageBefore: storageBefore,
                StorageAfterMutation: afterMutation,
                StorageAfterConvergence: afterMutation,
                RemainingEligibleRows: oversized.NextBatchRows + aged.NextBatchRows + allGoalOperations.NextBatchRows,
                RemainingBytesOverBudget: BytesOverBudget(afterMutation, options.MaxDatabaseBytes),
                NextAttemptAt: retryLimitReached
                    ? null
                    : now.Add(DeferredBackoff(options.EffectiveContinuationDelay, deferredAttempts)),
                ConsecutiveNoProgressAttempts: deferredAttempts,
                DeleteBatchesCompleted: oversized.BatchesCompleted + aged.BatchesCompleted + allGoalOperations.BatchesCompleted);
        }

        var vacuumCompleted = false;
        var vacuumDeferred = false;
        var checkpoint = await ReadCheckpointAsync(conn, cancellationToken).ConfigureAwait(false);
        var autoVacuumMode = await ReadPragmaLongAsync(conn, "auto_vacuum", cancellationToken).ConfigureAwait(false);
        var freelistCount = await ReadPragmaLongAsync(conn, "freelist_count", cancellationToken).ConfigureAwait(false);
        var incrementalVacuumCapReached = false;
        if (checkpoint.Completed && autoVacuumMode == 2 && freelistCount > 0)
        {
            var pages = Math.Min(freelistCount, Math.Max(1, options.MaxIncrementalVacuumPagesPerPass));
            incrementalVacuumCapReached = pages < freelistCount;
            await RunNonQueryAsync(
                conn,
                $"PRAGMA incremental_vacuum({pages.ToString(CultureInfo.InvariantCulture)})",
                cancellationToken).ConfigureAwait(false);
            checkpoint = await ReadCheckpointAsync(conn, cancellationToken).ConfigureAwait(false);
        }

        if (options.Vacuum)
        {
            if (!checkpoint.Completed)
            {
                vacuumDeferred = true;
            }
            else
            {
                try
                {
                    await RunNonQueryAsync(conn, "VACUUM", cancellationToken).ConfigureAwait(false);
                    afterMutation = SqliteStorageSnapshot.Measure(_dbPath);
                    if (_beforeVacuumConvergenceCheckpoint is not null)
                    {
                        await _beforeVacuumConvergenceCheckpoint(cancellationToken).ConfigureAwait(false);
                    }
                    checkpoint = await ReadCheckpointAsync(conn, cancellationToken).ConfigureAwait(false);
                    vacuumCompleted = checkpoint.Completed;
                    vacuumDeferred = !checkpoint.Completed;
                }
                catch (SqliteException ex) when (IsTransientLock(ex))
                {
                    vacuumDeferred = true;
                }
            }
        }

        var afterConvergence = SqliteStorageSnapshot.Measure(_dbPath);
        var remainingEligibleRows = oversized.NextBatchRows + aged.NextBatchRows + allGoalOperations.NextBatchRows;
        var remainingBytes = BytesOverBudget(afterConvergence, options.MaxDatabaseBytes);
        var rowsDeleted = oversized.RowsDeleted + aged.RowsDeleted + allGoalOperations.RowsDeleted;
        var madeProgress = rowsDeleted > 0 || afterConvergence.TotalBytes < storageBefore.TotalBytes;
        var noProgressAttempts = (!checkpoint.Completed || vacuumDeferred || remainingBytes > 0) && !madeProgress
            ? options.ConsecutiveNoProgressAttempts + 1
            : 0;
        var decision = SqliteMaintenanceClassifier.Classify(
            storageBefore,
            afterConvergence,
            checkpoint,
            remainingEligibleRows,
            remainingBytes,
            noProgressAttempts,
            options.MaxNoProgressAttempts,
            checked((int)autoVacuumMode),
            freelistCount,
            vacuumDeferred,
            options.MaterialGrowthToleranceBytes,
            now.Add(options.EffectiveContinuationDelay),
            workCapReachedWithByteWorkPending:
                (bytePressureProbeDeferredByWorkCap || incrementalVacuumCapReached) && remainingBytes > 0);

        clock.Stop();
        return new RunEventMaintenanceResult(
            Deferred: false,
            DeferredReason: null,
            ConductorTickRowsDeleted: oversized.RowsDeleted + aged.RowsDeleted,
            AgedConductorTickRowsDeleted: aged.RowsDeleted,
            OversizedConductorTickRowsDeleted: oversized.RowsDeleted,
            DeletedPayloadBytesEstimate: oversized.DeletedPayloadBytesEstimate + aged.DeletedPayloadBytesEstimate + allGoalOperations.DeletedPayloadBytesEstimate,
            MaxRowsDeletedInTransaction: Math.Max(Math.Max(oversized.MaxRowsDeletedInTransaction, aged.MaxRowsDeletedInTransaction), allGoalOperations.MaxRowsDeletedInTransaction),
            Duration: clock.Elapsed,
            BytesBefore: storageBefore.TotalBytes,
            BytesAfter: afterConvergence.TotalBytes,
            VacuumRequested: options.Vacuum,
            VacuumCompleted: vacuumCompleted,
            VacuumDeferred: vacuumDeferred,
            TerminalGoalOperationRowsDeleted: allGoalOperations.RowsDeleted,
            Disposition: decision.Disposition,
            Reason: decision.Reason,
            StorageBefore: storageBefore,
            StorageAfterMutation: afterMutation,
            StorageAfterConvergence: afterConvergence,
            Checkpoint: checkpoint,
            RemainingEligibleRows: remainingEligibleRows,
            RemainingBytesOverBudget: remainingBytes,
            NextAttemptAt: decision.NextAttemptAt,
            ConsecutiveNoProgressAttempts: noProgressAttempts,
            DeleteBatchesCompleted: oversized.BatchesCompleted + aged.BatchesCompleted + allGoalOperations.BatchesCompleted);
    }

    private static long BytesOverBudget(SqliteStorageSnapshot storage, long maximumBytes) =>
        Math.Max(0, storage.TotalBytes - maximumBytes);

    private static TimeSpan DeferredBackoff(TimeSpan initialDelay, int attempt)
    {
        var multiplier = 1L << Math.Clamp(attempt - 1, 0, 6);
        var ticks = Math.Min(TimeSpan.FromHours(6).Ticks, checked(initialDelay.Ticks * multiplier));
        return TimeSpan.FromTicks(ticks);
    }

    private static async Task PopulateTerminalGoalsAsync(
        SqliteConnection conn,
        IReadOnlyCollection<string>? terminalGoalIds,
        CancellationToken cancellationToken)
    {
        await RunNonQueryAsync(
            conn,
            "CREATE TEMP TABLE IF NOT EXISTS maintenance_terminal_goals (goal_id TEXT PRIMARY KEY)",
            cancellationToken).ConfigureAwait(false);
        await RunNonQueryAsync(conn, "DELETE FROM maintenance_terminal_goals", cancellationToken).ConfigureAwait(false);
        if (terminalGoalIds is null || terminalGoalIds.Count == 0)
        {
            return;
        }

        await using var command = conn.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO maintenance_terminal_goals (goal_id) VALUES ($goal_id)";
        var parameter = command.Parameters.Add("$goal_id", SqliteType.Text);
        foreach (var goalId in terminalGoalIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            parameter.Value = goalId;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SqliteConnection OpenConnection()
    {
        return OpenConnection(_busyTimeoutMilliseconds);
    }

    private SqliteConnection OpenConnection(int busyTimeoutMilliseconds)
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, $"PRAGMA busy_timeout={Math.Max(0, busyTimeoutMilliseconds)}");
        return conn;
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var isNewDatabase = !File.Exists(_dbPath) || new FileInfo(_dbPath).Length == 0;
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        if (isNewDatabase)
            RunNonQuery(conn, "PRAGMA auto_vacuum=INCREMENTAL");
        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS run_events (
                seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id     TEXT NOT NULL UNIQUE,
                occurred_at  TEXT NOT NULL,
                event_type   TEXT NOT NULL,
                goal_id      TEXT NULL,
                operation    TEXT NULL,
                status       TEXT NULL,
                detail       TEXT NULL,
                payload_json TEXT NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_run_events_goal_seq ON run_events(goal_id, seq)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_run_events_type_seq ON run_events(event_type, seq)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_run_events_type_operation_seq ON run_events(event_type, operation, seq)");
    }

    private static async Task<IReadOnlyList<RunEventRecord>> ReadRecordsAsync(
        SqliteCommand cmd,
        CancellationToken cancellationToken)
    {
        var results = new List<RunEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RunEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return results;
    }

    private async Task<ConductorTickPruneResult> PruneOversizedConductorTicksAsync(
        SqliteConnection conn,
        RunEventMaintenanceOptions options,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        var maxPayloadBytes = Math.Max(0, options.MaxConductorTickPayloadBytes);
        if (maxPayloadBytes == 0)
        {
            return ConductorTickPruneResult.Empty;
        }

        var batchSize = NormalizeDeleteBatchSize(options.DeleteBatchSize);
        return await PruneConductorTickBatchesAsync(
            conn,
            batchSize,
            statsCommand =>
            {
                statsCommand.CommandText = """
                    SELECT COUNT(1), COALESCE(SUM(payload_bytes), 0)
                    FROM (
                        SELECT seq, LENGTH(payload_json) AS payload_bytes
                        FROM run_events
                        WHERE event_type = $event_type
                          AND payload_json IS NOT NULL
                          AND LENGTH(payload_json) > $max_payload_bytes
                        ORDER BY seq ASC
                        LIMIT $batch_size
                    )
                    """;
                statsCommand.Parameters.AddWithValue("$event_type", RunEventTypes.ConductorTick);
                statsCommand.Parameters.AddWithValue("$max_payload_bytes", maxPayloadBytes);
                statsCommand.Parameters.AddWithValue("$batch_size", batchSize);
            },
            deleteCommand =>
            {
                deleteCommand.CommandText = """
                    DELETE FROM run_events
                    WHERE seq IN (
                        SELECT seq
                        FROM run_events
                        WHERE event_type = $event_type
                          AND payload_json IS NOT NULL
                          AND LENGTH(payload_json) > $max_payload_bytes
                        ORDER BY seq ASC
                        LIMIT $batch_size
                    )
                    """;
                deleteCommand.Parameters.AddWithValue("$event_type", RunEventTypes.ConductorTick);
                deleteCommand.Parameters.AddWithValue("$max_payload_bytes", maxPayloadBytes);
                deleteCommand.Parameters.AddWithValue("$batch_size", batchSize);
            },
            options.MaintenanceLockCommandTimeoutSeconds,
            maxBatches,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConductorTickPruneResult> PruneAgedConductorTicksAsync(
        SqliteConnection conn,
        RunEventMaintenanceOptions options,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        var keepRows = Math.Max(0, options.MinConductorTickRowsToKeep);
        var cutoff = (options.UtcNow ?? DateTimeOffset.UtcNow)
            .Subtract(options.ConductorTickMaxAge)
            .ToString("O", CultureInfo.InvariantCulture);
        var batchSize = NormalizeDeleteBatchSize(options.DeleteBatchSize);
        return await PruneConductorTickBatchesAsync(
            conn,
            batchSize,
            statsCommand =>
            {
                statsCommand.CommandText = """
                    SELECT COUNT(1), COALESCE(SUM(payload_bytes), 0)
                    FROM (
                        SELECT seq, COALESCE(LENGTH(payload_json), 0) AS payload_bytes
                        FROM run_events
                        WHERE event_type = $event_type
                          AND occurred_at < $cutoff
                          AND seq NOT IN (
                              SELECT seq
                              FROM run_events
                              WHERE event_type = $event_type
                              ORDER BY seq DESC
                              LIMIT $keep_rows
                          )
                        ORDER BY seq ASC
                        LIMIT $batch_size
                    )
                    """;
                statsCommand.Parameters.AddWithValue("$event_type", RunEventTypes.ConductorTick);
                statsCommand.Parameters.AddWithValue("$cutoff", cutoff);
                statsCommand.Parameters.AddWithValue("$keep_rows", keepRows);
                statsCommand.Parameters.AddWithValue("$batch_size", batchSize);
            },
            deleteCommand =>
            {
                deleteCommand.CommandText = """
                    DELETE FROM run_events
                    WHERE seq IN (
                        SELECT seq
                        FROM run_events
                        WHERE event_type = $event_type
                          AND occurred_at < $cutoff
                          AND seq NOT IN (
                              SELECT seq
                              FROM run_events
                              WHERE event_type = $event_type
                              ORDER BY seq DESC
                              LIMIT $keep_rows
                          )
                        ORDER BY seq ASC
                        LIMIT $batch_size
                    )
                    """;
                deleteCommand.Parameters.AddWithValue("$event_type", RunEventTypes.ConductorTick);
                deleteCommand.Parameters.AddWithValue("$cutoff", cutoff);
                deleteCommand.Parameters.AddWithValue("$keep_rows", keepRows);
                deleteCommand.Parameters.AddWithValue("$batch_size", batchSize);
            },
            options.MaintenanceLockCommandTimeoutSeconds,
            maxBatches,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConductorTickPruneResult> PruneAgedTerminalGoalOperationsAsync(
        SqliteConnection conn,
        RunEventMaintenanceOptions options,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        if (options.TerminalGoalIds is null || options.TerminalGoalIds.Count == 0)
        {
            return ConductorTickPruneResult.Empty;
        }

        var cutoff = (options.UtcNow ?? DateTimeOffset.UtcNow)
            .Subtract(options.EffectiveTerminalGoalOperationMaxAge)
            .ToString("O", CultureInfo.InvariantCulture);
        return await PruneTerminalGoalOperationsBeforeAsync(
            conn, options, cutoff, maxBatches, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConductorTickPruneResult> PruneTerminalGoalOperationsUnderBytePressureAsync(
        SqliteConnection conn,
        RunEventMaintenanceOptions options,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        if (options.TerminalGoalIds is null || options.TerminalGoalIds.Count == 0)
        {
            return ConductorTickPruneResult.Empty;
        }

        var cutoff = (options.UtcNow ?? DateTimeOffset.UtcNow)
            .Subtract(options.EffectiveRecentTerminalGoalProtectionAge)
            .ToString("O", CultureInfo.InvariantCulture);
        return await PruneTerminalGoalOperationsBeforeAsync(
            conn, options, cutoff, maxBatches, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConductorTickPruneResult> PruneTerminalGoalOperationsBeforeAsync(
        SqliteConnection conn,
        RunEventMaintenanceOptions options,
        string cutoff,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        var batchSize = NormalizeDeleteBatchSize(options.DeleteBatchSize);
        return await PruneConductorTickBatchesAsync(
            conn,
            batchSize,
            statsCommand =>
            {
                statsCommand.CommandText = """
                    SELECT COUNT(1), COALESCE(SUM(payload_bytes), 0)
                    FROM (
                        SELECT seq, COALESCE(LENGTH(payload_json), 0) AS payload_bytes
                        FROM run_events
                        WHERE event_type = $event_type
                          AND occurred_at < $cutoff
                          AND EXISTS (
                              SELECT 1
                              FROM maintenance_terminal_goals terminal
                              WHERE terminal.goal_id = run_events.goal_id
                          )
                        ORDER BY seq ASC
                        LIMIT $batch_size
                    )
                    """;
                statsCommand.Parameters.AddWithValue("$event_type", RunEventTypes.GoalOperation);
                statsCommand.Parameters.AddWithValue("$cutoff", cutoff);
                statsCommand.Parameters.AddWithValue("$batch_size", batchSize);
            },
            deleteCommand =>
            {
                deleteCommand.CommandText = """
                    DELETE FROM run_events
                    WHERE seq IN (
                        SELECT seq
                        FROM run_events
                        WHERE event_type = $event_type
                          AND occurred_at < $cutoff
                          AND EXISTS (
                              SELECT 1
                              FROM maintenance_terminal_goals terminal
                              WHERE terminal.goal_id = run_events.goal_id
                          )
                        ORDER BY seq ASC
                        LIMIT $batch_size
                    )
                    """;
                deleteCommand.Parameters.AddWithValue("$event_type", RunEventTypes.GoalOperation);
                deleteCommand.Parameters.AddWithValue("$cutoff", cutoff);
                deleteCommand.Parameters.AddWithValue("$batch_size", batchSize);
            },
            options.MaintenanceLockCommandTimeoutSeconds,
            maxBatches,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConductorTickPruneResult> PruneConductorTickBatchesAsync(
        SqliteConnection conn,
        int batchSize,
        Action<SqliteCommand> configureStatsCommand,
        Action<SqliteCommand> configureDeleteCommand,
        int lockCommandTimeoutSeconds,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        var rowsDeleted = 0;
        var deletedPayloadBytesEstimate = 0L;
        var maxRowsDeletedInTransaction = 0;
        var batchesCompleted = 0;
        while (batchesCompleted < Math.Max(0, maxBatches))
        {
            try
            {
                await RunNonQueryAsync(
                    conn,
                    "BEGIN IMMEDIATE",
                    cancellationToken,
                    lockCommandTimeoutSeconds).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsTransientLock(ex))
            {
                return new ConductorTickPruneResult(
                    rowsDeleted,
                    deletedPayloadBytesEstimate,
                    maxRowsDeletedInTransaction,
                    Deferred: true,
                    HasMore: true,
                    NextBatchRows: 0,
                    BatchesCompleted: batchesCompleted);
            }

            var committed = false;
            try
            {
                await using var statsCommand = conn.CreateCommand();
                configureStatsCommand(statsCommand);
                var (batchRows, batchPayloadBytes) = await ReadBatchStatsAsync(statsCommand, cancellationToken).ConfigureAwait(false);
                if (batchRows == 0)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken).ConfigureAwait(false);
                    committed = true;
                    return new ConductorTickPruneResult(
                        rowsDeleted,
                        deletedPayloadBytesEstimate,
                        maxRowsDeletedInTransaction,
                        Deferred: false,
                        HasMore: false,
                        NextBatchRows: 0,
                        BatchesCompleted: batchesCompleted);
                }

                await using var deleteCommand = conn.CreateCommand();
                configureDeleteCommand(deleteCommand);
                var deleted = await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken).ConfigureAwait(false);
                committed = true;

                rowsDeleted += deleted;
                deletedPayloadBytesEstimate += batchPayloadBytes;
                maxRowsDeletedInTransaction = Math.Max(maxRowsDeletedInTransaction, deleted);
                batchesCompleted++;
                if (deleted < batchSize)
                {
                    return new ConductorTickPruneResult(
                        rowsDeleted,
                        deletedPayloadBytesEstimate,
                        maxRowsDeletedInTransaction,
                        Deferred: false,
                        HasMore: false,
                        NextBatchRows: 0,
                        BatchesCompleted: batchesCompleted);
                }
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqliteException)
                    {
                    }
                }
            }
        }

        await using var remainingCommand = conn.CreateCommand();
        configureStatsCommand(remainingCommand);
        var remaining = await ReadBatchStatsAsync(remainingCommand, cancellationToken).ConfigureAwait(false);
        return new ConductorTickPruneResult(
            rowsDeleted,
            deletedPayloadBytesEstimate,
            maxRowsDeletedInTransaction,
            Deferred: false,
            HasMore: remaining.Rows > 0,
            NextBatchRows: remaining.Rows,
            BatchesCompleted: batchesCompleted);
    }

    private static async Task<(int Rows, long PayloadBytes)> ReadBatchStatsAsync(
        SqliteCommand cmd,
        CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, 0);
        }

        return (Convert.ToInt32(reader.GetInt64(0)), reader.GetInt64(1));
    }

    private static async Task<SqliteCheckpointResult> ReadCheckpointAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQLite returned no wal_checkpoint receipt.");
        }

        return new SqliteCheckpointResult(
            Convert.ToInt32(reader.GetInt64(0)),
            Convert.ToInt32(reader.GetInt64(1)),
            Convert.ToInt32(reader.GetInt64(2)));
    }

    private static async Task<long> ReadPragmaLongAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static int NormalizeDeleteBatchSize(int value) => Math.Clamp(value, 1, 1000);

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
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
        {
            cmd.CommandTimeout = timeout;
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

    private async Task<T> WithBusyRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (attempt < _maxBusyRetries && IsTransientLock(ex))
            {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    private sealed record ConductorTickPruneResult(
        int RowsDeleted,
        long DeletedPayloadBytesEstimate,
        int MaxRowsDeletedInTransaction,
        bool Deferred,
        bool HasMore,
        int NextBatchRows,
        int BatchesCompleted)
    {
        public static ConductorTickPruneResult Empty { get; } = new(
            0, 0, 0, Deferred: false, HasMore: false, NextBatchRows: 0, BatchesCompleted: 0);

        public ConductorTickPruneResult Combine(ConductorTickPruneResult other) => new(
            RowsDeleted + other.RowsDeleted,
            DeletedPayloadBytesEstimate + other.DeletedPayloadBytesEstimate,
            Math.Max(MaxRowsDeletedInTransaction, other.MaxRowsDeletedInTransaction),
            Deferred || other.Deferred,
            HasMore || other.HasMore,
            NextBatchRows + other.NextBatchRows,
            BatchesCompleted + other.BatchesCompleted);
    }
}
