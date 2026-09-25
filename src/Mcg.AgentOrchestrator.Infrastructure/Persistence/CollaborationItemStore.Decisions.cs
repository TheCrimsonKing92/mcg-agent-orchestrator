using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CollaborationItemStore
{
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

    public async Task<IReadOnlyList<NotificationDelivery>> ListNotificationDeliveriesAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Notification delivery request id cannot be empty.", nameof(requestId));

        await using var conn = OpenConnection();
        var results = new List<NotificationDelivery>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, request_id, channel, target, content_hash, delivered_at
            FROM collaboration_notification_deliveries
            WHERE request_id = $request_id
            ORDER BY delivered_at ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ReadNotificationDelivery(reader));
        return results;
    }

    public async Task<DecisionReceipt> RecordDecisionAsync(
        string requestId,
        string actorId,
        string channel,
        AuthorizationTier authenticationAssurance,
        long? expectedGoalStateVersion,
        DecisionResponse response,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default) =>
        await RecordDecisionAsync(requestId, actorId, channel, authenticationAssurance,
            expectedGoalStateVersion, response, recordedAt, null, null, cancellationToken);

    public async Task<DecisionReceipt> RecordDecisionAsync(
        string requestId,
        string actorId,
        string channel,
        AuthorizationTier authenticationAssurance,
        long? expectedGoalStateVersion,
        DecisionResponse response,
        DateTimeOffset recordedAt,
        DecisionReversibility? reversibility,
        string? precedentRef,
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
                    null,
                    reversibility,
                    precedentRef);
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
        CancellationToken cancellationToken = default) =>
        await TryApplyDecisionEffectAsync(
            requestId,
            decisionReceiptId,
            actionRef,
            currentGoalStateVersion,
            result,
            appliedAt,
            rejectionReason: null,
            cancellationToken);

    public async Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(
        string requestId,
        string decisionReceiptId,
        DecisionActionRef actionRef,
        long? currentGoalStateVersion,
        string result,
        DateTimeOffset appliedAt,
        string? rejectionReason,
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
                var expectedGoalStateVersion = receipt.ExpectedGoalStateVersion ?? action?.ExpectedGoalStateVersion;
                var rejection = rejectionReason ?? ValidateDecisionEffectApply(
                    requestId,
                    receipt,
                    action,
                    actionRef,
                    expectedGoalStateVersion,
                    currentGoalStateVersion,
                    appliedAt);

                var effect = new EffectReceipt(
                    Guid.NewGuid().ToString("n"),
                    requestId,
                    decisionReceiptId,
                    actionRef,
                    rejection is null ? EffectReceiptStatus.Applied : EffectReceiptStatus.Rejected,
                    expectedGoalStateVersion,
                    currentGoalStateVersion,
                    rejection ?? result,
                    appliedAt,
                    rejection is null ? result : $"refused {rejection}");
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

    private static string? ValidateDecisionEffectApply(
        string requestId,
        DecisionReceipt receipt,
        DecisionAllowedAction? action,
        DecisionActionRef actionRef,
        long? expectedGoalStateVersion,
        long? currentGoalStateVersion,
        DateTimeOffset appliedAt)
    {
        if (action is null)
            return "Unknown opaque action reference.";
        if (!string.Equals(receipt.RequestId, requestId, StringComparison.Ordinal))
            return "Decision receipt does not belong to this request.";
        if (!string.Equals(receipt.Response.ActionRef.Value, actionRef.Value, StringComparison.Ordinal))
            return "Action ref does not match the recorded decision.";
        if (!DecisionAuthorization.Meets(receipt.AuthenticationAssurance, action.RequiredTier))
            return $"Authentication assurance '{receipt.AuthenticationAssurance}' does not satisfy required tier '{action.RequiredTier}'.";
        if (receipt.ExpectedGoalStateVersion is not null &&
            action.ExpectedGoalStateVersion is not null &&
            receipt.ExpectedGoalStateVersion != action.ExpectedGoalStateVersion)
        {
            return "Receipt expected goal state version does not match the action binding.";
        }

        if (expectedGoalStateVersion is not null && currentGoalStateVersion != expectedGoalStateVersion)
            return "Stale goal state version.";
        if (appliedAt > action.ExpiresAt)
            return "Action expired.";

        return null;
    }

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
                permanent_policy_proposed, recorded_at, reversibility, precedent_ref)
            VALUES (
                $id, $request_id, $rendered_text, $template_version, $evidence_manifest_json,
                $evidence_manifest_hash, $actor_id, $channel, $authentication_assurance,
                $expected_goal_state_version, $action_ref, $response_value, $selected_reuse_scope,
                $permanent_policy_proposed, $recorded_at, $reversibility, $precedent_ref)
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
        cmd.Parameters.AddWithValue("$reversibility", (object?)receipt.Reversibility?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$precedent_ref", (object?)receipt.PrecedentRef ?? DBNull.Value);
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
                actual_goal_state_version, result, recorded_at, outcome)
            VALUES (
                $id, $request_id, $decision_receipt_id, $action_ref, $status, $expected_goal_state_version,
                $actual_goal_state_version, $result, $recorded_at, $outcome)
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
        cmd.Parameters.AddWithValue("$outcome", (object?)receipt.Outcome ?? DBNull.Value);
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
                   permanent_policy_proposed, recorded_at, reversibility, precedent_ref
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
            effect,
            reader.IsDBNull(15) ? null : Enum.Parse<DecisionReversibility>(reader.GetString(15)),
            reader.IsDBNull(16) ? null : reader.GetString(16));
    }

    private static async Task<EffectReceipt?> TryReadEffectReceiptForRequestAsync(
        SqliteConnection conn,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, request_id, decision_receipt_id, action_ref, status, expected_goal_state_version,
                   actual_goal_state_version, result, recorded_at, outcome
            FROM collaboration_effect_receipts
            WHERE request_id = $request_id
            ORDER BY CASE status WHEN 'Applied' THEN 0 ELSE 1 END, recorded_at DESC
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
                   actual_goal_state_version, result, recorded_at, outcome
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
}
