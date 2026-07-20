using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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

    Task<DecisionRequest> RaiseDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<DecisionState?> GetDecisionStateAsync(
        string requestId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DecisionRequest>> ListDecisionRequestsAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default);

    Task<NotificationDelivery> RecordNotificationDeliveryAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default);

    Task<DecisionReceipt> RecordDecisionAsync(
        string requestId,
        string actorId,
        string channel,
        AuthorizationTier authenticationAssurance,
        long? expectedGoalStateVersion,
        DecisionResponse response,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default);

    Task<DecisionReceipt> RecordExpiredDefaultDispositionAsync(
        string requestId,
        DateTimeOffset expiredAt,
        CancellationToken cancellationToken = default);

    Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(
        string requestId,
        string decisionReceiptId,
        DecisionActionRef actionRef,
        long? currentGoalStateVersion,
        string result,
        DateTimeOffset appliedAt,
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

public sealed record DecisionEffectApplyResult(
    bool Applied,
    bool Duplicate,
    EffectReceipt Receipt,
    string? ErrorMessage);

public sealed class CollaborationItemStore : ICollaborationItemStore
{
    private readonly string _dbPath;
    private static readonly TimeSpan DefaultActionTtl = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_requests (
                id                       TEXT PRIMARY KEY,
                kind                     TEXT NOT NULL,
                goal_id                  TEXT,
                subject                  TEXT NOT NULL,
                rendered_text            TEXT NOT NULL,
                template_version         TEXT NOT NULL,
                evidence_manifest_json   TEXT NOT NULL,
                evidence_manifest_hash   TEXT NOT NULL,
                expires_at               TEXT NOT NULL,
                default_disposition      TEXT NOT NULL,
                blocking_impact_json     TEXT NOT NULL,
                reuse_scopes_json        TEXT NOT NULL,
                created_at               TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE INDEX IF NOT EXISTS idx_collaboration_decision_requests_goal
                ON collaboration_decision_requests (goal_id)
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_request_actions (
                request_id                   TEXT NOT NULL,
                action_ref                   TEXT NOT NULL,
                label                        TEXT NOT NULL,
                kind                         TEXT NOT NULL,
                required_tier                TEXT NOT NULL,
                expires_at                   TEXT NOT NULL,
                expected_goal_state_version  INTEGER,
                PRIMARY KEY (request_id, action_ref)
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_decision_receipts (
                id                           TEXT PRIMARY KEY,
                request_id                   TEXT NOT NULL UNIQUE,
                rendered_text                TEXT NOT NULL,
                template_version             TEXT NOT NULL,
                evidence_manifest_json       TEXT NOT NULL,
                evidence_manifest_hash       TEXT NOT NULL,
                actor_id                     TEXT NOT NULL,
                channel                      TEXT NOT NULL,
                authentication_assurance     TEXT NOT NULL,
                expected_goal_state_version  INTEGER,
                action_ref                   TEXT NOT NULL,
                response_value               TEXT NOT NULL,
                selected_reuse_scope         TEXT NOT NULL,
                permanent_policy_proposed    INTEGER NOT NULL,
                recorded_at                  TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_notification_deliveries (
                id                 TEXT PRIMARY KEY,
                request_id         TEXT NOT NULL,
                channel            TEXT NOT NULL,
                target             TEXT NOT NULL,
                content_hash       TEXT NOT NULL,
                delivered_at       TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS collaboration_effect_receipts (
                id                           TEXT PRIMARY KEY,
                request_id                   TEXT NOT NULL,
                decision_receipt_id          TEXT NOT NULL,
                action_ref                   TEXT NOT NULL,
                status                       TEXT NOT NULL,
                expected_goal_state_version  INTEGER,
                actual_goal_state_version    INTEGER,
                result                       TEXT NOT NULL,
                recorded_at                  TEXT NOT NULL,
                UNIQUE (request_id, decision_receipt_id, action_ref)
            )
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
                            (!await HasActionRowsAsync(conn, correlationKey!, cancellationToken) ||
                             await HasOnlyUnconsumedDefaultActionsAsync(conn, existing.Type, correlationKey!, cancellationToken)))
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

    public async Task<DecisionRequest> RaiseDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateDecisionRequest(request);
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await TryReadDecisionRequestAsync(conn, request.Id, cancellationToken);
                if (existing is not null)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return existing;
                }

                await InsertDecisionRequestAsync(conn, request, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return request;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<DecisionState?> GetDecisionStateAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var request = await TryReadDecisionRequestAsync(conn, requestId, cancellationToken);
        if (request is null)
            return null;

        var effect = await TryReadEffectReceiptForRequestAsync(conn, requestId, cancellationToken);
        var receipt = await TryReadDecisionReceiptForRequestAsync(conn, requestId, effect, cancellationToken);
        return DecisionState.Create(request, receipt, effect);
    }

    public async Task<IReadOnlyList<DecisionRequest>> ListDecisionRequestsAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        var ids = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            if (goalId is null)
            {
                cmd.CommandText = "SELECT id FROM collaboration_decision_requests ORDER BY created_at ASC";
            }
            else
            {
                cmd.CommandText = "SELECT id FROM collaboration_decision_requests WHERE goal_id = $goal_id ORDER BY created_at ASC";
                cmd.Parameters.AddWithValue("$goal_id", goalId);
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetString(0));
        }

        var results = new List<DecisionRequest>();
        foreach (var id in ids)
        {
            var request = await TryReadDecisionRequestAsync(conn, id, cancellationToken);
            if (request is not null)
                results.Add(request);
        }

        return results;
    }

    public async Task<NotificationDelivery> RecordNotificationDeliveryAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delivery.Id))
            throw new ArgumentException("Notification delivery id cannot be empty.", nameof(delivery));
        if (string.IsNullOrWhiteSpace(delivery.RequestId))
            throw new ArgumentException("Notification delivery request id cannot be empty.", nameof(delivery));
        if (string.IsNullOrWhiteSpace(delivery.ContentHash))
            throw new ArgumentException("Notification delivery content hash cannot be empty.", nameof(delivery));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO collaboration_notification_deliveries (
                        id, request_id, channel, target, content_hash, delivered_at)
                    VALUES (
                        $id, $request_id, $channel, $target, $content_hash, $delivered_at)
                    ON CONFLICT(id) DO NOTHING
                    """;
                cmd.Parameters.AddWithValue("$id", delivery.Id);
                cmd.Parameters.AddWithValue("$request_id", delivery.RequestId);
                cmd.Parameters.AddWithValue("$channel", delivery.Channel);
                cmd.Parameters.AddWithValue("$target", delivery.Target);
                cmd.Parameters.AddWithValue("$content_hash", delivery.ContentHash);
                cmd.Parameters.AddWithValue("$delivered_at", delivery.DeliveredAt.ToString("O"));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return delivery;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<DecisionReceipt> RecordDecisionAsync(
        string requestId,
        string actorId,
        string channel,
        AuthorizationTier authenticationAssurance,
        long? expectedGoalStateVersion,
        DecisionResponse response,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await TryReadDecisionReceiptForRequestAsync(conn, requestId, null, cancellationToken);
                if (existing is not null)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return existing;
                }

                var request = await TryReadDecisionRequestAsync(conn, requestId, cancellationToken)
                    ?? throw new InvalidOperationException($"Decision request '{requestId}' was not found.");
                if (recordedAt > request.ExpiresAt)
                    throw new InvalidOperationException("Decision request expired; record the stated default disposition instead.");
                var action = FindAllowedAction(request, response.ActionRef)
                    ?? throw new InvalidOperationException("Unknown opaque action reference.");
                if (!request.ReuseScopeOptions.Contains(response.SelectedReuseScope))
                    throw new InvalidOperationException("Selected reuse scope was not offered by the decision request.");
                if (!DecisionAuthorization.Meets(authenticationAssurance, AuthorizationTier.Answer))
                    throw new InvalidOperationException("Authentication assurance is below the Answer tier.");

                var normalizedResponse = response with
                {
                    PermanentPolicyProposed = response.PermanentPolicyProposed ||
                        response.SelectedReuseScope == DecisionReuseScope.ProposePermanentPolicy
                };
                var receipt = new DecisionReceipt(
                    Guid.NewGuid().ToString("n"),
                    request.Id,
                    request.RenderedText,
                    request.TemplateVersion,
                    request.EvidenceManifest.Entries,
                    request.EvidenceManifest.ManifestHash,
                    actorId,
                    channel,
                    authenticationAssurance,
                    expectedGoalStateVersion ?? action.ExpectedGoalStateVersion,
                    normalizedResponse,
                    recordedAt,
                    null);
                await InsertDecisionReceiptAsync(conn, receipt, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return receipt;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<DecisionReceipt> RecordExpiredDefaultDispositionAsync(
        string requestId,
        DateTimeOffset expiredAt,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await TryReadDecisionReceiptForRequestAsync(conn, requestId, null, cancellationToken);
                if (existing is not null)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return existing;
                }

                var request = await TryReadDecisionRequestAsync(conn, requestId, cancellationToken)
                    ?? throw new InvalidOperationException($"Decision request '{requestId}' was not found.");
                if (expiredAt < request.ExpiresAt)
                    throw new InvalidOperationException("Decision request has not expired.");

                var actionRef = request.AllowedActions.FirstOrDefault(
                    action => ActionMatchesDefaultDisposition(action.Kind, request.DefaultDisposition))?.ActionRef
                    ?? new DecisionActionRef("expired-default");
                var response = new DecisionResponse(
                    actionRef,
                    $"default:{request.DefaultDisposition}",
                    DecisionReuseScope.ThisOccurrence,
                    false);
                var receipt = new DecisionReceipt(
                    Guid.NewGuid().ToString("n"),
                    request.Id,
                    request.RenderedText,
                    request.TemplateVersion,
                    request.EvidenceManifest.Entries,
                    request.EvidenceManifest.ManifestHash,
                    "system:expiry",
                    "system",
                    AuthorizationTier.Answer,
                    null,
                    response,
                    expiredAt,
                    null);
                await InsertDecisionReceiptAsync(conn, receipt, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return receipt;
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    public async Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(
        string requestId,
        string decisionReceiptId,
        DecisionActionRef actionRef,
        long? currentGoalStateVersion,
        string result,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var existing = await TryReadEffectReceiptAsync(conn, requestId, decisionReceiptId, actionRef, cancellationToken);
                if (existing is not null)
                {
                    await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                    return new DecisionEffectApplyResult(
                        existing.Status == EffectReceiptStatus.Applied,
                        true,
                        existing,
                        existing.Status == EffectReceiptStatus.Applied ? null : existing.Result);
                }

                var request = await TryReadDecisionRequestAsync(conn, requestId, cancellationToken)
                    ?? throw new InvalidOperationException($"Decision request '{requestId}' was not found.");
                var receipt = await TryReadDecisionReceiptAsync(conn, decisionReceiptId, null, cancellationToken)
                    ?? throw new InvalidOperationException($"Decision receipt '{decisionReceiptId}' was not found.");
                var action = FindAllowedAction(request, actionRef);
                var rejection = action is null
                    ? "Unknown opaque action reference."
                    : !string.Equals(receipt.Response.ActionRef.Value, actionRef.Value, StringComparison.Ordinal)
                        ? "Action ref does not match the recorded decision."
                        : !DecisionAuthorization.Meets(receipt.AuthenticationAssurance, action.RequiredTier)
                            ? $"Authentication assurance '{receipt.AuthenticationAssurance}' does not satisfy required tier '{action.RequiredTier}'."
                            : action.ExpectedGoalStateVersion is not null && currentGoalStateVersion != action.ExpectedGoalStateVersion
                                ? "Stale goal state version."
                                : appliedAt > action.ExpiresAt
                                    ? "Action expired."
                                    : null;

                var effect = new EffectReceipt(
                    Guid.NewGuid().ToString("n"),
                    requestId,
                    decisionReceiptId,
                    actionRef,
                    rejection is null ? EffectReceiptStatus.Applied : EffectReceiptStatus.Rejected,
                    action?.ExpectedGoalStateVersion,
                    currentGoalStateVersion,
                    rejection ?? result,
                    appliedAt);
                await InsertEffectReceiptAsync(conn, effect, cancellationToken);
                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return new DecisionEffectApplyResult(effect.Status == EffectReceiptStatus.Applied, false, effect, rejection);
            }
            catch
            {
                try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
                throw;
            }
        }, cancellationToken);
    }

    private static void ValidateDecisionRequest(DecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new ArgumentException("Decision request id cannot be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Subject))
            throw new ArgumentException("Decision request subject cannot be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RenderedText))
            throw new ArgumentException("Decision request rendered text cannot be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TemplateVersion))
            throw new ArgumentException("Decision request template version cannot be empty.", nameof(request));
        if (!request.EvidenceManifest.HasExpectedHash())
            throw new ArgumentException("Evidence manifest hash does not match its entries.", nameof(request));
        if (request.DefaultDisposition == DecisionDefaultDisposition.Approve)
            throw new ArgumentException("Silence cannot default to approval.", nameof(request));
        if (request.Kind == DecisionRequestKind.RiskApproval &&
            request.DefaultDisposition != DecisionDefaultDisposition.Deny)
        {
            throw new ArgumentException("Risk/approval decision requests must fail closed with a Deny default.", nameof(request));
        }

        if (request.ReuseScopeOptions.Count == 0)
            throw new ArgumentException("At least one reuse scope option is required.", nameof(request));
        if (request.AllowedActions.Count == 0)
            throw new ArgumentException("At least one opaque allowed action is required.", nameof(request));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in request.AllowedActions)
        {
            ValidateActionRef(action.ActionRef);
            if (!seen.Add(action.ActionRef.Value))
                throw new ArgumentException($"Duplicate decision action ref '{action.ActionRef.Value}'.", nameof(request));
            var required = DecisionAuthorization.RequiredTierFor(action.Kind);
            if (action.RequiredTier < required)
            {
                throw new ArgumentException(
                    $"Action '{action.ActionRef.Value}' requires at least {required}.",
                    nameof(request));
            }
        }
    }

    private static void ValidateActionRef(DecisionActionRef actionRef)
    {
        if (string.IsNullOrWhiteSpace(actionRef.Value))
            throw new ArgumentException("Decision action ref cannot be empty.", nameof(actionRef));
        if (actionRef.Value.Any(char.IsWhiteSpace) ||
            actionRef.Value.Any(char.IsControl) ||
            actionRef.Value.Contains("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Decision action refs must be opaque tokens, not wire-borne command strings.", nameof(actionRef));
        }
    }

    private static DecisionAllowedAction? FindAllowedAction(DecisionRequest request, DecisionActionRef actionRef) =>
        request.AllowedActions.FirstOrDefault(action =>
            string.Equals(action.ActionRef.Value, actionRef.Value, StringComparison.Ordinal));

    private static bool ActionMatchesDefaultDisposition(
        DecisionActionKind kind,
        DecisionDefaultDisposition disposition) =>
        (kind, disposition) switch
        {
            (DecisionActionKind.Deny, DecisionDefaultDisposition.Deny) => true,
            (DecisionActionKind.Park, DecisionDefaultDisposition.Park) => true,
            _ => false
        };

    private static async Task InsertDecisionRequestAsync(
        SqliteConnection conn,
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO collaboration_decision_requests (
                    id, kind, goal_id, subject, rendered_text, template_version,
                    evidence_manifest_json, evidence_manifest_hash, expires_at, default_disposition,
                    blocking_impact_json, reuse_scopes_json, created_at)
                VALUES (
                    $id, $kind, $goal_id, $subject, $rendered_text, $template_version,
                    $evidence_manifest_json, $evidence_manifest_hash, $expires_at, $default_disposition,
                    $blocking_impact_json, $reuse_scopes_json, $created_at)
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$kind", request.Kind.ToString());
            cmd.Parameters.AddWithValue("$goal_id", (object?)request.GoalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$subject", request.Subject);
            cmd.Parameters.AddWithValue("$rendered_text", request.RenderedText);
            cmd.Parameters.AddWithValue("$template_version", request.TemplateVersion);
            cmd.Parameters.AddWithValue("$evidence_manifest_json", Serialize(request.EvidenceManifest.Entries));
            cmd.Parameters.AddWithValue("$evidence_manifest_hash", request.EvidenceManifest.ManifestHash);
            cmd.Parameters.AddWithValue("$expires_at", request.ExpiresAt.ToString("O"));
            cmd.Parameters.AddWithValue("$default_disposition", request.DefaultDisposition.ToString());
            cmd.Parameters.AddWithValue("$blocking_impact_json", Serialize(request.BlockingImpact));
            cmd.Parameters.AddWithValue("$reuse_scopes_json", Serialize(request.ReuseScopeOptions));
            cmd.Parameters.AddWithValue("$created_at", request.CreatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var action in request.AllowedActions)
        {
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO collaboration_decision_request_actions (
                    request_id, action_ref, label, kind, required_tier, expires_at, expected_goal_state_version)
                VALUES (
                    $request_id, $action_ref, $label, $kind, $required_tier, $expires_at, $expected_goal_state_version)
                """;
            insert.Parameters.AddWithValue("$request_id", request.Id);
            insert.Parameters.AddWithValue("$action_ref", action.ActionRef.Value);
            insert.Parameters.AddWithValue("$label", action.Label);
            insert.Parameters.AddWithValue("$kind", action.Kind.ToString());
            insert.Parameters.AddWithValue("$required_tier", action.RequiredTier.ToString());
            insert.Parameters.AddWithValue("$expires_at", action.ExpiresAt.ToString("O"));
            insert.Parameters.AddWithValue("$expected_goal_state_version", (object?)action.ExpectedGoalStateVersion ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertDecisionReceiptAsync(
        SqliteConnection conn,
        DecisionReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collaboration_decision_receipts (
                id, request_id, rendered_text, template_version, evidence_manifest_json,
                evidence_manifest_hash, actor_id, channel, authentication_assurance,
                expected_goal_state_version, action_ref, response_value, selected_reuse_scope,
                permanent_policy_proposed, recorded_at)
            VALUES (
                $id, $request_id, $rendered_text, $template_version, $evidence_manifest_json,
                $evidence_manifest_hash, $actor_id, $channel, $authentication_assurance,
                $expected_goal_state_version, $action_ref, $response_value, $selected_reuse_scope,
                $permanent_policy_proposed, $recorded_at)
            """;
        cmd.Parameters.AddWithValue("$id", receipt.Id);
        cmd.Parameters.AddWithValue("$request_id", receipt.RequestId);
        cmd.Parameters.AddWithValue("$rendered_text", receipt.RenderedText);
        cmd.Parameters.AddWithValue("$template_version", receipt.TemplateVersion);
        cmd.Parameters.AddWithValue("$evidence_manifest_json", Serialize(receipt.EvidenceHashes));
        cmd.Parameters.AddWithValue("$evidence_manifest_hash", receipt.EvidenceManifestHash);
        cmd.Parameters.AddWithValue("$actor_id", receipt.ActorId);
        cmd.Parameters.AddWithValue("$channel", receipt.Channel);
        cmd.Parameters.AddWithValue("$authentication_assurance", receipt.AuthenticationAssurance.ToString());
        cmd.Parameters.AddWithValue("$expected_goal_state_version", (object?)receipt.ExpectedGoalStateVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$action_ref", receipt.Response.ActionRef.Value);
        cmd.Parameters.AddWithValue("$response_value", receipt.Response.Value);
        cmd.Parameters.AddWithValue("$selected_reuse_scope", receipt.Response.SelectedReuseScope.ToString());
        cmd.Parameters.AddWithValue("$permanent_policy_proposed", receipt.Response.PermanentPolicyProposed ? 1 : 0);
        cmd.Parameters.AddWithValue("$recorded_at", receipt.RecordedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEffectReceiptAsync(
        SqliteConnection conn,
        EffectReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collaboration_effect_receipts (
                id, request_id, decision_receipt_id, action_ref, status, expected_goal_state_version,
                actual_goal_state_version, result, recorded_at)
            VALUES (
                $id, $request_id, $decision_receipt_id, $action_ref, $status, $expected_goal_state_version,
                $actual_goal_state_version, $result, $recorded_at)
            """;
        cmd.Parameters.AddWithValue("$id", receipt.Id);
        cmd.Parameters.AddWithValue("$request_id", receipt.RequestId);
        cmd.Parameters.AddWithValue("$decision_receipt_id", receipt.DecisionReceiptId);
        cmd.Parameters.AddWithValue("$action_ref", receipt.ActionRef.Value);
        cmd.Parameters.AddWithValue("$status", receipt.Status.ToString());
        cmd.Parameters.AddWithValue("$expected_goal_state_version", (object?)receipt.ExpectedGoalStateVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$actual_goal_state_version", (object?)receipt.ActualGoalStateVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$result", receipt.Result);
        cmd.Parameters.AddWithValue("$recorded_at", receipt.RecordedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DecisionRequest?> TryReadDecisionRequestAsync(
        SqliteConnection conn,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, goal_id, subject, rendered_text, template_version,
                   evidence_manifest_json, evidence_manifest_hash, expires_at, default_disposition,
                   blocking_impact_json, reuse_scopes_json, created_at
            FROM collaboration_decision_requests
            WHERE id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var id = reader.GetString(0);
        var kind = Enum.Parse<DecisionRequestKind>(reader.GetString(1));
        var goalId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var subject = reader.GetString(3);
        var renderedText = reader.GetString(4);
        var templateVersion = reader.GetString(5);
        var entries = Deserialize<List<EvidenceManifestEntry>>(reader.GetString(6));
        var manifestHash = reader.GetString(7);
        var expiresAt = DateTimeOffset.Parse(reader.GetString(8));
        var defaultDisposition = Enum.Parse<DecisionDefaultDisposition>(reader.GetString(9));
        var blockingImpact = Deserialize<DecisionBlockingImpact>(reader.GetString(10));
        var reuseScopes = Deserialize<List<DecisionReuseScope>>(reader.GetString(11));
        var createdAt = DateTimeOffset.Parse(reader.GetString(12));
        await reader.DisposeAsync();

        var actions = await ReadDecisionActionsAsync(conn, id, cancellationToken);
        return new DecisionRequest(
            id,
            kind,
            goalId,
            subject,
            renderedText,
            templateVersion,
            new EvidenceManifest(entries, manifestHash),
            expiresAt,
            defaultDisposition,
            blockingImpact,
            reuseScopes,
            actions,
            createdAt);
    }

    private static async Task<IReadOnlyList<DecisionAllowedAction>> ReadDecisionActionsAsync(
        SqliteConnection conn,
        string requestId,
        CancellationToken cancellationToken)
    {
        var results = new List<DecisionAllowedAction>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT action_ref, label, kind, required_tier, expires_at, expected_goal_state_version
            FROM collaboration_decision_request_actions
            WHERE request_id = $request_id
            ORDER BY action_ref ASC
            """;
        cmd.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DecisionAllowedAction(
                new DecisionActionRef(reader.GetString(0)),
                reader.GetString(1),
                Enum.Parse<DecisionActionKind>(reader.GetString(2)),
                Enum.Parse<AuthorizationTier>(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }

        return results;
    }

    private static async Task<DecisionReceipt?> TryReadDecisionReceiptForRequestAsync(
        SqliteConnection conn,
        string requestId,
        EffectReceipt? effect,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM collaboration_decision_receipts WHERE request_id = $request_id LIMIT 1";
        cmd.Parameters.AddWithValue("$request_id", requestId);
        var id = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        return id is null ? null : await TryReadDecisionReceiptAsync(conn, id, effect, cancellationToken);
    }

    private static async Task<DecisionReceipt?> TryReadDecisionReceiptAsync(
        SqliteConnection conn,
        string receiptId,
        EffectReceipt? effect,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, request_id, rendered_text, template_version, evidence_manifest_json,
                   evidence_manifest_hash, actor_id, channel, authentication_assurance,
                   expected_goal_state_version, action_ref, response_value, selected_reuse_scope,
                   permanent_policy_proposed, recorded_at
            FROM collaboration_decision_receipts
            WHERE id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", receiptId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new DecisionReceipt(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Deserialize<List<EvidenceManifestEntry>>(reader.GetString(4)),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            Enum.Parse<AuthorizationTier>(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            new DecisionResponse(
                new DecisionActionRef(reader.GetString(10)),
                reader.GetString(11),
                Enum.Parse<DecisionReuseScope>(reader.GetString(12)),
                reader.GetInt32(13) != 0),
            DateTimeOffset.Parse(reader.GetString(14)),
            effect);
    }

    private static async Task<EffectReceipt?> TryReadEffectReceiptForRequestAsync(
        SqliteConnection conn,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, request_id, decision_receipt_id, action_ref, status, expected_goal_state_version,
                   actual_goal_state_version, result, recorded_at
            FROM collaboration_effect_receipts
            WHERE request_id = $request_id
            ORDER BY recorded_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEffectReceipt(reader) : null;
    }

    private static async Task<EffectReceipt?> TryReadEffectReceiptAsync(
        SqliteConnection conn,
        string requestId,
        string decisionReceiptId,
        DecisionActionRef actionRef,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, request_id, decision_receipt_id, action_ref, status, expected_goal_state_version,
                   actual_goal_state_version, result, recorded_at
            FROM collaboration_effect_receipts
            WHERE request_id = $request_id
              AND decision_receipt_id = $decision_receipt_id
              AND action_ref = $action_ref
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$request_id", requestId);
        cmd.Parameters.AddWithValue("$decision_receipt_id", decisionReceiptId);
        cmd.Parameters.AddWithValue("$action_ref", actionRef.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEffectReceipt(reader) : null;
    }

    private static EffectReceipt ReadEffectReceipt(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            new DecisionActionRef(reader.GetString(3)),
            Enum.Parse<EffectReceiptStatus>(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)));

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialize collaboration decision payload.");

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

    private static async Task<bool> HasOnlyUnconsumedDefaultActionsAsync(
        SqliteConnection conn,
        CollaborationItemType type,
        string correlationKey,
        CancellationToken cancellationToken)
    {
        var defaults = BuildDefaultActions(type, correlationKey);
        if (defaults is null || defaults.Count == 0)
            return false;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT action_index, label, command, requires_confirmation, requires_input, consumed_at
            FROM collaboration_item_actions
            WHERE correlation_key = $key
            ORDER BY action_index ASC
            """;
        cmd.Parameters.AddWithValue("$key", correlationKey);

        var rows = new List<(int Index, string Label, string Command, bool RequiresConfirmation, bool RequiresInput, string? ConsumedAt)>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        if (rows.Count != defaults.Count)
            return false;

        for (var i = 0; i < defaults.Count; i++)
        {
            var expected = NormalizeAction(defaults[i]);
            var actual = rows[i];
            if (actual.Index != i ||
                actual.ConsumedAt is not null ||
                !string.Equals(actual.Label, expected.Label, StringComparison.Ordinal) ||
                !string.Equals(actual.Command, expected.Command, StringComparison.Ordinal) ||
                actual.RequiresConfirmation != expected.RequiresConfirmation ||
                actual.RequiresInput != expected.RequiresInput)
            {
                return false;
            }
        }

        return true;
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
