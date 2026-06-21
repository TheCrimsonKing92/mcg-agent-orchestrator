using Microsoft.Data.Sqlite;
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

public sealed class SqliteRunEventStore : IRunEventStore
{
    private const int MaxBusyRetries = 6;
    private readonly string _dbPath;

    public SqliteRunEventStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
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

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
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
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
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
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool IsTransientLock(SqliteException ex) =>
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

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
}
