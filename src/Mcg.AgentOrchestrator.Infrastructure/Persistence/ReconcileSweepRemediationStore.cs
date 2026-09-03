using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ReconcileSweepRemediationState(
    string StateKey,
    int AttemptCount,
    bool BlockerEmitted,
    bool EscalationEmitted,
    string? InFlightOwner,
    DateTimeOffset? InFlightAt);

public sealed record ReconcileSweepAttemptClaim(bool Claimed, int AttemptNumber, string? Owner);

public sealed record ReconcileAcceptanceLeaseState(
    string Owner,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc);

public interface IReconcileSweepRemediationStore
{
    ReconcileSweepRemediationState Observe(string stateKey, string goalId, string blockerKind, string evidence, string remedy);
    bool TryMarkBlockerEmitted(string stateKey);
    ReconcileSweepAttemptClaim TryClaimAttempt(string stateKey, int maximumAttempts, string owner);
    void CompleteAttempt(string stateKey, string owner, int exitStatus, string output, bool consumeAttempt = true);
    bool TryMarkEscalationEmitted(string stateKey);
    bool TryClaimAcceptanceLease(string goalId, string owner, TimeSpan staleAfter);
    IDisposable? TryAcquireAcceptanceLease(string goalId, string owner, TimeSpan staleAfter);
    ReconcileAcceptanceLeaseState? TryGetAcceptanceLease(string goalId, TimeSpan staleAfter);
    string? TryGetAcceptanceLeaseOwner(string goalId);
    void ReleaseAcceptanceLease(string goalId, string owner);
}

public sealed class ReconcileSweepRemediationStore : IReconcileSweepRemediationStore
{
    private readonly string _dbPath;
    private readonly int _busyTimeoutSeconds;
    private readonly Func<int, TimeSpan, CancellationToken, Task>? _busyRetryDelay;
    private readonly SqliteWriteTelemetry _writeTelemetry;
    private readonly TimeProvider _timeProvider;

    public ReconcileSweepRemediationStore(string dbPath)
        : this(dbPath, busyTimeoutSeconds: 5, busyRetryDelay: null)
    {
    }

    internal ReconcileSweepRemediationStore(
        string dbPath,
        int busyTimeoutSeconds,
        Func<int, TimeSpan, CancellationToken, Task>? busyRetryDelay,
        SqliteWriteTelemetryOptions? writeTelemetryOptions = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentOutOfRangeException.ThrowIfNegative(busyTimeoutSeconds);
        _dbPath = Path.GetFullPath(dbPath);
        _busyTimeoutSeconds = busyTimeoutSeconds;
        _busyRetryDelay = busyRetryDelay;
        _writeTelemetry = new SqliteWriteTelemetry(_dbPath, writeTelemetryOptions);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        if (StateDbWriteSession.TryExecute(_dbPath, EnsureSchema))
        {
            return;
        }

        using var connection = Open();
        EnsureSchema(connection);
    }

