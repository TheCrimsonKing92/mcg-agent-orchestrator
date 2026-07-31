using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record BacklogItem(
    string Id,
    string Title,
    string Body,
    BacklogItemStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SourceGoalId,
    string? Priority = null,
    string Tags = "",
    string? SupersededBy = null,
    DateTimeOffset? SupersededAt = null)
{
    public IReadOnlyList<BacklogNote> Notes { get; init; } = [];
    public IReadOnlyList<BacklogLink> Links { get; init; } = [];
    public IReadOnlyList<BacklogDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<BacklogDependency> Dependents { get; init; } = [];
}

public sealed record BacklogNote(
    DateTimeOffset CreatedAt,
    string Text);

public sealed record BacklogLink(
    string Item1Id,
    string Item2Id,
    BacklogLinkKind Kind,
    string? CanonicalId,
    DateTimeOffset CreatedAt)
{
    public string OtherId(string id) =>
        string.Equals(Item1Id, id, StringComparison.Ordinal) ? Item2Id : Item1Id;

    public string? DuplicateId =>
        Kind == BacklogLinkKind.Duplicate && CanonicalId is not null
            ? string.Equals(Item1Id, CanonicalId, StringComparison.Ordinal) ? Item2Id : Item1Id
            : null;
}

public sealed record BacklogDependency(
    string DependentId,
    string PrerequisiteId,
    BacklogDependencyTargetKind TargetKind,
    DateTimeOffset CreatedAt);

public sealed record BacklogDependencyTarget(
    string Id,
    BacklogDependencyTargetKind Kind);

public enum BacklogItemStatus { Open, Done, Superseded }

public enum BacklogLinkKind { Duplicate, Related }

public enum BacklogDependencyTargetKind { Backlog, Goal }

public enum BacklogCloseDisposition { Closed, AlreadyDone, NotFound }

public sealed record BacklogCloseResult(BacklogCloseDisposition Disposition, BacklogItem? Item)
{
    public bool Closed => Disposition == BacklogCloseDisposition.Closed;
}

public sealed record BacklogItemUpdate(
    string? Title = null,
    string? Body = null,
    string? Priority = null,
    string? Tags = null,
    BacklogItemStatus? Status = null);

public sealed record BacklogUnsupersedeResult(bool Changed, BacklogItem Item);

public sealed class BacklogStore
{
    private readonly string _dbPath;

