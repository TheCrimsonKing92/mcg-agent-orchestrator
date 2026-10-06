using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CollaborationItemStore
{
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
            SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json
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
        CancellationToken cancellationToken = default,
        int? briefVersion = null)
    {
        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var answeredAt = DateTimeOffset.UtcNow;
                var resolvedAt = answeredAt.ToString("O");
                var answerHistory = new[]
                {
                    new HumanInputAnswerRecord(
                        Guid.NewGuid().ToString("n"),
                        resolution,
                        answeredAt,
                        BriefVersion: briefVersion)
                };
                await using var cmd = conn.CreateCommand();
                // Idempotent: only update if currently in a non-terminal state.
                cmd.CommandText = """
                    UPDATE collaboration_items
                    SET status = 'Resolved', resolved_at = $resolved_at, resolution = $resolution,
                        answer_history_json = $answer_history_json
                    WHERE correlation_key = $key
                      AND status NOT IN ('Resolved', 'Closed')
                    """;
                cmd.Parameters.AddWithValue("$resolved_at", resolvedAt);
                cmd.Parameters.AddWithValue("$resolution", resolution);
                cmd.Parameters.AddWithValue("$answer_history_json", Serialize(answerHistory));
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

    public async Task<bool> TryResolveByIdAsync(
        string itemId,
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
                var resolvedAt = DateTimeOffset.UtcNow;
                var answerHistory = new[]
                {
                    new HumanInputAnswerRecord(
                        Guid.NewGuid().ToString("n"),
                        resolution,
                        resolvedAt)
                };
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE collaboration_items
                    SET status = 'Resolved', resolved_at = $resolved_at, resolution = $resolution,
                        answer_history_json = $answer_history_json
                    WHERE id = $id
                      AND status NOT IN ('Resolved', 'Closed')
                    """;
                cmd.Parameters.AddWithValue("$resolved_at", resolvedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$resolution", resolution);
                cmd.Parameters.AddWithValue("$answer_history_json", Serialize(answerHistory));
                cmd.Parameters.AddWithValue("$id", itemId);
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

    public async Task<CollaborationItem> SupersedeClarificationAsync(
        string goalId,
        string itemId,
        string replacementAnswer,
        HumanInputAnswerOrigin origin,
        int? briefVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (origin != HumanInputAnswerOrigin.Operator)
            throw new UnauthorizedAccessException("Only operator-origin answers can supersede a clarification.");
        if (string.IsNullOrWhiteSpace(replacementAnswer))
            throw new ArgumentException("Replacement answer cannot be empty.", nameof(replacementAnswer));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                await using var read = conn.CreateCommand();
                read.CommandText = """
                    SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json
                    FROM collaboration_items
                    WHERE id = $id AND goal_id = $goal_id
                    """;
                read.Parameters.AddWithValue("$id", itemId);
                read.Parameters.AddWithValue("$goal_id", goalId);
                CollaborationItem item;
                await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                {
                    item = await reader.ReadAsync(cancellationToken)
                        ? ReadItem(reader)
                        : throw new KeyNotFoundException(
                            $"Clarification '{itemId}' was not found on goal '{goalId}'.");
                }

                if (item.Type != CollaborationItemType.Clarification ||
                    !CollaborationItemLifecycle.IsTerminal(item.Status) ||
                    string.IsNullOrWhiteSpace(item.Resolution))
                {
                    throw new InvalidOperationException(
                        $"Clarification '{itemId}' has no answered clarification to supersede.");
                }

                var history = (item.AnswerHistory ?? LegacyAnswerHistory(
                        item.Id,
                        item.Resolution,
                        item.ResolvedAt ?? item.RaisedAt))
                    .OrderBy(answer => answer.AnsweredAt)
                    .ToList();
                var priorIndex = history.FindLastIndex(answer => !answer.IsRetracted);
                if (priorIndex < 0)
                    throw new InvalidOperationException($"Clarification '{itemId}' has no authoritative answer.");

                var replacement = new HumanInputAnswerRecord(
                    Guid.NewGuid().ToString("n"),
                    replacementAnswer.Trim(),
                    DateTimeOffset.UtcNow,
                    origin,
                    BriefVersion: briefVersion);
                history[priorIndex] = history[priorIndex] with { SupersededByAnswerId = replacement.Id };
                history.Add(replacement);

                await using var update = conn.CreateCommand();
                update.CommandText = """
                    UPDATE collaboration_items
                    SET resolution = $resolution, resolved_at = $resolved_at, answer_history_json = $answer_history_json
                    WHERE id = $id AND goal_id = $goal_id
                    """;
                update.Parameters.AddWithValue("$resolution", replacement.Text);
                update.Parameters.AddWithValue("$resolved_at", replacement.AnsweredAt.ToString("O"));
                update.Parameters.AddWithValue("$answer_history_json", Serialize(history));
                update.Parameters.AddWithValue("$id", item.Id);
                update.Parameters.AddWithValue("$goal_id", goalId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException($"Clarification '{itemId}' changed while it was being superseded.");

                await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
                return item with
                {
                    Resolution = replacement.Text,
                    ResolvedAt = replacement.AnsweredAt,
                    AnswerHistory = history
                };
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

    public async Task<int> ResolveOpenForGoalRaisedAtOrBeforeAsync(
        string goalId,
        string resolution,
        DateTimeOffset raisedAtOrBefore,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Goal id cannot be empty.", nameof(goalId));

        return await WithBusyRetryAsync(async () =>
        {
            await using var conn = OpenConnection();
            await RunNonQueryAsync(conn, "PRAGMA busy_timeout=30000", cancellationToken);
            await RunNonQueryAsync(conn, "BEGIN IMMEDIATE", cancellationToken);
            try
            {
                var ids = new List<string>();
                await using (var select = conn.CreateCommand())
                {
                    select.CommandText = """
                        SELECT id, raised_at FROM collaboration_items
                        WHERE goal_id = $goal_id AND type IN ('Decision', 'Clarification', 'Verify')
                          AND status NOT IN ('Resolved', 'Closed')
                        """;
                    select.Parameters.AddWithValue("$goal_id", goalId);
                    await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        if (DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind) <= raisedAtOrBefore)
                            ids.Add(reader.GetString(0));
                }
                var rows = 0;
                var resolvedAt = DateTimeOffset.UtcNow.ToString("O");
                foreach (var id in ids)
                {
                    await using var update = conn.CreateCommand();
                    update.CommandText = """
                        UPDATE collaboration_items
                        SET status = 'Resolved', resolved_at = $resolved_at, resolution = $resolution
                        WHERE id = $id
                        """;
                    update.Parameters.AddWithValue("$resolved_at", resolvedAt);
                    update.Parameters.AddWithValue("$resolution", resolution);
                    update.Parameters.AddWithValue("$id", id);
                    rows += await update.ExecuteNonQueryAsync(cancellationToken);
                }
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
        var open = await ListOpenItemsAsync(cancellationToken);
        return CollaborationItemLifecycle.BuildAttentionQueue(open);
    }

    private async Task<IReadOnlyList<CollaborationItem>> ListOpenItemsAsync(
        CancellationToken cancellationToken)
    {
        await using var conn = OpenConnection();
        var results = new List<CollaborationItem>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json FROM collaboration_items WHERE status IN ('Raised', 'Delivered') ORDER BY raised_at ASC";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadItem(reader));
        return results;
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
            cmd.CommandText = "SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json FROM collaboration_items ORDER BY raised_at ASC";
        }
        else
        {
            cmd.CommandText = "SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json FROM collaboration_items WHERE goal_id = $goal_id ORDER BY raised_at ASC";
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
            SELECT id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json
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
            INSERT INTO collaboration_items (id, type, goal_id, status, subject, body, correlation_key, raised_at, resolved_at, resolution, answer_history_json)
            VALUES ($id, $type, $goal_id, $status, $subject, $body, $correlation_key, $raised_at, $resolved_at, $resolution, $answer_history_json)
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
        cmd.Parameters.AddWithValue("$answer_history_json", item.AnswerHistory is null ? DBNull.Value : Serialize(item.AnswerHistory));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
