using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ReconcileSweepRemediationState(
    string StateKey,
    int AttemptCount,
    bool BlockerEmitted,
    bool EscalationEmitted,
    string? InFlightOwner,
    DateTimeOffset? InFlightAt);

public sealed record ReconcileSweepAttemptClaim(bool Claimed, int AttemptNumber, string? Owner);

public interface IReconcileSweepRemediationStore
{
    ReconcileSweepRemediationState Observe(string stateKey, string goalId, string blockerKind, string evidence, string remedy);
    bool TryMarkBlockerEmitted(string stateKey);
    ReconcileSweepAttemptClaim TryClaimAttempt(string stateKey, int maximumAttempts, string owner);
    void CompleteAttempt(string stateKey, string owner, int exitStatus, string output, bool consumeAttempt = true);
    bool TryMarkEscalationEmitted(string stateKey);
    bool TryClaimAcceptanceLease(string goalId, string owner, TimeSpan staleAfter);
    IDisposable? TryAcquireAcceptanceLease(string goalId, string owner, TimeSpan staleAfter);
    void ReleaseAcceptanceLease(string goalId, string owner);
}

public sealed class ReconcileSweepRemediationStore : IReconcileSweepRemediationStore
{
    private readonly string _dbPath;

    public ReconcileSweepRemediationStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
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
        return TryClaimAcceptanceLease(goalId, owner, staleAfter)
            ? new AcceptanceLease(_dbPath, goalId, owner)
            : null;
    }

    public bool TryClaimAcceptanceLease(string goalId, string owner, TimeSpan staleAfter)
    {
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            var now = DateTimeOffset.UtcNow;
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
            var acquired = insert.ExecuteNonQuery() == 1;
            Commit(connection);
            return acquired;
        }
        catch
        {
            Rollback(connection);
            throw;
        }
    }

    public void ReleaseAcceptanceLease(string goalId, string owner)
        => ReleaseAcceptanceLease(_dbPath, goalId, owner);

    private static void ReleaseAcceptanceLease(string dbPath, string goalId, string owner)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
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
            DefaultTimeout = 5
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

    private static void BeginImmediate(SqliteConnection connection) => Execute(connection, "BEGIN IMMEDIATE");
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

    private sealed class AcceptanceLease(string dbPath, string goalId, string owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            ReleaseAcceptanceLease(dbPath, goalId, owner);
        }
    }
}
