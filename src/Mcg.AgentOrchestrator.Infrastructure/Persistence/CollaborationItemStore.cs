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

    Task<CollaborationItem> RaiseWithActionsAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string correlationKey,
        IReadOnlyList<CollaborationActionBinding> actions,
        CancellationToken cancellationToken = default);

    Task<bool> TryResolveAsync(
        string correlationKey,
        string resolution,
        CancellationToken cancellationToken = default);

    Task<int> ResolveOpenForGoalAsync(
        string goalId,
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

    Task<IReadOnlyList<CollaborationItem>> ListForGoalIdsAsync(
        IEnumerable<string> goalIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationBoundAction>> ListActionsAsync(
        string correlationKey,
        CancellationToken cancellationToken = default);

    Task UpdateRenderedContentHashAsync(
        IEnumerable<string> correlationKeys,
        string renderedContentHash,
        CancellationToken cancellationToken = default);

    Task<CollaborationActionApplyResult> TryClaimActionAsync(
        string correlationKey,
        int actionIndex,
        string actorId,
        string interactionId,
        long? currentGoalStateVersion,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default);

    Task<CollaborationDecisionAuditEntry> RecordRejectedDecisionAsync(
        string correlationKey,
        int? actionIndex,
        string actorId,
        string interactionId,
        string reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollaborationDecisionAuditEntry>> ListDecisionAuditAsync(
        CancellationToken cancellationToken = default);
}

public sealed record CollaborationActionBinding(
    string Label,
    string Command,
    bool RequiresConfirmation = false,
    bool RequiresInput = false,
    long? ExpectedGoalStateVersion = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record CollaborationBoundAction(
    string CorrelationKey,
    int ActionIndex,
    string Label,
    string Command,
    bool RequiresConfirmation,
    bool RequiresInput,
    long? ExpectedGoalStateVersion,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt,
    string? RenderedContentHash);

public sealed record CollaborationActionApplyResult(
    bool Applied,
    bool Duplicate,
    CollaborationBoundAction? Action,
    CollaborationDecisionAuditEntry Audit,
    string? ErrorMessage);

public sealed record CollaborationDecisionAuditEntry(
    long Id,
    string CorrelationKey,
    int? ActionIndex,
    string ActorId,
    string InteractionId,
    string Outcome,
    string? Command,
    string? RejectionReason,
    long? ExpectedGoalStateVersion,
    long? ActualGoalStateVersion,
    string? RenderedContentHash,
    DateTimeOffset DecidedAt);

public sealed class CollaborationItemStore : ICollaborationItemStore
{
    private readonly string _dbPath;
    private static readonly TimeSpan DefaultActionTtl = TimeSpan.FromHours(12);
    private static readonly HashSet<string> AllowedActionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "acceptance",
        "agent-add",
        "answer",
        "conduct",
        "doctor",
        "input-needed",
        "land",
        "next",
        "operator-inbox-ack",
        "recover",
        "re-delegate",
        "retry",
        "readiness",
        "refresh-dispatch",
        "subscription-plan",
        "verify",
        "verify-needed",
        "workspace"
    };

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

    // Bounded retry on a transient SQLITE_BUSY/LOCKED: busy_timeout (30s) handles the simple lock-wait,
    // but the deadlock-avoidance path can still surface an immediate BUSY; this turns that into a brief
    // wait instead of a fatal throw. Mirrors SqliteOrchestratorStateRepository (matching the loop
    // critical-path stores so a write concurrent with operator-listen never hard-fails).
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
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_items_goal_id
                ON collaboration_items (goal_id)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_item_actions (
                correlation_key              TEXT NOT NULL,
                action_index                 INTEGER NOT NULL,
                label                        TEXT NOT NULL,
                command                      TEXT NOT NULL,
                requires_confirmation        INTEGER NOT NULL,
                requires_input               INTEGER NOT NULL,
                expected_goal_state_version  INTEGER,
                expires_at                   TEXT NOT NULL,
                consumed_at                  TEXT,
                rendered_content_hash        TEXT,
                PRIMARY KEY (correlation_key, action_index)
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_item_actions_expiry
                ON collaboration_item_actions (expires_at)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_audit (
                id                           INTEGER PRIMARY KEY AUTOINCREMENT,
                correlation_key              TEXT NOT NULL,
                action_index                 INTEGER,
                actor_id                     TEXT NOT NULL,
                interaction_id               TEXT NOT NULL UNIQUE,
                outcome                      TEXT NOT NULL,
                command                      TEXT,
                rejection_reason             TEXT,
                expected_goal_state_version  INTEGER,
                actual_goal_state_version    INTEGER,
                rendered_content_hash        TEXT,
                decided_at                   TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_decision_audit_correlation
                ON collaboration_decision_audit (correlation_key)
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
        if (string.IsNullOrWhiteSpace(correlationKey))
            return await RaiseCoreAsync(type, goalId, subject, body, correlationKey, null, cancellationToken);

        var defaultActions = BuildDefaultActions(type, correlationKey);
        return await RaiseCoreAsync(type, goalId, subject, body, correlationKey, defaultActions, cancellationToken);
    }

    public async Task<CollaborationItem> RaiseWithActionsAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string correlationKey,
        IReadOnlyList<CollaborationActionBinding> actions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationKey))
            throw new ArgumentException("Correlation key cannot be empty when binding actions.", nameof(correlationKey));

        if (actions.Count == 0)
            throw new ArgumentException("At least one action is required.", nameof(actions));

        return await RaiseCoreAsync(type, goalId, subject, body, correlationKey, actions, cancellationToken);
    }

    private async Task<CollaborationItem> RaiseCoreAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey,
        IReadOnlyList<CollaborationActionBinding>? actions,
        CancellationToken cancellationToken)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                // Idempotent on correlation key: if an OPEN (non-terminal) item already exists for this key,
                // refresh its subject/body and return it rather than inserting a duplicate. Without this, a
                // conductor that re-escalates the same goal+reason each tick piles up identical items
                // (observed: 12 copies of one landing escalation), which also collide as duplicate Discord
                // button customIds. Raising "another one" while one is pending is the bug — not the rendering.
                if (!string.IsNullOrWhiteSpace(correlationKey))
                {
                    var existing = await TryReadOpenItemByCorrelationKeyAsync(conn, correlationKey!, cancellationToken);
                    if (existing is not null)
                    {
                        await using var refresh = conn.CreateCommand();
                        refresh.CommandText = "UPDATE collaboration_items SET subject = $subject, body = $body WHERE id = $id";
                        refresh.Parameters.AddWithValue("$subject", subject);
                        refresh.Parameters.AddWithValue("$body", body);
                        refresh.Parameters.AddWithValue("$id", existing.Id);
                        await refresh.ExecuteNonQueryAsync(cancellationToken);
                        if (actions is not null &&
                            !await HasActionRowsAsync(conn, correlationKey!, cancellationToken))
                        {
                            await ReplaceActionsAsync(conn, correlationKey!, actions, cancellationToken);
                        }

                        await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                        return existing with { Subject = subject, Body = body };
                    }
                }

                var item = new CollaborationItem(
                    Guid.NewGuid().ToString("n"), type, goalId, CollaborationItemStatus.Raised,
                    subject, body, correlationKey, DateTimeOffset.UtcNow, null, null);
                await InsertItemAsync(conn, item, cancellationToken);
                if (!string.IsNullOrWhiteSpace(correlationKey) && actions is not null)
                    await ReplaceActionsAsync(conn, correlationKey!, actions, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return item;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    private async Task<CollaborationItem?> TryReadOpenItemByCorrelationKeyAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, string correlationKey, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution
            FROM collaboration_items
            WHERE correlation_key = $key AND status NOT IN ('Resolved', 'Closed')
            ORDER BY raised_at ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$key", correlationKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<bool> TryResolveAsync(
        string correlationKey,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
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
        }, cancellationToken);
    }

    public async Task<int> ResolveOpenForGoalAsync(
        string goalId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goalId))
        {
            throw new ArgumentException("Goal id cannot be empty.", nameof(goalId));
        }

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var resolvedAt = DateTimeOffset.UtcNow.ToString("O");
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE collaboration_items
                    SET status = 'Resolved', resolved_at = $resolved_at, resolution = $resolution
                    WHERE goal_id = $goal_id
                      AND type IN ('Decision', 'Clarification', 'Verify')
                      AND status NOT IN ('Resolved', 'Closed')
                    """;
                cmd.Parameters.AddWithValue("$resolved_at", resolvedAt);
                cmd.Parameters.AddWithValue("$resolution", resolution);
                cmd.Parameters.AddWithValue("$goal_id", goalId);
                var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return rows;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<bool> TryMarkDeliveredAsync(
        string correlationKey,
        CancellationToken cancellationToken = default)
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
        }, cancellationToken);
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

    public async Task<IReadOnlyList<CollaborationItem>> ListForGoalIdsAsync(
        IEnumerable<string> goalIds,
        CancellationToken cancellationToken = default)
    {
        var scopedGoalIds = goalIds
            .Where(goalId => !string.IsNullOrWhiteSpace(goalId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scopedGoalIds.Length == 0)
        {
            return [];
        }

        await using var conn = OpenConnection();
        var results = new List<CollaborationItem>();
        await using var cmd = conn.CreateCommand();
        var parameterNames = new string[scopedGoalIds.Length];
        for (var i = 0; i < scopedGoalIds.Length; i++)
        {
            parameterNames[i] = $"$goal_id_{i}";
            cmd.Parameters.AddWithValue(parameterNames[i], scopedGoalIds[i]);
        }

        cmd.CommandText = $"""
            SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution
            FROM collaboration_items
            WHERE goal_id IN ({string.Join(", ", parameterNames)})
            ORDER BY raised_at ASC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<IReadOnlyList<CollaborationBoundAction>> ListActionsAsync(
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<CollaborationBoundAction>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, action_index, label, command, requires_confirmation, requires_input,
                   expected_goal_state_version, expires_at, consumed_at, rendered_content_hash
            FROM collaboration_item_actions
            WHERE correlation_key = $key
              AND consumed_at IS NULL
              AND expires_at > $now
            ORDER BY action_index ASC
            """;
        cmd.Parameters.AddWithValue("$key", correlationKey);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadAction(reader));
        return results;
    }

    public async Task UpdateRenderedContentHashAsync(
        IEnumerable<string> correlationKeys,
        string renderedContentHash,
        CancellationToken cancellationToken = default)
    {
        var keys = correlationKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
            return;

        await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                foreach (var key in keys)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        UPDATE collaboration_item_actions
                        SET rendered_content_hash = $hash
                        WHERE correlation_key = $key
                          AND consumed_at IS NULL
                        """;
                    cmd.Parameters.AddWithValue("$hash", renderedContentHash);
                    cmd.Parameters.AddWithValue("$key", key);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

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

    public async Task<CollaborationActionApplyResult> TryClaimActionAsync(
        string correlationKey,
        int actionIndex,
        string actorId,
        string interactionId,
        long? currentGoalStateVersion,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var duplicate = await TryReadAuditByInteractionIdAsync(conn, interactionId, cancellationToken);
                if (duplicate is not null)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new CollaborationActionApplyResult(false, true, null, duplicate, "Duplicate interaction.");
                }

                var action = await TryReadActionAsync(conn, correlationKey, actionIndex, cancellationToken);
                if (action is null)
                {
                    var audit = await InsertAuditAsync(
                        conn, correlationKey, actionIndex, actorId, interactionId, "Rejected", null,
                        "Unknown action reference.", null, currentGoalStateVersion, null, decidedAt, cancellationToken);
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new CollaborationActionApplyResult(false, false, null, audit, audit.RejectionReason);
                }

                if (action.ConsumedAt is not null)
                {
                    var audit = await InsertAuditAsync(
                        conn, correlationKey, actionIndex, actorId, interactionId, "Rejected", action.Command,
                        "Action already consumed.", action.ExpectedGoalStateVersion, currentGoalStateVersion,
                        action.RenderedContentHash, decidedAt, cancellationToken);
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new CollaborationActionApplyResult(false, false, action, audit, audit.RejectionReason);
                }

                if (decidedAt > action.ExpiresAt)
                {
                    var audit = await InsertAuditAsync(
                        conn, correlationKey, actionIndex, actorId, interactionId, "Rejected", action.Command,
                        "Action expired.", action.ExpectedGoalStateVersion, currentGoalStateVersion,
                        action.RenderedContentHash, decidedAt, cancellationToken);
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new CollaborationActionApplyResult(false, false, action, audit, audit.RejectionReason);
                }

                if (action.ExpectedGoalStateVersion is not null &&
                    currentGoalStateVersion != action.ExpectedGoalStateVersion)
                {
                    var audit = await InsertAuditAsync(
                        conn, correlationKey, actionIndex, actorId, interactionId, "Rejected", action.Command,
                        "Stale goal state version.", action.ExpectedGoalStateVersion, currentGoalStateVersion,
                        action.RenderedContentHash, decidedAt, cancellationToken);
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new CollaborationActionApplyResult(false, false, action, audit, audit.RejectionReason);
                }

                var consumedAt = decidedAt.ToString("O");
                await using (var consume = conn.CreateCommand())
                {
                    consume.CommandText = """
                        UPDATE collaboration_item_actions
                        SET consumed_at = $consumed_at
                        WHERE correlation_key = $key
                          AND action_index = $index
                          AND consumed_at IS NULL
                        """;
                    consume.Parameters.AddWithValue("$consumed_at", consumedAt);
                    consume.Parameters.AddWithValue("$key", correlationKey);
                    consume.Parameters.AddWithValue("$index", actionIndex);
                    await consume.ExecuteNonQueryAsync(cancellationToken);
                }

                var appliedAudit = await InsertAuditAsync(
                    conn, correlationKey, actionIndex, actorId, interactionId, "Applied", action.Command,
                    null, action.ExpectedGoalStateVersion, currentGoalStateVersion,
                    action.RenderedContentHash, decidedAt, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return new CollaborationActionApplyResult(
                    true, false, action with { ConsumedAt = decidedAt }, appliedAudit, null);
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<CollaborationDecisionAuditEntry> RecordRejectedDecisionAsync(
        string correlationKey,
        int? actionIndex,
        string actorId,
        string interactionId,
        string reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await TryReadAuditByInteractionIdAsync(conn, interactionId, cancellationToken);
                if (existing is not null)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return existing;
                }

                var referencedAction = actionIndex is null
                    ? null
                    : await TryReadActionAsync(conn, correlationKey, actionIndex.Value, cancellationToken);
                var audit = await InsertAuditAsync(
                    conn, correlationKey, actionIndex, actorId, interactionId, "Rejected", null,
                    reason, referencedAction?.ExpectedGoalStateVersion, null,
                    referencedAction?.RenderedContentHash, decidedAt, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return audit;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<CollaborationDecisionAuditEntry>> ListDecisionAuditAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var results = new List<CollaborationDecisionAuditEntry>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, correlation_key, action_index, actor_id, interaction_id, outcome, command,
                   rejection_reason, expected_goal_state_version, actual_goal_state_version,
                   rendered_content_hash, decided_at
            FROM collaboration_decision_audit
            ORDER BY id ASC
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadAudit(reader));
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

    private static CollaborationBoundAction ReadAction(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) != 0,
            reader.GetInt32(5) != 0,
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static CollaborationDecisionAuditEntry ReadAudit(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11)));

    private static IReadOnlyList<CollaborationActionBinding>? BuildDefaultActions(
        CollaborationItemType type,
        string correlationKey) =>
        type switch
        {
            CollaborationItemType.Decision => [new CollaborationActionBinding("Resolve", $"operator-inbox-ack {correlationKey}")],
            CollaborationItemType.Verify => [new CollaborationActionBinding("Verify", $"operator-inbox-ack {correlationKey}")],
            _ => null
        };

    private static async Task ReplaceActionsAsync(
        SqliteConnection conn,
        string correlationKey,
        IReadOnlyList<CollaborationActionBinding> actions,
        CancellationToken cancellationToken)
    {
        await using (var delete = conn.CreateCommand())
        {
            delete.CommandText = """
                DELETE FROM collaboration_item_actions
                WHERE correlation_key = $key
                  AND consumed_at IS NULL
                """;
            delete.Parameters.AddWithValue("$key", correlationKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var i = 0; i < actions.Count; i++)
        {
            var action = NormalizeAction(actions[i]);
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO collaboration_item_actions (
                    correlation_key, action_index, label, command, requires_confirmation, requires_input,
                    expected_goal_state_version, expires_at, consumed_at, rendered_content_hash)
                VALUES (
                    $correlation_key, $action_index, $label, $command, $requires_confirmation, $requires_input,
                    $expected_goal_state_version, $expires_at, NULL, NULL)
                ON CONFLICT(correlation_key, action_index) DO UPDATE SET
                    label = excluded.label,
                    command = excluded.command,
                    requires_confirmation = excluded.requires_confirmation,
                    requires_input = excluded.requires_input,
                    expected_goal_state_version = excluded.expected_goal_state_version,
                    expires_at = excluded.expires_at,
                    rendered_content_hash = NULL
                WHERE collaboration_item_actions.consumed_at IS NULL
                """;
            insert.Parameters.AddWithValue("$correlation_key", correlationKey);
            insert.Parameters.AddWithValue("$action_index", i);
            insert.Parameters.AddWithValue("$label", action.Label);
            insert.Parameters.AddWithValue("$command", action.Command);
            insert.Parameters.AddWithValue("$requires_confirmation", action.RequiresConfirmation ? 1 : 0);
            insert.Parameters.AddWithValue("$requires_input", action.RequiresInput ? 1 : 0);
            insert.Parameters.AddWithValue("$expected_goal_state_version", (object?)action.ExpectedGoalStateVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("$expires_at", (action.ExpiresAt ?? DateTimeOffset.UtcNow.Add(DefaultActionTtl)).ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> HasActionRowsAsync(
        SqliteConnection conn,
        string correlationKey,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM collaboration_item_actions
            WHERE correlation_key = $key
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$key", correlationKey);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static CollaborationActionBinding NormalizeAction(CollaborationActionBinding action)
    {
        if (string.IsNullOrWhiteSpace(action.Label))
            throw new ArgumentException("Action label cannot be empty.", nameof(action));
        if (string.IsNullOrWhiteSpace(action.Command))
            throw new ArgumentException("Action command cannot be empty.", nameof(action));

        var command = action.Command.Trim();
        var verb = command.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (verb is null || !AllowedActionVerbs.Contains(verb))
            throw new ArgumentException($"Action verb '{verb ?? "<empty>"}' is not allowed.", nameof(action));

        return action with
        {
            Label = action.Label.Trim(),
            Command = command
        };
    }

    private static async Task<CollaborationBoundAction?> TryReadActionAsync(
        SqliteConnection conn,
        string correlationKey,
        int actionIndex,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, action_index, label, command, requires_confirmation, requires_input,
                   expected_goal_state_version, expires_at, consumed_at, rendered_content_hash
            FROM collaboration_item_actions
            WHERE correlation_key = $key
              AND action_index = $index
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$key", correlationKey);
        cmd.Parameters.AddWithValue("$index", actionIndex);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAction(reader) : null;
    }

    private static async Task<CollaborationDecisionAuditEntry?> TryReadAuditByInteractionIdAsync(
        SqliteConnection conn,
        string interactionId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, correlation_key, action_index, actor_id, interaction_id, outcome, command,
                   rejection_reason, expected_goal_state_version, actual_goal_state_version,
                   rendered_content_hash, decided_at
            FROM collaboration_decision_audit
            WHERE interaction_id = $interaction_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$interaction_id", interactionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAudit(reader) : null;
    }

    private static async Task<CollaborationDecisionAuditEntry> InsertAuditAsync(
        SqliteConnection conn,
        string correlationKey,
        int? actionIndex,
        string actorId,
        string interactionId,
        string outcome,
        string? command,
        string? rejectionReason,
        long? expectedGoalStateVersion,
        long? actualGoalStateVersion,
        string? renderedContentHash,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collaboration_decision_audit (
                correlation_key, action_index, actor_id, interaction_id, outcome, command,
                rejection_reason, expected_goal_state_version, actual_goal_state_version,
                rendered_content_hash, decided_at)
            VALUES (
                $correlation_key, $action_index, $actor_id, $interaction_id, $outcome, $command,
                $rejection_reason, $expected_goal_state_version, $actual_goal_state_version,
                $rendered_content_hash, $decided_at)
            RETURNING id, correlation_key, action_index, actor_id, interaction_id, outcome, command,
                      rejection_reason, expected_goal_state_version, actual_goal_state_version,
                      rendered_content_hash, decided_at
            """;
        cmd.Parameters.AddWithValue("$correlation_key", string.IsNullOrWhiteSpace(correlationKey) ? "unknown" : correlationKey);
        cmd.Parameters.AddWithValue("$action_index", (object?)actionIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$actor_id", actorId);
        cmd.Parameters.AddWithValue("$interaction_id", interactionId);
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$command", (object?)command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rejection_reason", (object?)rejectionReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expected_goal_state_version", (object?)expectedGoalStateVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$actual_goal_state_version", (object?)actualGoalStateVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rendered_content_hash", (object?)renderedContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$decided_at", decidedAt.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Failed to insert collaboration decision audit row.");
        return ReadAudit(reader);
    }

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