    public ReconcileSweepRemediationState Observe(
        string stateKey,
        string goalId,
        string blockerKind,
        string evidence,
        string remedy)
    {
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = """
                    INSERT OR IGNORE INTO reconcile_sweep_remediation
                        (state_key, goal_id, blocker_kind, evidence, remedy, attempt_count,
                         blocker_emitted, escalation_emitted, in_flight_owner, in_flight_at,
                         last_exit_status, last_output, updated_at)
                    VALUES
                        ($key, $goal, $kind, $evidence, $remedy, 0, 0, 0, NULL, NULL, NULL, NULL, $at)
                    """;
                insert.Parameters.AddWithValue("$key", stateKey);
                insert.Parameters.AddWithValue("$goal", goalId);
                insert.Parameters.AddWithValue("$kind", blockerKind);
                insert.Parameters.AddWithValue("$evidence", evidence);
                insert.Parameters.AddWithValue("$remedy", remedy);
                insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            var state = ReadState(connection, stateKey);
            Commit(connection);
            return state;
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    public bool TryMarkBlockerEmitted(string stateKey) =>
        TrySetOnce(stateKey, "blocker_emitted");

    public bool TryMarkEscalationEmitted(string stateKey) =>
        TrySetOnce(stateKey, "escalation_emitted");

    public ReconcileSweepAttemptClaim TryClaimAttempt(string stateKey, int maximumAttempts, string owner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            var state = ReadState(connection, stateKey);
            if (!string.IsNullOrWhiteSpace(state.InFlightOwner) &&
                state.InFlightAt is { } inFlightAt &&
                inFlightAt < DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(30)))
            {
                using var recover = connection.CreateCommand();
                recover.CommandText = """
                    UPDATE reconcile_sweep_remediation
                    SET in_flight_owner = NULL,
                        in_flight_at = NULL,
                        last_exit_status = 1,
                        last_output = 'interrupted attempt recovered after stale claim',
                        updated_at = $at
                    WHERE state_key = $key
                    """;
                recover.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                recover.Parameters.AddWithValue("$key", stateKey);
                recover.ExecuteNonQuery();
                state = state with { InFlightOwner = null, InFlightAt = null };
            }
            if (state.AttemptCount >= maximumAttempts || !string.IsNullOrWhiteSpace(state.InFlightOwner))
            {
                Commit(connection);
                return new ReconcileSweepAttemptClaim(false, state.AttemptCount, state.InFlightOwner);
            }

            var attempt = state.AttemptCount + 1;
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE reconcile_sweep_remediation
                SET attempt_count = $attempt,
                    in_flight_owner = $owner,
                    in_flight_at = $at,
                    updated_at = $at
                WHERE state_key = $key
                """;
            update.Parameters.AddWithValue("$attempt", attempt);
            update.Parameters.AddWithValue("$owner", owner);
            update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$key", stateKey);
            update.ExecuteNonQuery();
            Commit(connection);
            return new ReconcileSweepAttemptClaim(true, attempt, owner);
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    public void CompleteAttempt(
        string stateKey,
        string owner,
        int exitStatus,
        string output,
        bool consumeAttempt = true)
    {
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE reconcile_sweep_remediation
                SET attempt_count = CASE WHEN $consume = 1 THEN attempt_count ELSE MAX(attempt_count - 1, 0) END,
                    in_flight_owner = NULL,
                    in_flight_at = NULL,
                    last_exit_status = $exit,
                    last_output = $output,
                    updated_at = $at
                WHERE state_key = $key AND in_flight_owner = $owner
                """;
            update.Parameters.AddWithValue("$exit", exitStatus);
            update.Parameters.AddWithValue("$consume", consumeAttempt ? 1 : 0);
            update.Parameters.AddWithValue("$output", output ?? string.Empty);
            update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$key", stateKey);
            update.Parameters.AddWithValue("$owner", owner);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Reconcile sweep attempt '{stateKey}' is not owned by '{owner}'.");
            }
            Commit(connection);
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    public IDisposable? TryAcquireAcceptanceLease(string goalId, string owner, TimeSpan staleAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        if (!TryClaimAcceptanceLease(goalId, owner, staleAfter))
        {
            return null;
        }

        try
        {
            return new AcceptanceLease(this, goalId, owner, staleAfter, _timeProvider);
        }
        catch
        {
            try { ReleaseAcceptanceLease(goalId, owner); }
            catch { }
            throw;
        }
    }

