using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum BacklogIntakeReservationKind
{
    Acquired,
    ExistingGoal,
    InProgress
}

public sealed record BacklogIntakeRecord(
    string SourceBacklogItemId,
    string Heading,
    string Status,
    string? GoalId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    int? OwnerProcessId,
    string? StdoutPath,
    string? StderrPath);

public sealed record BacklogIntakeReservation(
    BacklogIntakeReservationKind Kind,
    BacklogIntakeRecord Record);

public sealed class BacklogIntakeRecordStore
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(30);

    private readonly string _dbPath;

    // The connection factory already waits for the full state-store budget. Retrying here would
    // multiply that wait for the same write.
    private const int MaxBusyRetries = 1;

    public BacklogIntakeRecordStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Value cannot be empty.", nameof(dbPath));

        _dbPath = dbPath;
    }

    public BacklogIntakeReservation Reserve(
        string sourceBacklogItemId,
        string heading,
        bool forceReclaim = false,
        TimeSpan? staleAfter = null)
    {
        if (string.IsNullOrWhiteSpace(sourceBacklogItemId))
            throw new ArgumentException("Value cannot be empty.", nameof(sourceBacklogItemId));

        var now = DateTimeOffset.UtcNow;
        var stdoutPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH");
        var stderrPath = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH");
        using var conn = BeginWrite();
        try
        {
            var existing = LoadRecord(conn, sourceBacklogItemId);
            if (existing is null)
            {
                var inserted = new BacklogIntakeRecord(
                    sourceBacklogItemId,
                    heading,
                    "InProgress",
                    GoalId: null,
                    StartedAt: now,
                    LastHeartbeatAt: now,
                    OwnerProcessId: Environment.ProcessId,
                    StdoutPath: NormalizeLogPath(stdoutPath),
                    StderrPath: NormalizeLogPath(stderrPath));
                InsertRecord(conn, inserted);
                RunNonQuery(conn, "COMMIT");
                return new BacklogIntakeReservation(BacklogIntakeReservationKind.Acquired, inserted);
            }

            if (!string.IsNullOrWhiteSpace(existing.GoalId))
            {
                RunNonQuery(conn, "COMMIT");
                return new BacklogIntakeReservation(BacklogIntakeReservationKind.ExistingGoal, existing);
            }

            if (forceReclaim || IsStale(existing, now, staleAfter ?? DefaultStaleAfter))
            {
                var reclaimed = existing with
                {
                    Heading = string.IsNullOrWhiteSpace(heading) ? existing.Heading : heading,
                    Status = "InProgress",
                    StartedAt = now,
                    LastHeartbeatAt = now,
                    OwnerProcessId = Environment.ProcessId,
                    StdoutPath = NormalizeLogPath(stdoutPath) ?? existing.StdoutPath,
                    StderrPath = NormalizeLogPath(stderrPath) ?? existing.StderrPath
                };
                UpdateRecord(conn, reclaimed);
                RunNonQuery(conn, "COMMIT");
                return new BacklogIntakeReservation(BacklogIntakeReservationKind.Acquired, reclaimed);
            }

            RunNonQuery(conn, "COMMIT");
            return new BacklogIntakeReservation(BacklogIntakeReservationKind.InProgress, existing);
        }
        catch
        {
            try { RunNonQuery(conn, "ROLLBACK"); } catch { }
            throw;
        }
    }

    public void MarkGoalCreated(string sourceBacklogItemId, string goalId)
    {
        if (string.IsNullOrWhiteSpace(sourceBacklogItemId))
            throw new ArgumentException("Value cannot be empty.", nameof(sourceBacklogItemId));
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Value cannot be empty.", nameof(goalId));

        if (StateDbWriteSession.TryExecute(
                _dbPath,
                connection => MarkGoalCreated(connection, sourceBacklogItemId, goalId)))
        {
            return;
        }

        WithBusyRetry(() =>
        {
            using var conn = OpenConnection();
            MarkGoalCreated(conn, sourceBacklogItemId, goalId);
            return true;
        });
    }

    private static void MarkGoalCreated(
        SqliteConnection connection,
        string sourceBacklogItemId,
        string goalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backlog_intake_records
            SET goal_id = $goal_id,
                status = 'GoalCreated',
                last_heartbeat_at = $last_heartbeat_at
            WHERE source_backlog_item_id = $source_backlog_item_id
              AND goal_id IS NULL
            """;
        command.Parameters.AddWithValue("$goal_id", goalId);
        command.Parameters.AddWithValue("$last_heartbeat_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source_backlog_item_id", sourceBacklogItemId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"BACKLOG_INTAKE_INVALID_TRANSITION source={sourceBacklogItemId} goal={goalId}");
        }
    }

    public BacklogIntakeRecord? Get(string sourceBacklogItemId)
    {
        if (string.IsNullOrWhiteSpace(sourceBacklogItemId))
            return null;

        if (!File.Exists(_dbPath))
            return null;

        try
        {
            using var conn = OpenReadConnection();
            return LoadRecord(conn, sourceBacklogItemId);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return null;
        }
    }

    private SqliteConnection OpenConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.ReadWrite);

    private SqliteConnection OpenReadConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.QueryOnlyRead);

    private SqliteConnection BeginWrite()
    {
        return WithBusyRetry(() =>
        {
            var conn = OpenConnection();
            try
            {
                RunNonQuery(conn, "BEGIN IMMEDIATE");
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        });
    }

    private static T WithBusyRetry<T>(Func<T> operation)
    {
        var delayMs = 50;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (SqliteException ex) when (attempt < MaxBusyRetries && IsTransientLock(ex))
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
        }
    }

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;

    private static BacklogIntakeRecord? LoadRecord(SqliteConnection conn, string sourceBacklogItemId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_backlog_item_id, heading, status, goal_id, started_at, last_heartbeat_at,
                   owner_process_id, stdout_path, stderr_path
            FROM backlog_intake_records
            WHERE source_backlog_item_id = $source_backlog_item_id
            """;
        cmd.Parameters.AddWithValue("$source_backlog_item_id", sourceBacklogItemId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadRecord(reader);
    }

    private static void InsertRecord(SqliteConnection conn, BacklogIntakeRecord record)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backlog_intake_records (
                source_backlog_item_id, heading, status, goal_id, started_at, last_heartbeat_at,
                owner_process_id, stdout_path, stderr_path)
            VALUES (
                $source_backlog_item_id, $heading, $status, $goal_id, $started_at, $last_heartbeat_at,
                $owner_process_id, $stdout_path, $stderr_path)
            """;
        AddRecordParameters(cmd, record);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateRecord(SqliteConnection conn, BacklogIntakeRecord record)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE backlog_intake_records
            SET heading = $heading,
                status = $status,
                goal_id = $goal_id,
                started_at = $started_at,
                last_heartbeat_at = $last_heartbeat_at,
                owner_process_id = $owner_process_id,
                stdout_path = $stdout_path,
                stderr_path = $stderr_path
            WHERE source_backlog_item_id = $source_backlog_item_id
            """;
        AddRecordParameters(cmd, record);
        cmd.ExecuteNonQuery();
    }

    private static void AddRecordParameters(SqliteCommand cmd, BacklogIntakeRecord record)
    {
        cmd.Parameters.AddWithValue("$source_backlog_item_id", record.SourceBacklogItemId);
        cmd.Parameters.AddWithValue("$heading", record.Heading);
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$goal_id", (object?)record.GoalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started_at", record.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_heartbeat_at", record.LastHeartbeatAt.ToString("O"));
        cmd.Parameters.AddWithValue("$owner_process_id", (object?)record.OwnerProcessId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stdout_path", (object?)record.StdoutPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stderr_path", (object?)record.StderrPath ?? DBNull.Value);
    }

    private static BacklogIntakeRecord ReadRecord(SqliteDataReader reader)
    {
        return new BacklogIntakeRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static bool IsStale(BacklogIntakeRecord record, DateTimeOffset now, TimeSpan staleAfter)
    {
        if (now - record.LastHeartbeatAt >= staleAfter)
            return true;

        return record.OwnerProcessId is int pid && !IsProcessAlive(pid);
    }

    private static bool IsProcessAlive(int pid)
        => IsProcessAlive(pid, ReadHasExited);

    internal static bool IsProcessAlive(int pid, Func<int, bool> readHasExited)
    {
        try
        {
            return !readHasExited(pid);
        }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception))
        {
            return false;
        }
    }

    private static bool ReadHasExited(int pid)
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        return process.HasExited;
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? NormalizeLogPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Path.GetFullPath(path);
    }
}
