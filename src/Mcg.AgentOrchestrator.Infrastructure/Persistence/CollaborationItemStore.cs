using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface ICollaborationItemStore
{
    Task<CollaborationItem> RaiseAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey = null,
        CancellationToken cancellationToken = default);

    Task<bool> TryResolveAsync(
        string correlationKey,
        string resolution,
        CancellationToken cancellationToken = default);

    Task<bool> TryMarkDeliveredAsync(
        string correlationKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationItem>> GetAttentionQueueAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationItem>> ListAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default);
}

public sealed class CollaborationItemStore : ICollaborationItemStore
{
    private readonly string _dbPath;

    public CollaborationItemStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    public static CollaborationItemStore ForDirectory(string directory) =>
        new(Path.Combine(directory, "collaboration-items.db"));

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
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
            CREATE TABLE IF NOT EXISTS collaboration_items (
                id              TEXT PRIMARY KEY,
                type            TEXT NOT NULL,
                goal_id         TEXT,
                status          TEXT NOT NULL,
                subject         TEXT NOT NULL,
                body            TEXT NOT NULL,
                correlation_key TEXT,
                raised_at       TEXT NOT NULL,
                resolved_at     TEXT,
                resolution      TEXT
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_items_correlation
                ON collaboration_items (correlation_key)
            """);
    }

    public async Task<CollaborationItem> RaiseAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("n");
        var raisedAt = DateTimeOffset.UtcNow;
        var item = new CollaborationItem(
            id, type, goalId, CollaborationItemStatus.Raised,
            subject, body, correlationKey, raisedAt, null, null);

        await using var conn = OpenConnection();
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            await InsertItemAsync(conn, item, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
        return item;
    }

    public async Task<bool> TryResolveAsync(
        string correlationKey,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            var resolvedAt = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = conn.CreateCommand();
            // Idempotent: only update if currently in a non-terminal state.
            cmd.CommandText = """
                UPDATE collaboration_items
                SET status = 'Resolved', resolved_at = $resolved_at, resolution = $resolution
                WHERE correlation_key = $key
                  AND status NOT IN ('Resolved', 'Closed')
                """;
            cmd.Parameters.AddWithValue("$resolved_at", resolvedAt);
            cmd.Parameters.AddWithValue("$resolution", resolution);
            cmd.Parameters.AddWithValue("$key", correlationKey);
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return rows > 0;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<bool> TryMarkDeliveredAsync(
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE collaboration_items
                SET status = 'Delivered'
                WHERE correlation_key = $key
                  AND status = 'Raised'
                """;
            cmd.Parameters.AddWithValue("$key", correlationKey);
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return rows > 0;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<CollaborationItem>> GetAttentionQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(null, cancellationToken);
        return CollaborationItemLifecycle.BuildAttentionQueue(all);
    }

    public async Task<IReadOnlyList<CollaborationItem>> ListAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<CollaborationItem>();
        await using var cmd = conn.CreateCommand();
        if (goalId is null)
        {
            cmd.CommandText = "SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution FROM collaboration_items ORDER BY raised_at ASC";
        }
        else
        {
            cmd.CommandText = "SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution FROM collaboration_items WHERE goal_id = $goal_id ORDER BY raised_at ASC";
            cmd.Parameters.AddWithValue("$goal_id", goalId);
        }
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadItem(reader));
        return results;
    }

    private static CollaborationItem ReadItem(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            Enum.Parse<CollaborationItemType>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<CollaborationItemStatus>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static async Task InsertItemAsync(SqliteConnection conn, CollaborationItem item, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collaboration_items (id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution)
            VALUES ($id, $type, $goal_id, $status, $subject, $body, $correlation_key, $raised_at, $resolved_at, $resolution)
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$type", item.Type.ToString());
        cmd.Parameters.AddWithValue("$goal_id", (object?)item.GoalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", item.Status.ToString());
        cmd.Parameters.AddWithValue("$subject", item.Subject);
        cmd.Parameters.AddWithValue("$body", item.Body);
        cmd.Parameters.AddWithValue("$correlation_key", (object?)item.CorrelationKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$raised_at", item.RaisedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$resolved_at", (object?)item.ResolvedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resolution", (object?)item.Resolution ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

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
}