    public string? TryGetAcceptanceLeaseOwner(string goalId)
    {
        string? activeTransactionOwner = null;
        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => activeTransactionOwner = GetAcceptanceLeaseOwner(connection, goalId)))
        {
            return activeTransactionOwner;
        }

        using var connection = Open();
        return GetAcceptanceLeaseOwner(connection, goalId);
    }

    public ReconcileAcceptanceLeaseState? TryGetAcceptanceLease(string goalId, TimeSpan staleAfter)
    {
        ReconcileAcceptanceLeaseState? activeTransactionLease = null;
        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => activeTransactionLease = GetAcceptanceLease(connection, goalId, staleAfter)))
        {
            return activeTransactionLease;
        }

        using var connection = Open();
        return GetAcceptanceLease(connection, goalId, staleAfter);
    }

    private static ReconcileAcceptanceLeaseState? GetAcceptanceLease(
        SqliteConnection connection,
        string goalId,
        TimeSpan staleAfter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner, acquired_at FROM reconcile_acceptance_leases WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var acquiredAtUtc = DateTimeOffset.Parse(
            reader.GetString(1),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return new ReconcileAcceptanceLeaseState(
            reader.GetString(0),
            acquiredAtUtc,
            acquiredAtUtc.Add(staleAfter));
    }

    private static string? GetAcceptanceLeaseOwner(SqliteConnection connection, string goalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner FROM reconcile_acceptance_leases WHERE goal_id = $goal";
        command.Parameters.AddWithValue("$goal", goalId);
        return command.ExecuteScalar() as string;
    }

    public bool TryClaimAcceptanceLease(string goalId, string owner, TimeSpan staleAfter)
    {
        var activeTransactionResult = false;
        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => activeTransactionResult = TryClaimAcceptanceLease(
                    connection,
                    goalId,
                    owner,
                    staleAfter,
                    _timeProvider.GetUtcNow())))
        {
            return activeTransactionResult;
        }

        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            var acquired = TryClaimAcceptanceLease(
                connection,
                goalId,
                owner,
                staleAfter,
                _timeProvider.GetUtcNow());
            Commit(connection);
            return acquired;
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    private static bool TryClaimAcceptanceLease(
        SqliteConnection connection,
        string goalId,
        string owner,
        TimeSpan staleAfter,
        DateTimeOffset now)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM reconcile_acceptance_leases WHERE goal_id = $goal AND acquired_at < $stale";
            delete.Parameters.AddWithValue("$goal", goalId);
            delete.Parameters.AddWithValue("$stale", now.Subtract(staleAfter).ToString("O"));
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO reconcile_acceptance_leases (goal_id, owner, acquired_at) VALUES ($goal, $owner, $at)";
        insert.Parameters.AddWithValue("$goal", goalId);
        insert.Parameters.AddWithValue("$owner", owner);
        insert.Parameters.AddWithValue("$at", now.ToString("O"));
        return insert.ExecuteNonQuery() == 1;
    }

    private bool RenewAcceptanceLease(string goalId, string owner)
    {
        var renewed = false;
        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => renewed = RenewAcceptanceLease(
                    connection,
                    goalId,
                    owner,
                    _timeProvider.GetUtcNow())))
        {
            return renewed;
        }

        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            renewed = RenewAcceptanceLease(
                connection,
                goalId,
                owner,
                _timeProvider.GetUtcNow());
            Commit(connection);
            return renewed;
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    private static bool RenewAcceptanceLease(
        SqliteConnection connection,
        string goalId,
        string owner,
        DateTimeOffset renewedAtUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE reconcile_acceptance_leases SET acquired_at = $at WHERE goal_id = $goal AND owner = $owner";
        command.Parameters.AddWithValue("$at", renewedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$owner", owner);
        return command.ExecuteNonQuery() == 1;
    }

    private void RecordAcceptanceLeaseRenewalFailure(
        string goalId,
        string owner,
        Exception exception,
        string disposition)
    {
        try
        {
            _writeTelemetry.EmitFailure(
                "reconcile-acceptance-lease-renew",
                exception,
                attemptCount: 1,
                disposition: disposition,
                goalId: goalId,
                owner: owner);
        }
        catch
        {
            // Renewal diagnostics cannot crash the protected operation.
        }
    }

    public void ReleaseAcceptanceLease(string goalId, string owner)
    {
        var attemptCount = 1;
        try
        {
            if (StateDbWriteSession.TryExecute(
                    _dbPath,
                    connection => ReleaseAcceptanceLease(connection, goalId, owner)))
            {
                return;
            }

            using var connection = Open();
            attemptCount = BeginAcceptanceLeaseRelease(connection);
            try
            {
                ReleaseAcceptanceLease(connection, goalId, owner);
                Commit(connection);
            }
            catch
            {
                Rollback(connection);
                throw;
            }
        }
        catch (Exception ex)
        {
            attemptCount = ex.Data["Mcg.AttemptCount"] is int recordedAttempts
                ? recordedAttempts
                : attemptCount;
            var disposition = ex is SqliteException sqlite &&
                              SqliteOrchestratorStateRepository.IsTransientLock(sqlite)
                ? "lease-preserved-busy-exhausted"
                : "lease-preserved-non-transient";
            _writeTelemetry.EmitFailure(
                "reconcile-acceptance-lease-release",
                ex,
                attemptCount,
                disposition,
                goalId,
                owner);
            throw;
        }
    }

    private int BeginAcceptanceLeaseRelease(SqliteConnection connection)
    {
        var attemptCount = 0;
        SqliteException? lastTransient = null;
        SqliteOrchestratorStateRepository.WithBusyRetryAsync(
            () =>
            {
                attemptCount++;
                Execute(connection, "BEGIN IMMEDIATE");
                return Task.FromResult(true);
            },
            CancellationToken.None,
            retryBudget: _writeTelemetry.Options.BusyRetryBudget,
            maxBusyRetries: _writeTelemetry.Options.MaxBusyRetries,
            retryDelay: _busyRetryDelay ?? _writeTelemetry.Options.RetryDelay,
            retryObserver: (attempt, elapsed, exception) =>
            {
                lastTransient = exception;
                _writeTelemetry.EmitBusyRetry(
                    "reconcile-acceptance-lease-release",
                    elapsed,
                    exception,
                    attempt);
            }).GetAwaiter().GetResult();
        if (lastTransient is not null)
        {
            _writeTelemetry.EmitBusyRecovered(
                "reconcile-acceptance-lease-release",
                TimeSpan.Zero,
                lastTransient,
                attemptCount);
        }

        return Math.Max(1, attemptCount);
    }

    private static void ReleaseAcceptanceLease(SqliteConnection connection, string goalId, string owner)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reconcile_acceptance_leases WHERE goal_id = $goal AND owner = $owner";
        command.Parameters.AddWithValue("$goal", goalId);
        command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
    }

    private bool TrySetOnce(string stateKey, string column)
    {
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE reconcile_sweep_remediation SET {column} = 1, updated_at = $at WHERE state_key = $key AND {column} = 0";
            update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$key", stateKey);
            var changed = update.ExecuteNonQuery() == 1;
            Commit(connection);
            return changed;
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reconcile_sweep_remediation (
                state_key TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL,
                blocker_kind TEXT NOT NULL,
                evidence TEXT NOT NULL,
                remedy TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                blocker_emitted INTEGER NOT NULL,
                escalation_emitted INTEGER NOT NULL,
                in_flight_owner TEXT NULL,
                in_flight_at TEXT NULL,
                last_exit_status INTEGER NULL,
                last_output TEXT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reconcile_sweep_goal ON reconcile_sweep_remediation(goal_id, blocker_kind);
            CREATE TABLE IF NOT EXISTS reconcile_acceptance_leases (
                goal_id TEXT PRIMARY KEY,
                owner TEXT NOT NULL,
                acquired_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static ReconcileSweepRemediationState ReadState(SqliteConnection connection, string stateKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempt_count, blocker_emitted, escalation_emitted, in_flight_owner, in_flight_at FROM reconcile_sweep_remediation WHERE state_key = $key";
        command.Parameters.AddWithValue("$key", stateKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Reconcile sweep state '{stateKey}' was not found.");
        }
        return new ReconcileSweepRemediationState(
            stateKey,
            reader.GetInt32(0),
            reader.GetInt32(1) != 0,
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
    }

    // Retry only write-lock acquisition. Once BEGIN IMMEDIATE succeeds, each caller executes its
    // side-effecting transaction body exactly once.
    private void BeginImmediate(SqliteConnection connection) =>
        SqliteOrchestratorStateRepository.WithBusyRetryAsync(
            () =>
            {
                Execute(connection, "BEGIN IMMEDIATE");
                return Task.FromResult(true);
            },
            CancellationToken.None,
            retryDelay: _busyRetryDelay).GetAwaiter().GetResult();
    private static void Commit(SqliteConnection connection) => Execute(connection, "COMMIT");
    private static void Rollback(SqliteConnection connection)
    {
        try { Execute(connection, "ROLLBACK"); }
        catch { }
    }
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class AcceptanceLease : IDisposable
    {
        private readonly ReconcileSweepRemediationStore _store;
        private readonly string _goalId;
        private readonly string _owner;
        private readonly ITimer _renewalTimer;
        private int _disposed;
        private int _renewalTerminal;

        public AcceptanceLease(
            ReconcileSweepRemediationStore store,
            string goalId,
            string owner,
            TimeSpan staleAfter,
            TimeProvider timeProvider)
        {
            _store = store;
            _goalId = goalId;
            _owner = owner;
            var interval = RenewalInterval(staleAfter);
            _renewalTimer = timeProvider.CreateTimer(
                static state => ((AcceptanceLease)state!).Renew(),
                this,
                interval,
                interval);
        }

        private static TimeSpan RenewalInterval(TimeSpan staleAfter) =>
            TimeSpan.FromTicks(Math.Max(1, staleAfter.Ticks / 4));

        private void Renew()
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _renewalTerminal) != 0)
            {
                return;
            }

            try
            {
                if (!_store.RenewAcceptanceLease(_goalId, _owner) &&
                    Interlocked.Exchange(ref _renewalTerminal, 1) == 0)
                {
                    _store.RecordAcceptanceLeaseRenewalFailure(
                        _goalId,
                        _owner,
                        new InvalidOperationException("The owner-qualified acceptance lease row no longer exists."),
                        "lease-lost-owner-mismatch");
                }
            }
            catch (Exception ex)
            {
                _store.RecordAcceptanceLeaseRenewalFailure(
                    _goalId,
                    _owner,
                    ex,
                    "lease-preserved-renewal-failed");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _renewalTimer.Dispose();
            try
            {
                _store.ReleaseAcceptanceLease(_goalId, _owner);
            }
            catch
            {
                // Lease cleanup is best-effort after the protected operation has reached a verdict.
                // The owner-qualified row remains available for stale-expiry recovery, and the store
                // has already emitted typed failure evidence.
            }
        }
    }
}
