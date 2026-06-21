using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record BacklogItem(
    string Id,
    string Title,
    string Body,
    BacklogItemStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SourceGoalId);

public enum BacklogItemStatus { Open, Done }

public sealed class BacklogStore
{
    private readonly string _dbPath;

    public BacklogStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;";

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
            CREATE TABLE IF NOT EXISTS backlog (
                id             TEXT PRIMARY KEY,
                title          TEXT NOT NULL,
                body           TEXT NOT NULL,
                status         TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                updated_at     TEXT NOT NULL,
                source_goal_id TEXT
            )
            """);
    }

    public async Task<BacklogItem> AddAsync(
        string title,
        string body = "",
        string? sourceGoalId = null,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var item = new BacklogItem(id, title, body, BacklogItemStatus.Open, now, now, sourceGoalId);
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

    public async Task<IReadOnlyList<BacklogItem>> ListAsync(
        bool includeAll = false,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<BacklogItem>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeAll
            ? "SELECT id, title, body, status, created_at, updated_at, source_goal_id FROM backlog ORDER BY created_at ASC"
            : "SELECT id, title, body, status, created_at, updated_at, source_goal_id FROM backlog WHERE status = 'Open' ORDER BY created_at ASC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<BacklogItem?> GetByIdPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, body, status, created_at, updated_at, source_goal_id FROM backlog WHERE id LIKE $prefix ORDER BY created_at ASC LIMIT 2";
        cmd.Parameters.AddWithValue("$prefix", prefix + "%");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        BacklogItem? first = null;
        if (await reader.ReadAsync(cancellationToken))
            first = ReadItem(reader);
        if (first is null)
            return null;
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"Ambiguous id prefix '{prefix}' matches multiple items.");
        return first;
    }

    public async Task<BacklogItem> CloseAsync(
        string id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            await using (var cmd = conn.CreateCommand())
            {
                if (reason is null)
                {
                    cmd.CommandText = "UPDATE backlog SET status = 'Done', updated_at = $updated_at WHERE id = $id";
                }
                else
                {
                    cmd.CommandText = """
                        UPDATE backlog
                        SET status = 'Done',
                            updated_at = $updated_at,
                            body = CASE WHEN body = '' THEN $reason
                                        ELSE body || char(10) || char(10) || 'Closed: ' || $reason
                                   END
                        WHERE id = $id
                        """;
                    cmd.Parameters.AddWithValue("$reason", reason);
                }
                cmd.Parameters.AddWithValue("$updated_at", updatedAt);
                cmd.Parameters.AddWithValue("$id", id);
                var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rows == 0)
                    throw new InvalidOperationException($"No backlog item found with id '{id}'.");
            }
            var result = await LoadItemByIdAsync(conn, id, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return result!;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<BacklogItem> ReopenAsync(
        string id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            await using (var cmd = conn.CreateCommand())
            {
                if (reason is null)
                {
                    cmd.CommandText = "UPDATE backlog SET status = 'Open', updated_at = $updated_at WHERE id = $id";
                }
                else
                {
                    cmd.CommandText = """
                        UPDATE backlog
                        SET status = 'Open',
                            updated_at = $updated_at,
                            body = CASE WHEN body = '' THEN $reason
                                        ELSE body || char(10) || char(10) || 'Reopened: ' || $reason
                                   END
                        WHERE id = $id
                        """;
                    cmd.Parameters.AddWithValue("$reason", reason);
                }
                cmd.Parameters.AddWithValue("$updated_at", updatedAt);
                cmd.Parameters.AddWithValue("$id", id);
                var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
                if (rows == 0)
                    throw new InvalidOperationException($"No backlog item found with id '{id}'.");
            }
            var result = await LoadItemByIdAsync(conn, id, cancellationToken);
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            return result!;
        }
        catch
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            throw;
        }
    }

    public async Task<BacklogItem?> GetByExactIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        return await LoadItemByIdAsync(conn, id, cancellationToken);
    }

    // Closes the item if Open; no-op if already Done or absent; never throws.
    public async Task<bool> TryCloseByIdAsync(
        string id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");
            await using var cmd = conn.CreateCommand();
            if (reason is null)
            {
                cmd.CommandText = "UPDATE backlog SET status = 'Done', updated_at = $updated_at WHERE id = $id AND status = 'Open'";
            }
            else
            {
                cmd.CommandText = """
                    UPDATE backlog
                    SET status = 'Done',
                        updated_at = $updated_at,
                        body = CASE WHEN body = '' THEN $reason
                                    ELSE body || char(10) || char(10) || 'Closed: ' || $reason
                               END
                    WHERE id = $id AND status = 'Open'
                    """;
                cmd.Parameters.AddWithValue("$reason", reason);
            }
            cmd.Parameters.AddWithValue("$updated_at", updatedAt);
            cmd.Parameters.AddWithValue("$id", id);
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0;
        }
        catch
        {
            return false;
        }
    }

    // Returns true if inserted, false if the item already existed (idempotent for import).
    public async Task<bool> UpsertAsync(BacklogItem item, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
        await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id)
                VALUES ($id, $title, $body, $status, $created_at, $updated_at, $source_goal_id)
                """;
            SetItemParameters(cmd, item);
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

    // Derives a deterministic slug-style id from a title (used for idempotent upserts).
    public static string SlugId(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 60 ? slug[..60] : slug;
    }

    private static BacklogItem ReadItem(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<BacklogItemStatus>(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));

    private static async Task<BacklogItem?> LoadItemByIdAsync(SqliteConnection conn, string id, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, body, status, created_at, updated_at, source_goal_id FROM backlog WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    private static async Task InsertItemAsync(SqliteConnection conn, BacklogItem item, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id)
            VALUES ($id, $title, $body, $status, $created_at, $updated_at, $source_goal_id)
            """;
        SetItemParameters(cmd, item);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void SetItemParameters(SqliteCommand cmd, BacklogItem item)
    {
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$body", item.Body);
        cmd.Parameters.AddWithValue("$status", item.Status.ToString());
        cmd.Parameters.AddWithValue("$created_at", item.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated_at", item.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$source_goal_id", (object?)item.SourceGoalId ?? DBNull.Value);
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