    public BacklogStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    // Pooling=False matches the loop critical-path stores: a POOLED connection can be returned to the
    // pool still holding a WAL read/lock slot, so a later writer meets "database is locked" that
    // busy_timeout cannot wait out. Without it a backlog write concurrent with a reader could fail.
    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    // Bounded retry on a transient SQLITE_BUSY/LOCKED: busy_timeout (30s) handles the simple lock-wait,
    // but the deadlock-avoidance path can still surface an immediate BUSY; this turns that into a brief
    // wait instead of a fatal throw. Mirrors SqliteOrchestratorStateRepository.
    private const int MaxBusyRetries = 6;
    private const string SelectColumns = "id, title, body, status, created_at, updated_at, source_goal_id, priority, tags, superseded_by, superseded_at";

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
        AddColumnIfMissing(conn, "backlog", "priority", "TEXT");
        AddColumnIfMissing(conn, "backlog", "tags", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "backlog", "superseded_by", "TEXT");
        AddColumnIfMissing(conn, "backlog", "superseded_at", "TEXT");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS backlog_notes (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                backlog_item_id TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                text            TEXT NOT NULL,
                FOREIGN KEY(backlog_item_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_notes_item_created ON backlog_notes(backlog_item_id, created_at, id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS backlog_links (
                item1_id     TEXT NOT NULL,
                item2_id     TEXT NOT NULL,
                kind         TEXT NOT NULL,
                canonical_id TEXT,
                created_at   TEXT NOT NULL,
                PRIMARY KEY(item1_id, item2_id),
                FOREIGN KEY(item1_id) REFERENCES backlog(id) ON DELETE CASCADE,
                FOREIGN KEY(item2_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_links_item2 ON backlog_links(item2_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS backlog_dependencies (
                sequence          INTEGER PRIMARY KEY AUTOINCREMENT,
                dependent_id      TEXT NOT NULL,
                prerequisite_id   TEXT NOT NULL,
                prerequisite_kind TEXT NOT NULL,
                created_at        TEXT NOT NULL,
                UNIQUE(dependent_id, prerequisite_id),
                FOREIGN KEY(dependent_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_dependencies_prerequisite ON backlog_dependencies(prerequisite_id, prerequisite_kind)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS backlog_history (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                backlog_item_id TEXT NOT NULL,
                action          TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                details         TEXT NOT NULL,
                FOREIGN KEY(backlog_item_id) REFERENCES backlog(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_history_item_created ON backlog_history(backlog_item_id, created_at, id)");
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
        return await WithBusyRetryAsync(async () =>
        {
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
        }, cancellationToken);
    }

    public async Task<BacklogItem> AddWithDependenciesAsync(
        string title,
        string body,
        IReadOnlyList<BacklogDependencyTarget> dependencies,
        string? sourceGoalId = null,
        CancellationToken cancellationToken = default,
        Func<string, bool>? goalExists = null)
    {
        var distinctDependencies = dependencies
            .DistinctBy(dependency => dependency.Id, StringComparer.Ordinal)
            .ToArray();
        var id = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var item = new BacklogItem(id, title, body, BacklogItemStatus.Open, now, now, sourceGoalId);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                await InsertItemAsync(conn, item, cancellationToken);
                foreach (var dependency in distinctDependencies)
                {
                    await ValidateAndInsertDependencyAsync(
                        conn,
                        id,
                        dependency,
                        allowExisting: true,
                        goalExists,
                        cancellationToken);
                }
                var result = (await LoadItemByIdAsync(conn, id, cancellationToken))!;
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return result;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<BacklogItem>> ListAsync(
        bool includeAll = false,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<BacklogItem>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = includeAll
            ? $"SELECT {SelectColumns} FROM backlog ORDER BY created_at ASC"
            : $"""
                SELECT {SelectColumns}
                FROM backlog
                WHERE status = 'Open'
                  AND superseded_by IS NULL
                  AND id NOT IN (
                      SELECT CASE WHEN canonical_id = item1_id THEN item2_id ELSE item1_id END
                      FROM backlog_links
                      WHERE kind = 'Duplicate' AND canonical_id IS NOT NULL
                  )
                ORDER BY created_at ASC
                """;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadItem(reader));
        }
        return await AttachRelationshipsAsync(conn, results, cancellationToken);
    }

    public async Task<BacklogItem?> GetByIdPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        BacklogItem? first = null;
        var ambiguous = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {SelectColumns} FROM backlog WHERE id LIKE $prefix ORDER BY created_at ASC LIMIT 2";
            cmd.Parameters.AddWithValue("$prefix", prefix + "%");
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                first = ReadItem(reader);
            ambiguous = first is not null && await reader.ReadAsync(cancellationToken);
        }

        if (first is null)
            return null;
        if (ambiguous)
            throw new InvalidOperationException($"Ambiguous id prefix '{prefix}' matches multiple items.");

        var notes = await LoadNotesAsync(conn, first.Id, cancellationToken);
        var links = await LoadLinksForItemAsync(conn, first.Id, cancellationToken);
        var dependencies = await LoadDependenciesAsync(conn, first.Id, asDependent: true, cancellationToken);
        var dependents = await LoadDependenciesAsync(conn, first.Id, asDependent: false, cancellationToken);
        return first with { Notes = notes, Links = links, Dependencies = dependencies, Dependents = dependents };
    }

    public async Task<IReadOnlyList<BacklogItem>> FindByIdPrefixAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<BacklogItem>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM backlog WHERE id LIKE $prefix ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("$prefix", prefix + "%");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<BacklogDependency> AddDependencyAsync(
        string dependentId,
        BacklogDependencyTarget prerequisite,
        CancellationToken cancellationToken = default,
        Func<string, bool>? goalExists = null)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var dependency = await ValidateAndInsertDependencyAsync(
                    conn,
                    dependentId,
                    prerequisite,
                    allowExisting: true,
                    goalExists,
                    cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return dependency;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task RemoveDependencyAsync(
        string dependentId,
        string prerequisiteId,
        CancellationToken cancellationToken = default)
    {
        await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM backlog_dependencies
                    WHERE dependent_id = $dependent_id AND prerequisite_id = $prerequisite_id
                    """;
                cmd.Parameters.AddWithValue("$dependent_id", dependentId);
                cmd.Parameters.AddWithValue("$prerequisite_id", prerequisiteId);
                if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
                    throw new InvalidOperationException(
                        $"Backlog item '{ShortId(dependentId)}' does not depend on '{ShortId(prerequisiteId)}'.");
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return true;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task ClearDependenciesAsync(
        string dependentId,
        CancellationToken cancellationToken = default)
    {
        await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                if (await LoadItemByIdAsync(conn, dependentId, cancellationToken, includeNotes: false) is null)
                    throw new InvalidOperationException($"No backlog item found with id '{dependentId}'.");
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM backlog_dependencies WHERE dependent_id = $dependent_id";
                cmd.Parameters.AddWithValue("$dependent_id", dependentId);
                if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
                    throw new InvalidOperationException(
                        $"Backlog item '{ShortId(dependentId)}' has no dependencies to clear.");
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return true;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<BacklogItem> CloseAsync(
        string id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
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
        }, cancellationToken);
    }

    public async Task<BacklogItem> ReopenAsync(
        string id,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
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
        }, cancellationToken);
    }

    public async Task<BacklogItem> AppendNoteAsync(
        string id,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Backlog note text cannot be empty.", nameof(text));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var item = await LoadItemByIdAsync(conn, id, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{id}'.");
                var createdAt = DateTimeOffset.UtcNow.ToString("O");
                await using (var insert = conn.CreateCommand())
                {
                    insert.CommandText = """
                        INSERT INTO backlog_notes (backlog_item_id, created_at, text)
                        VALUES ($backlog_item_id, $created_at, $text)
                        """;
                    insert.Parameters.AddWithValue("$backlog_item_id", id);
                    insert.Parameters.AddWithValue("$created_at", createdAt);
                    insert.Parameters.AddWithValue("$text", text);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var update = conn.CreateCommand())
                {
                    update.CommandText = "UPDATE backlog SET updated_at = $updated_at WHERE id = $id";
                    update.Parameters.AddWithValue("$updated_at", createdAt);
                    update.Parameters.AddWithValue("$id", id);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                var result = (await LoadItemByIdAsync(conn, item.Id, cancellationToken))!;
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return result;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<BacklogItem> UpdateAsync(
        string id,
        BacklogItemUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (update == new BacklogItemUpdate())
            throw new ArgumentException("At least one backlog field must be provided.", nameof(update));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await LoadItemByIdAsync(conn, id, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{id}'.");
                var updatedAt = DateTimeOffset.UtcNow.ToString("O");
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        UPDATE backlog
                        SET title = $title,
                            body = $body,
                            priority = $priority,
                            tags = $tags,
                            status = $status,
                            updated_at = $updated_at
                        WHERE id = $id
                        """;
                    cmd.Parameters.AddWithValue("$title", update.Title ?? existing.Title);
                    cmd.Parameters.AddWithValue("$body", update.Body ?? existing.Body);
                    cmd.Parameters.AddWithValue("$priority", (object?)(update.Priority ?? existing.Priority) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$tags", update.Tags ?? existing.Tags);
                    cmd.Parameters.AddWithValue("$status", (update.Status ?? existing.Status).ToString());
                    cmd.Parameters.AddWithValue("$updated_at", updatedAt);
                    cmd.Parameters.AddWithValue("$id", id);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                var result = (await LoadItemByIdAsync(conn, id, cancellationToken))!;
                await InsertHistoryAsync(conn, id, "update", updatedAt, new
                {
                    before = ToHistorySnapshot(existing),
                    after = ToHistorySnapshot(result)
                }, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return result;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<BacklogItem> SupersedeAsync(
        string oldId,
        string newId,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(oldId, newId, StringComparison.Ordinal))
            throw new InvalidOperationException("A backlog item cannot supersede itself.");

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var oldItem = await LoadItemByIdAsync(conn, oldId, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{oldId}'.");
                var newItem = await LoadItemByIdAsync(conn, newId, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{newId}'.");
                if (await WouldCreateSupersedeCycleAsync(conn, oldItem.Id, newItem.Id, cancellationToken))
                    throw new InvalidOperationException("Supersede relationship would create a cycle.");

                var now = DateTimeOffset.UtcNow.ToString("O");
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        UPDATE backlog
                        SET status = 'Superseded',
                            superseded_by = $superseded_by,
                            superseded_at = $superseded_at,
                            updated_at = $updated_at
                        WHERE id = $id
                        """;
                    cmd.Parameters.AddWithValue("$superseded_by", newItem.Id);
                    cmd.Parameters.AddWithValue("$superseded_at", now);
                    cmd.Parameters.AddWithValue("$updated_at", now);
                    cmd.Parameters.AddWithValue("$id", oldItem.Id);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                var result = (await LoadItemByIdAsync(conn, oldItem.Id, cancellationToken))!;
                await InsertHistoryAsync(conn, oldItem.Id, "supersede", now, new { supersededBy = newItem.Id }, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return result;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<BacklogUnsupersedeResult> UnsupersedeAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await LoadItemByIdAsync(conn, id, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{id}'.");
                if (existing.SupersededBy is null && existing.Status != BacklogItemStatus.Superseded)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new BacklogUnsupersedeResult(false, existing);
                }

                var now = DateTimeOffset.UtcNow.ToString("O");
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        UPDATE backlog
                        SET status = 'Open',
                            superseded_by = NULL,
                            superseded_at = NULL,
                            updated_at = $updated_at
                        WHERE id = $id
                        """;
                    cmd.Parameters.AddWithValue("$updated_at", now);
                    cmd.Parameters.AddWithValue("$id", id);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                var result = (await LoadItemByIdAsync(conn, id, cancellationToken))!;
                await InsertHistoryAsync(conn, id, "unsupersede", now, new { previousSupersededBy = existing.SupersededBy }, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return new BacklogUnsupersedeResult(true, result);
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<BacklogLink> LinkAsync(
        string id1,
        string id2,
        BacklogLinkKind kind = BacklogLinkKind.Duplicate,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(id1, id2, StringComparison.Ordinal))
            throw new InvalidOperationException("A backlog item cannot be linked to itself.");

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var first = await LoadItemByIdAsync(conn, id1, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{id1}'.");
                var second = await LoadItemByIdAsync(conn, id2, cancellationToken, includeNotes: false)
                    ?? throw new InvalidOperationException($"No backlog item found with id '{id2}'.");
                var (item1, item2) = NormalizeLinkIds(first.Id, second.Id);
                var now = DateTimeOffset.UtcNow.ToString("O");
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT OR IGNORE INTO backlog_links (item1_id, item2_id, kind, canonical_id, created_at)
                        VALUES ($item1_id, $item2_id, $kind, $canonical_id, $created_at)
                        """;
                    cmd.Parameters.AddWithValue("$item1_id", item1);
                    cmd.Parameters.AddWithValue("$item2_id", item2);
                    cmd.Parameters.AddWithValue("$kind", kind.ToString());
                    cmd.Parameters.AddWithValue("$canonical_id", kind == BacklogLinkKind.Duplicate ? first.Id : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$created_at", now);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await InsertHistoryAsync(conn, first.Id, kind == BacklogLinkKind.Duplicate ? "link-duplicate" : "link-related", now, new { linkedId = second.Id }, cancellationToken);
                await InsertHistoryAsync(conn, second.Id, kind == BacklogLinkKind.Duplicate ? "link-duplicate" : "link-related", now, new { linkedId = first.Id }, cancellationToken);
                var link = (await LoadLinkAsync(conn, item1, item2, cancellationToken))!;
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return link;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
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
        var result = await TryCloseByIdWithResultAsync(id, reason, note: null, cancellationToken);
        return result.Closed;
    }

    // Closes the item if Open and optionally records a note; no-op if already Done or absent; never throws.
    public async Task<BacklogCloseResult> TryCloseByIdWithResultAsync(
        string id,
        string? reason = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithBusyRetryAsync(async () =>
            {
                await using var conn = OpenConnection();
                await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
                await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
                var updatedAt = DateTimeOffset.UtcNow.ToString("O");
                try
                {
                    var existing = await LoadItemByIdAsync(conn, id, cancellationToken, includeNotes: false);
                    if (existing is null)
                    {
                        await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                        return new BacklogCloseResult(BacklogCloseDisposition.NotFound, null);
                    }

                    if (existing.Status == BacklogItemStatus.Done)
                    {
                        await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                        return new BacklogCloseResult(BacklogCloseDisposition.AlreadyDone, existing);
                    }

                    await using (var cmd = conn.CreateCommand())
                    {
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
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        await using var insert = conn.CreateCommand();
                        insert.CommandText = """
                            INSERT INTO backlog_notes (backlog_item_id, created_at, text)
                            VALUES ($backlog_item_id, $created_at, $text)
                            """;
                        insert.Parameters.AddWithValue("$backlog_item_id", id);
                        insert.Parameters.AddWithValue("$created_at", updatedAt);
                        insert.Parameters.AddWithValue("$text", note);
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                    }

                    var resultItem = await LoadItemByIdAsync(conn, id, cancellationToken);
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new BacklogCloseResult(BacklogCloseDisposition.Closed, resultItem);
                }
                catch
                {
                    try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                    throw;
                }
            }, cancellationToken);
        }
        catch
        {
            return new BacklogCloseResult(BacklogCloseDisposition.NotFound, null);
        }
    }

    // Returns true if inserted, false if the item already existed (idempotent for import).
    public async Task<bool> UpsertAsync(BacklogItem item, CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id, priority, tags, superseded_by, superseded_at)
                    VALUES ($id, $title, $body, $status, $created_at, $updated_at, $source_goal_id, $priority, $tags, $superseded_by, $superseded_at)
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
        }, cancellationToken);
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
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? "" : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)));

    private static async Task<BacklogItem?> LoadItemByIdAsync(
        SqliteConnection conn,
        string id,
        CancellationToken cancellationToken,
        bool includeNotes = true)
    {
        BacklogItem? item;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {SelectColumns} FROM backlog WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            item = await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
        }

        if (item is null || !includeNotes)
        {
            return item;
        }

        var notes = await LoadNotesAsync(conn, id, cancellationToken);
        var links = await LoadLinksForItemAsync(conn, id, cancellationToken);
        var dependencies = await LoadDependenciesAsync(conn, id, asDependent: true, cancellationToken);
        var dependents = await LoadDependenciesAsync(conn, id, asDependent: false, cancellationToken);
        return item with { Notes = notes, Links = links, Dependencies = dependencies, Dependents = dependents };
    }

    private static async Task<IReadOnlyList<BacklogNote>> LoadNotesAsync(
        SqliteConnection conn,
        string itemId,
        CancellationToken cancellationToken)
    {
        var notes = new List<BacklogNote>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT created_at, text
            FROM backlog_notes
            WHERE backlog_item_id = $backlog_item_id
            ORDER BY created_at ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("$backlog_item_id", itemId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            notes.Add(new BacklogNote(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetString(1)));
        }

        return notes;
    }

    private static async Task<IReadOnlyList<BacklogItem>> AttachRelationshipsAsync(
        SqliteConnection conn,
        IReadOnlyList<BacklogItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return items;

        var linksByItem = new Dictionary<string, List<BacklogLink>>(StringComparer.Ordinal);
        foreach (var item in items)
            linksByItem[item.Id] = [];

        await CreateTempIdTableAsync(conn, items.Select(item => item.Id), cancellationToken);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT item1_id, item2_id, kind, canonical_id, created_at
                FROM backlog_links
                WHERE item1_id IN (
                    SELECT id FROM temp_backlog_link_ids
                )
                   OR item2_id IN (
                    SELECT id FROM temp_backlog_link_ids
                )
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var link = ReadLink(reader);
                if (linksByItem.TryGetValue(link.Item1Id, out var first))
                    first.Add(link);
                if (linksByItem.TryGetValue(link.Item2Id, out var second))
                    second.Add(link);
            }
        }

        await RunNonQueryAsync(conn, "DROP TABLE temp_backlog_link_ids", cancellationToken);
        var results = new List<BacklogItem>(items.Count);
        foreach (var item in items)
        {
            var dependencies = await LoadDependenciesAsync(conn, item.Id, asDependent: true, cancellationToken);
            var dependents = await LoadDependenciesAsync(conn, item.Id, asDependent: false, cancellationToken);
            results.Add(item with
            {
                Links = linksByItem[item.Id],
                Dependencies = dependencies,
                Dependents = dependents
            });
        }
        return results;
    }

    private static async Task CreateTempIdTableAsync(SqliteConnection conn, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        await RunNonQueryAsync(conn, "DROP TABLE IF EXISTS temp_backlog_link_ids", cancellationToken);
        await RunNonQueryAsync(conn, "CREATE TEMP TABLE temp_backlog_link_ids (id TEXT PRIMARY KEY)", cancellationToken);
        foreach (var id in ids)
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO temp_backlog_link_ids (id) VALUES ($id)";
            insert.Parameters.AddWithValue("$id", id);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<BacklogLink>> LoadLinksForItemAsync(
        SqliteConnection conn,
        string itemId,
        CancellationToken cancellationToken)
    {
        var links = new List<BacklogLink>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT item1_id, item2_id, kind, canonical_id, created_at
            FROM backlog_links
            WHERE item1_id = $item_id OR item2_id = $item_id
            ORDER BY created_at ASC, item1_id ASC, item2_id ASC
            """;
        cmd.Parameters.AddWithValue("$item_id", itemId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            links.Add(ReadLink(reader));
        return links;
    }

    private static async Task<BacklogLink?> LoadLinkAsync(
        SqliteConnection conn,
        string item1Id,
        string item2Id,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT item1_id, item2_id, kind, canonical_id, created_at
            FROM backlog_links
            WHERE item1_id = $item1_id AND item2_id = $item2_id
            """;
        cmd.Parameters.AddWithValue("$item1_id", item1Id);
        cmd.Parameters.AddWithValue("$item2_id", item2Id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLink(reader) : null;
    }

    private static BacklogLink ReadLink(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            Enum.Parse<BacklogLinkKind>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)));

    private static async Task<IReadOnlyList<BacklogDependency>> LoadDependenciesAsync(
        SqliteConnection conn,
        string itemId,
        bool asDependent,
        CancellationToken cancellationToken)
    {
        var results = new List<BacklogDependency>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = asDependent
            ? """
                SELECT dependent_id, prerequisite_id, prerequisite_kind, created_at
                FROM backlog_dependencies
                WHERE dependent_id = $item_id
                ORDER BY sequence ASC
                """
            : """
                SELECT dependent_id, prerequisite_id, prerequisite_kind, created_at
                FROM backlog_dependencies
                WHERE prerequisite_id = $item_id AND prerequisite_kind = 'Backlog'
                ORDER BY sequence ASC
                """;
        cmd.Parameters.AddWithValue("$item_id", itemId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BacklogDependency(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<BacklogDependencyTargetKind>(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3))));
        }
        return results;
    }

    private static async Task<BacklogDependency> ValidateAndInsertDependencyAsync(
        SqliteConnection conn,
        string dependentId,
        BacklogDependencyTarget prerequisite,
        bool allowExisting,
        Func<string, bool>? goalExists,
        CancellationToken cancellationToken)
    {
        if (await LoadItemByIdAsync(conn, dependentId, cancellationToken, includeNotes: false) is null)
            throw new InvalidOperationException($"No backlog item found with id '{dependentId}'.");
        if (string.Equals(dependentId, prerequisite.Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"Backlog item '{ShortId(dependentId)}' cannot depend on itself.");
        if (prerequisite.Kind == BacklogDependencyTargetKind.Backlog &&
            await LoadItemByIdAsync(conn, prerequisite.Id, cancellationToken, includeNotes: false) is null)
        {
            throw new InvalidOperationException($"No backlog prerequisite found with id '{prerequisite.Id}'.");
        }
        if (prerequisite.Kind == BacklogDependencyTargetKind.Goal &&
            (goalExists is null || !goalExists(prerequisite.Id)))
        {
            throw new InvalidOperationException($"No goal prerequisite found with id '{prerequisite.Id}'.");
        }

        if (prerequisite.Kind == BacklogDependencyTargetKind.Backlog &&
            await FindDependencyPathAsync(conn, prerequisite.Id, dependentId, cancellationToken) is { } path)
        {
            var cycle = new[] { dependentId }.Concat(path).Select(ShortId);
            throw new InvalidOperationException($"Adding backlog dependency would create cycle: {string.Join(" -> ", cycle)}.");
        }

        var now = DateTimeOffset.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = allowExisting
            ? """
                INSERT OR IGNORE INTO backlog_dependencies
                    (dependent_id, prerequisite_id, prerequisite_kind, created_at)
                VALUES ($dependent_id, $prerequisite_id, $prerequisite_kind, $created_at)
                """
            : """
                INSERT INTO backlog_dependencies
                    (dependent_id, prerequisite_id, prerequisite_kind, created_at)
                VALUES ($dependent_id, $prerequisite_id, $prerequisite_kind, $created_at)
                """;
        cmd.Parameters.AddWithValue("$dependent_id", dependentId);
        cmd.Parameters.AddWithValue("$prerequisite_id", prerequisite.Id);
        cmd.Parameters.AddWithValue("$prerequisite_kind", prerequisite.Kind.ToString());
        cmd.Parameters.AddWithValue("$created_at", now.ToString("O"));
        var inserted = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 0)
        {
            return (await LoadDependenciesAsync(conn, dependentId, asDependent: true, cancellationToken))
                .Single(dependency => dependency.PrerequisiteId == prerequisite.Id);
        }
        return new BacklogDependency(dependentId, prerequisite.Id, prerequisite.Kind, now);
    }

    private static async Task<IReadOnlyList<string>?> FindDependencyPathAsync(
        SqliteConnection conn,
        string startId,
        string targetId,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<IReadOnlyList<string>>();
        queue.Enqueue([startId]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var current = path[^1];
            if (string.Equals(current, targetId, StringComparison.Ordinal))
                return path;
            if (!visited.Add(current))
                continue;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT prerequisite_id
                FROM backlog_dependencies
                WHERE dependent_id = $dependent_id AND prerequisite_kind = 'Backlog'
                ORDER BY sequence ASC
                """;
            cmd.Parameters.AddWithValue("$dependent_id", current);
            var nextIds = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    nextIds.Add(reader.GetString(0));
            }
            foreach (var nextId in nextIds)
                queue.Enqueue(path.Concat([nextId]).ToArray());
        }
        return null;
    }

    private static string ShortId(string id) => id[..Math.Min(8, id.Length)];

    private static (string Item1Id, string Item2Id) NormalizeLinkIds(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);

    private static async Task<bool> WouldCreateSupersedeCycleAsync(
        SqliteConnection conn,
        string oldId,
        string replacementId,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = replacementId;
        while (seen.Add(current))
        {
            if (string.Equals(current, oldId, StringComparison.Ordinal))
                return true;

            var item = await LoadItemByIdAsync(conn, current, cancellationToken, includeNotes: false);
            if (item?.SupersededBy is null)
                return false;
            current = item.SupersededBy;
        }

        return true;
    }

    private static async Task InsertHistoryAsync(
        SqliteConnection conn,
        string itemId,
        string action,
        string createdAt,
        object details,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backlog_history (backlog_item_id, action, created_at, details)
            VALUES ($backlog_item_id, $action, $created_at, $details)
            """;
        cmd.Parameters.AddWithValue("$backlog_item_id", itemId);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.Parameters.AddWithValue("$details", System.Text.Json.JsonSerializer.Serialize(details));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToHistorySnapshot(BacklogItem item) => new
    {
        item.Title,
        item.Body,
        Status = item.Status.ToString(),
        item.Priority,
        item.Tags,
        item.SupersededBy,
        SupersededAt = item.SupersededAt?.ToString("O")
    };

    private static async Task InsertItemAsync(SqliteConnection conn, BacklogItem item, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id, priority, tags, superseded_by, superseded_at)
            VALUES ($id, $title, $body, $status, $created_at, $updated_at, $source_goal_id, $priority, $tags, $superseded_by, $superseded_at)
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
        cmd.Parameters.AddWithValue("$priority", (object?)item.Priority ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", item.Tags);
        cmd.Parameters.AddWithValue("$superseded_by", (object?)item.SupersededBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$superseded_at", item.SupersededAt is null ? DBNull.Value : (object)item.SupersededAt.Value.ToString("O"));
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table})";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        RunNonQuery(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
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
