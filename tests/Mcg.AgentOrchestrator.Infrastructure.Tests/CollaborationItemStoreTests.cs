using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CollaborationItemStoreTests
{
    // --- Basic CRUD ---

    [Xunit.Fact(DisplayName = "CollaborationItemStore_raise_creates_item_in_Raised_status")]
    public async Task RaiseCreatesItemInRaisedStatus()
    {
        var store = new CollaborationItemStore(DbPath());

        var item = await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc", "subject", "body");

        Xunit.Assert.NotNull(item.Id);
        Xunit.Assert.Equal(CollaborationItemType.Decision, item.Type);
        Xunit.Assert.Equal("goal-abc", item.GoalId);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Xunit.Assert.Equal("subject", item.Subject);
        Xunit.Assert.Equal("body", item.Body);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_raise_with_correlation_key")]
    public async Task RaiseWithCorrelationKeyStoredAndQueryable()
    {
        var store = new CollaborationItemStore(DbPath());

        var item = await store.RaiseAsync(
            CollaborationItemType.Decision, null, "Landing conflict", "merge failed",
            correlationKey: "inbox-deadbeef1234");

        Xunit.Assert.Equal("inbox-deadbeef1234", item.CorrelationKey);
        var listed = await store.ListAsync();
        Xunit.Assert.Single(listed);
        Xunit.Assert.Equal("inbox-deadbeef1234", listed[0].CorrelationKey);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_raise_is_idempotent_on_correlation_key_while_open")]
    public async Task RaiseIsIdempotentOnCorrelationKeyWhileOpen()
    {
        var store = new CollaborationItemStore(DbPath());

        var first = await store.RaiseAsync(CollaborationItemType.Decision, "goal-x", "Acceptance failed", "body v1", "corr-dup");
        var second = await store.RaiseAsync(CollaborationItemType.Decision, "goal-x", "Acceptance failed", "body v2", "corr-dup");

        // No duplicate while one is pending: still ONE item, same id, body refreshed to the latest raise.
        var listed = await store.ListAsync();
        Xunit.Assert.Single(listed);
        Xunit.Assert.Equal(first.Id, second.Id);
        Xunit.Assert.Equal("body v2", listed[0].Body);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_raise_after_resolve_creates_a_new_item")]
    public async Task RaiseAfterResolveCreatesNewItem()
    {
        var store = new CollaborationItemStore(DbPath());

        var first = await store.RaiseAsync(CollaborationItemType.Decision, "goal-x", "Issue", "body", "corr-reraise");
        await store.TryResolveAsync("corr-reraise", "resolved");
        var second = await store.RaiseAsync(CollaborationItemType.Decision, "goal-x", "Issue again", "body2", "corr-reraise");

        // Idempotency applies only while OPEN; a fresh issue after resolution is legitimately a new item.
        Xunit.Assert.NotEqual(first.Id, second.Id);
        Xunit.Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_list_returns_all_items")]
    public async Task ListReturnsAllItems()
    {
        var store = new CollaborationItemStore(DbPath());

        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "s1", "b1");
        await store.RaiseAsync(CollaborationItemType.Clarification, "g2", "s2", "b2");

        var all = await store.ListAsync();
        Xunit.Assert.Equal(2, all.Count);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_list_filters_by_goal_id")]
    public async Task ListFiltersByGoalId()
    {
        var store = new CollaborationItemStore(DbPath());

        await store.RaiseAsync(CollaborationItemType.Decision, "goal-aaa", "s1", "b1");
        await store.RaiseAsync(CollaborationItemType.Clarification, "goal-bbb", "s2", "b2");

        var filtered = await store.ListAsync("goal-aaa");
        Xunit.Assert.Single(filtered);
        Xunit.Assert.Equal("goal-aaa", filtered[0].GoalId);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_list_filters_by_goal_ids")]
    public async Task ListFiltersByGoalIds()
    {
        var store = new CollaborationItemStore(DbPath());

        await store.RaiseAsync(CollaborationItemType.Decision, "goal-aaa", "s1", "b1");
        await store.RaiseAsync(CollaborationItemType.Clarification, "goal-bbb", "s2", "b2");
        await store.RaiseAsync(CollaborationItemType.Verify, "goal-ccc", "s3", "b3");

        var filtered = await store.ListForGoalIdsAsync(["goal-aaa", "goal-bbb", "goal-missing"]);

        Xunit.Assert.Equal(["goal-aaa", "goal-bbb"], filtered.Select(item => item.GoalId).ToArray());
    }

    // --- Resolution ---

    [Xunit.Fact(DisplayName = "CollaborationItemStore_resolve_transitions_item_to_Resolved")]
    public async Task ResolveSetsItemToResolved()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "s", "b", "corr-key-1");

        var resolved = await store.TryResolveAsync("corr-key-1", "land g1prefix");

        Xunit.Assert.True(resolved);
        var items = await store.ListAsync();
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, items[0].Status);
        Xunit.Assert.Equal("land g1prefix", items[0].Resolution);
        Xunit.Assert.NotNull(items[0].ResolvedAt);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_resolve_is_idempotent_returns_false_on_second_call")]
    public async Task ResolveIsIdempotent()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "s", "b", "corr-key-2");

        var firstResult = await store.TryResolveAsync("corr-key-2", "land g1prefix");
        var secondResult = await store.TryResolveAsync("corr-key-2", "land g1prefix");

        Xunit.Assert.True(firstResult);
        Xunit.Assert.False(secondResult);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_resolve_unknown_key_returns_false")]
    public async Task ResolveUnknownKeyReturnsFalse()
    {
        var store = new CollaborationItemStore(DbPath());

        var result = await store.TryResolveAsync("no-such-key", "some command");

        Xunit.Assert.False(result);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_bound_actions_are_one_use_and_idempotent_by_interaction")]
    public async Task BoundActionsAreOneUseAndIdempotentByInteraction()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-action-1",
            [new CollaborationActionBinding("Run", "next abc123")]);

        var first = await store.TryClaimActionAsync(
            "corr-action-1", 0, "discord:user1", "interaction-1", null, DateTimeOffset.UtcNow);
        var duplicate = await store.TryClaimActionAsync(
            "corr-action-1", 0, "discord:user1", "interaction-1", null, DateTimeOffset.UtcNow);
        var secondInteraction = await store.TryClaimActionAsync(
            "corr-action-1", 0, "discord:user1", "interaction-2", null, DateTimeOffset.UtcNow);

        Xunit.Assert.True(first.Applied);
        Xunit.Assert.True(duplicate.Duplicate);
        Xunit.Assert.False(secondInteraction.Applied);
        Xunit.Assert.Equal("Action already consumed.", secondInteraction.ErrorMessage);
        Xunit.Assert.Equal(2, (await store.ListDecisionAuditAsync()).Count);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_list_actions_excludes_consumed_and_expired_actions")]
    public async Task ListActionsExcludesConsumedAndExpiredActions()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-open-actions",
            [
                new CollaborationActionBinding("Run", "next abc123"),
                new CollaborationActionBinding("Expired", "next expired", ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1))
            ]);

        var before = await store.ListActionsAsync("corr-open-actions");
        await store.TryClaimActionAsync(
            "corr-open-actions", 0, "discord:user1", "interaction-consume", null, DateTimeOffset.UtcNow);
        var after = await store.ListActionsAsync("corr-open-actions");

        Xunit.Assert.Equal(["next abc123"], before.Select(action => action.Command).ToArray());
        Xunit.Assert.Empty(after);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_open_item_refresh_preserves_consumed_actions")]
    public async Task OpenItemRefreshPreservesConsumedActions()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-refresh-consumed",
            [new CollaborationActionBinding("Run", "next abc123")]);
        var first = await store.TryClaimActionAsync(
            "corr-refresh-consumed", 0, "discord:user1", "interaction-1", null, DateTimeOffset.UtcNow);

        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s refreshed",
            "b refreshed",
            "corr-refresh-consumed",
            [new CollaborationActionBinding("Run again", "next resurrected")]);
        var second = await store.TryClaimActionAsync(
            "corr-refresh-consumed", 0, "discord:user1", "interaction-2", null, DateTimeOffset.UtcNow);

        Xunit.Assert.True(first.Applied);
        Xunit.Assert.False(second.Applied);
        Xunit.Assert.Equal("Action already consumed.", second.ErrorMessage);
        Xunit.Assert.Equal("next abc123", second.Action!.Command);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_open_item_refresh_preserves_unconsumed_actions")]
    public async Task OpenItemRefreshPreservesUnconsumedActions()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-refresh-open",
            [new CollaborationActionBinding("Run", "next abc123")]);

        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s refreshed",
            "b refreshed",
            "corr-refresh-open",
            [new CollaborationActionBinding("Run changed", "next rebound")]);

        var actions = await store.ListActionsAsync("corr-refresh-open");
        var result = await store.TryClaimActionAsync(
            "corr-refresh-open", 0, "discord:user1", "interaction-open-refresh", null, DateTimeOffset.UtcNow);

        Xunit.Assert.Equal(["next abc123"], actions.Select(action => action.Command).ToArray());
        Xunit.Assert.True(result.Applied);
        Xunit.Assert.Equal("next abc123", result.Action!.Command);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_explicit_actions_replace_unconsumed_default_fallback")]
    public async Task ExplicitActionsReplaceUnconsumedDefaultFallback()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-default-then-explicit");

        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s refreshed",
            "b refreshed",
            "corr-default-then-explicit",
            [new CollaborationActionBinding("Accept Goal", "acceptance abc12345 --autonomy supervised-auto", RequiresConfirmation: true)]);

        var action = (await store.ListActionsAsync("corr-default-then-explicit")).Single();
        Xunit.Assert.Equal("Accept Goal", action.Label);
        Xunit.Assert.Equal("acceptance abc12345 --autonomy supervised-auto", action.Command);
        Xunit.Assert.True(action.RequiresConfirmation);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_bound_action_expiry_rejects_and_audits")]
    public async Task BoundActionExpiryRejectsAndAudits()
    {
        var store = new CollaborationItemStore(DbPath());
        var expiresAt = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-expired",
            [new CollaborationActionBinding("Run", "next abc123", ExpiresAt: expiresAt)]);

        var result = await store.TryClaimActionAsync(
            "corr-expired", 0, "discord:user1", "interaction-expired", null, expiresAt.AddSeconds(1));

        Xunit.Assert.False(result.Applied);
        Xunit.Assert.Equal("Action expired.", result.ErrorMessage);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Xunit.Assert.Equal("Rejected", audit.Outcome);
        Xunit.Assert.Equal("Action expired.", audit.RejectionReason);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_stale_goal_state_version_rejects_and_audits")]
    public async Task StaleGoalStateVersionRejectsAndAudits()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-stale",
            [new CollaborationActionBinding("Run", "acceptance abc123", ExpectedGoalStateVersion: 7)]);

        var result = await store.TryClaimActionAsync(
            "corr-stale", 0, "discord:user1", "interaction-stale", 8, DateTimeOffset.UtcNow);

        Xunit.Assert.False(result.Applied);
        Xunit.Assert.Equal("Stale goal state version.", result.ErrorMessage);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Xunit.Assert.Equal(7, audit.ExpectedGoalStateVersion);
        Xunit.Assert.Equal(8, audit.ActualGoalStateVersion);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_decision_audit_records_rendered_content_hash")]
    public async Task DecisionAuditRecordsRenderedContentHash()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-hash",
            [new CollaborationActionBinding("Run", "next abc123")]);
        await store.UpdateRenderedContentHashAsync(["corr-hash"], "sha256-card");

        var result = await store.TryClaimActionAsync(
            "corr-hash", 0, "discord:user1", "interaction-hash", null, DateTimeOffset.UtcNow);

        Xunit.Assert.True(result.Applied);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Xunit.Assert.Equal("discord:user1", audit.ActorId);
        Xunit.Assert.Equal("interaction-hash", audit.InteractionId);
        Xunit.Assert.Equal("sha256-card", audit.RenderedContentHash);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_rejected_decision_audit_records_rendered_content_hash")]
    public async Task RejectedDecisionAuditRecordsRenderedContentHash()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-reject-hash",
            [new CollaborationActionBinding("Run", "next abc123")]);
        await store.UpdateRenderedContentHashAsync(["corr-reject-hash"], "sha256-card");

        await store.RecordRejectedDecisionAsync(
            "corr-reject-hash",
            0,
            "discord:intruder",
            "interaction-reject-hash",
            "User 'intruder' is not in the operator allowlist.",
            DateTimeOffset.UtcNow);

        var audit = (await store.ListDecisionAuditAsync()).Single();
        Xunit.Assert.Equal("Rejected", audit.Outcome);
        Xunit.Assert.Equal("sha256-card", audit.RenderedContentHash);
        Xunit.Assert.Equal("User 'intruder' is not in the operator allowlist.", audit.RejectionReason);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_default_decision_and_verify_actions_are_cli_commands")]
    public async Task DefaultDecisionAndVerifyActionsAreCliCommands()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "decision", "body", "corr-default-decision");
        await store.RaiseAsync(CollaborationItemType.Verify, "g1", "verify", "body", "corr-default-verify");

        var decision = (await store.ListActionsAsync("corr-default-decision")).Single();
        var verify = (await store.ListActionsAsync("corr-default-verify")).Single();

        Xunit.Assert.Equal("operator-inbox-ack corr-default-decision", decision.Command);
        Xunit.Assert.Equal("operator-inbox-ack corr-default-verify", verify.Command);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_allowlist_accepts_existing_operator_inbox_verbs")]
    public async Task AllowlistAcceptsExistingOperatorInboxVerbs()
    {
        var store = new CollaborationItemStore(DbPath());

        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "g1",
            "s",
            "b",
            "corr-verbs",
            [
                new CollaborationActionBinding("Verify", "verify-needed abc12345"),
                new CollaborationActionBinding("Workspace", "workspace create abc12345"),
                new CollaborationActionBinding("Readiness", "readiness abc12345"),
                new CollaborationActionBinding("Re-delegate", "re-delegate 2 --autonomy safe-auto"),
                new CollaborationActionBinding("Agent Add", "agent-add Developer <provider> <model>")
            ]);

        var actions = await store.ListActionsAsync("corr-verbs");
        Xunit.Assert.Equal(
            [
                "verify-needed abc12345",
                "workspace create abc12345",
                "readiness abc12345",
                "re-delegate 2 --autonomy safe-auto",
                "agent-add Developer <provider> <model>"
            ],
            actions.Select(action => action.Command).ToArray());
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_mark_delivered_transitions_Raised_to_Delivered")]
    public async Task MarkDeliveredTransitionsRaisedToDelivered()
    {
        var store = new CollaborationItemStore(DbPath());
        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "s", "b", "corr-delivered-1");

        var delivered = await store.TryMarkDeliveredAsync("corr-delivered-1");

        Xunit.Assert.True(delivered);
        var items = await store.ListAsync();
        Xunit.Assert.Equal(CollaborationItemStatus.Delivered, items[0].Status);
    }

    // --- Attention queue ---

    [Xunit.Fact(DisplayName = "CollaborationItemStore_attention_queue_excludes_intake_types_and_terminal_items")]
    public async Task AttentionQueueExcludesIntakeTypesAndTerminalItems()
    {
        var store = new CollaborationItemStore(DbPath());

        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "open decision", "b", "ck-1");
        await store.RaiseAsync(CollaborationItemType.Notice, "g1", "notice", "b");
        await store.RaiseAsync(CollaborationItemType.Capture, "g1", "capture", "b");
        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "resolved decision", "b", "ck-2");
        await store.TryResolveAsync("ck-2", "done");

        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Single(queue);
        Xunit.Assert.Equal("open decision", queue[0].Subject);
    }

    [Xunit.Fact(DisplayName = "CollaborationItemStore_attention_queue_ordered_Decision_Clarification_Verify")]
    public async Task AttentionQueueOrdered()
    {
        var store = new CollaborationItemStore(DbPath());

        await store.RaiseAsync(CollaborationItemType.Verify, "g1", "verify-item", "b");
        await store.RaiseAsync(CollaborationItemType.Clarification, "g1", "clarification-item", "b");
        await store.RaiseAsync(CollaborationItemType.Decision, "g1", "decision-item", "b");

        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Equal(3, queue.Count);
        Xunit.Assert.Equal(CollaborationItemType.Decision, queue[0].Type);
        Xunit.Assert.Equal(CollaborationItemType.Clarification, queue[1].Type);
        Xunit.Assert.Equal(CollaborationItemType.Verify, queue[2].Type);
    }

    // --- Producer+consumer round-trip with fake store seam ---

    [Xunit.Fact(DisplayName = "RoundTrip_landing_escalation_raises_item_and_decision_resolves_it")]
    public async Task LandingEscalationRaisesItemAndDecisionResolvesIt()
    {
        var fakeStore = new FakeCollaborationItemStore();
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Test round-trip goal");

        // Producer: landing escalation raises a Decision item.
        OperatorInbox.RecordLandingEscalation(
            workspace, goal, "merge conflict", "integration",
            channel: null, collaborationStore: fakeStore);

        Xunit.Assert.Single(fakeStore.Items);
        var raised = fakeStore.Items[0];
        Xunit.Assert.Equal(CollaborationItemType.Decision, raised.Type);
        Xunit.Assert.Equal(goal.Id.Value, raised.GoalId);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, raised.Status);
        Xunit.Assert.NotNull(raised.CorrelationKey);

        // Consumer: applying the decision (keyed by correlationKey = inboxItemId) resolves it.
        var resolved = await fakeStore.TryResolveAsync(raised.CorrelationKey!, $"land {goal.Id.Value[..8]}");

        Xunit.Assert.True(resolved);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, fakeStore.Items[0].Status);
    }

    [Xunit.Fact(DisplayName = "RoundTrip_resolve_is_idempotent_on_fake_store")]
    public async Task ResolveIsIdempotentOnFakeStore()
    {
        var fakeStore = new FakeCollaborationItemStore();
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Test idempotent resolve");

        OperatorInbox.RecordLandingEscalation(
            workspace, goal, "conflict", "integration",
            channel: null, collaborationStore: fakeStore);

        var correlationKey = fakeStore.Items[0].CorrelationKey!;
        var first = await fakeStore.TryResolveAsync(correlationKey, "land prefix");
        var second = await fakeStore.TryResolveAsync(correlationKey, "land prefix");

        Xunit.Assert.True(first);
        Xunit.Assert.False(second);
    }

    [Xunit.Fact(DisplayName = "LandingEscalation_collaboration_timeout_preserves_json_record")]
    public void LandingEscalationCollaborationTimeoutPreservesJsonRecord()
    {
        using var raiseStarted = new ManualResetEventSlim();
        var pendingRaise = new TaskCompletionSource<CollaborationItem>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeStore = new FakeCollaborationItemStore
        {
            RaiseStarted = raiseStarted,
            PendingRaise = pendingRaise.Task
        };
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Escalation timeout keeps durable fallback");

        var record = Task.Run(() => OperatorInbox.RecordLandingEscalation(
            workspace,
            goal,
            "merge conflict",
            "integration",
            channel: null,
            collaborationStore: fakeStore,
            collaborationRaiseTimeout: TimeSpan.FromMilliseconds(25)));

        Xunit.Assert.True(raiseStarted.Wait(TimeSpan.FromSeconds(1)));
        Xunit.Assert.True(record.Wait(TimeSpan.FromSeconds(1)));
        var jsonPath = Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json");
        Xunit.Assert.True(File.Exists(jsonPath));
        Xunit.Assert.Contains(goal.Id.Value, File.ReadAllText(jsonPath), StringComparison.Ordinal);
    }

    private static string DbPath() =>
        Path.Combine(CreateTempDirectory(), "collab.db");
}

internal sealed class FakeCollaborationItemStore : ICollaborationItemStore
{
    private readonly List<CollaborationItem> _items = [];
    private readonly Dictionary<string, List<CollaborationBoundAction>> _actions = new(StringComparer.Ordinal);
    private readonly List<CollaborationDecisionAuditEntry> _audits = [];
    private readonly Dictionary<string, DecisionRequest> _decisionRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecisionReceipt> _decisionReceipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EffectReceipt> _effectReceipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NotificationDelivery> _notificationDeliveries = new(StringComparer.Ordinal);

    public IReadOnlyList<CollaborationItem> Items => _items;
    public ManualResetEventSlim? RaiseStarted { get; init; }
    public Task<CollaborationItem>? PendingRaise { get; init; }

    public Task<CollaborationItem> RaiseAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey = null,
        CancellationToken cancellationToken = default)
    {
        RaiseStarted?.Set();
        if (PendingRaise is not null)
        {
            return PendingRaise;
        }

        var item = new CollaborationItem(
            Guid.NewGuid().ToString("n"),
            type, goalId, CollaborationItemStatus.Raised,
            subject, body, correlationKey, DateTimeOffset.UtcNow, null, null);
        _items.Add(item);
        return Task.FromResult(item);
    }

    public Task<CollaborationItem> RaiseWithActionsAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string correlationKey,
        IReadOnlyList<CollaborationActionBinding> actions,
        CancellationToken cancellationToken = default)
    {
        var item = new CollaborationItem(
            Guid.NewGuid().ToString("n"),
            type, goalId, CollaborationItemStatus.Raised,
            subject, body, correlationKey, DateTimeOffset.UtcNow, null, null);
        _items.Add(item);
        _actions[correlationKey] = actions.Select((action, index) => new CollaborationBoundAction(
            correlationKey,
            index,
            action.Label,
            action.Command,
            action.RequiresConfirmation,
            action.RequiresInput,
            action.ExpectedGoalStateVersion,
            action.ExpiresAt ?? DateTimeOffset.UtcNow.AddHours(12),
            null,
            null)).ToList();
        return Task.FromResult(item);
    }

    public Task<bool> TryResolveAsync(
        string correlationKey,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.CorrelationKey == correlationKey && !CollaborationItemLifecycle.IsTerminal(item.Status))
            {
                _items[i] = item with { Status = CollaborationItemStatus.Resolved, Resolution = resolution, ResolvedAt = DateTimeOffset.UtcNow };
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    public Task<int> ResolveOpenForGoalAsync(
        string goalId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        var resolved = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.GoalId == goalId &&
                CollaborationItemLifecycle.IsReachUpType(item.Type) &&
                !CollaborationItemLifecycle.IsTerminal(item.Status))
            {
                _items[i] = item with { Status = CollaborationItemStatus.Resolved, Resolution = resolution, ResolvedAt = DateTimeOffset.UtcNow };
                resolved++;
            }
        }

        return Task.FromResult(resolved);
    }

    public Task<bool> TryMarkDeliveredAsync(
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.CorrelationKey == correlationKey && item.Status == CollaborationItemStatus.Raised)
            {
                _items[i] = item with { Status = CollaborationItemStatus.Delivered };
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<CollaborationItem>> GetAttentionQueueAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CollaborationItemLifecycle.BuildAttentionQueue(_items));

    public Task<IReadOnlyList<CollaborationItem>> ListAsync(string? goalId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollaborationItem>>(
            goalId is null ? _items : _items.Where(i => i.GoalId == goalId).ToList());

    public Task<IReadOnlyList<CollaborationItem>> ListForGoalIdsAsync(
        IEnumerable<string> goalIds,
        CancellationToken cancellationToken = default)
    {
        var scopedGoalIds = goalIds.ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<CollaborationItem>>(
            _items.Where(item => item.GoalId is not null && scopedGoalIds.Contains(item.GoalId)).ToList());
    }

    public Task<IReadOnlyList<CollaborationBoundAction>> ListActionsAsync(
        string correlationKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollaborationBoundAction>>(
            _actions.TryGetValue(correlationKey, out var actions) ? actions : []);

    public Task UpdateRenderedContentHashAsync(
        IEnumerable<string> correlationKeys,
        string renderedContentHash,
        CancellationToken cancellationToken = default)
    {
        foreach (var key in correlationKeys)
        {
            if (!_actions.TryGetValue(key, out var actions))
                continue;
            _actions[key] = actions.Select(action => action with { RenderedContentHash = renderedContentHash }).ToList();
        }

        return Task.CompletedTask;
    }

    public Task<CollaborationActionApplyResult> TryClaimActionAsync(
        string correlationKey,
        int actionIndex,
        string actorId,
        string interactionId,
        long? currentGoalStateVersion,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = _audits.FirstOrDefault(audit => audit.InteractionId == interactionId);
        if (existing is not null)
            return Task.FromResult(new CollaborationActionApplyResult(false, true, null, existing, "Duplicate interaction."));

        var action = _actions.TryGetValue(correlationKey, out var actions)
            ? actions.FirstOrDefault(candidate => candidate.ActionIndex == actionIndex)
            : null;
        if (action is null)
        {
            var audit = AddAudit(correlationKey, actionIndex, actorId, interactionId, "Rejected", null, "Unknown action reference.", null, currentGoalStateVersion, null, decidedAt);
            return Task.FromResult(new CollaborationActionApplyResult(false, false, null, audit, audit.RejectionReason));
        }

        var appliedAudit = AddAudit(correlationKey, actionIndex, actorId, interactionId, "Applied", action.Command, null, action.ExpectedGoalStateVersion, currentGoalStateVersion, action.RenderedContentHash, decidedAt);
        _actions[correlationKey] = actions.Select(candidate =>
            candidate.ActionIndex == actionIndex ? candidate with { ConsumedAt = decidedAt } : candidate).ToList();
        return Task.FromResult(new CollaborationActionApplyResult(true, false, action, appliedAudit, null));
    }

    public Task<CollaborationDecisionAuditEntry> RecordRejectedDecisionAsync(
        string correlationKey,
        int? actionIndex,
        string actorId,
        string interactionId,
        string reason,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(AddAudit(correlationKey, actionIndex, actorId, interactionId, "Rejected", null, reason, null, null, null, decidedAt));

    public Task<IReadOnlyList<CollaborationDecisionAuditEntry>> ListDecisionAuditAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CollaborationDecisionAuditEntry>>(_audits);

    public Task<DecisionRequest> RaiseDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        _decisionRequests.TryAdd(request.Id, request);
        return Task.FromResult(_decisionRequests[request.Id]);
    }

    public Task<DecisionState?> GetDecisionStateAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        if (!_decisionRequests.TryGetValue(requestId, out var request))
            return Task.FromResult<DecisionState?>(null);

        var receipt = _decisionReceipts.Values.FirstOrDefault(receipt => receipt.RequestId == requestId);
        var effect = _effectReceipts.Values.FirstOrDefault(effect => effect.RequestId == requestId);
        return Task.FromResult<DecisionState?>(DecisionState.Create(
            request,
            receipt is null ? null : receipt with { EffectResult = effect },
            effect));
    }

    public Task<IReadOnlyList<DecisionRequest>> ListDecisionRequestsAsync(
        string? goalId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DecisionRequest>>(
            _decisionRequests.Values.Where(request => goalId is null || request.GoalId == goalId).ToList());

    public Task<NotificationDelivery> RecordNotificationDeliveryAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_notificationDeliveries.TryAdd(delivery.Id, delivery) ? delivery : _notificationDeliveries[delivery.Id]);

    public Task<IReadOnlyList<NotificationDelivery>> ListNotificationDeliveriesAsync(
        string requestId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NotificationDelivery>>(
            _notificationDeliveries.Values.Where(delivery => delivery.RequestId == requestId).ToList());

    public Task<DecisionReceipt> RecordDecisionAsync(
        string requestId,
        string actorId,
        string channel,
        AuthorizationTier authenticationAssurance,
        long? expectedGoalStateVersion,
        DecisionResponse response,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        var request = _decisionRequests[requestId];
        var existing = _decisionReceipts.Values.FirstOrDefault(receipt => receipt.RequestId == requestId);
        if (existing is not null)
            return Task.FromResult(existing);

        var receipt = new DecisionReceipt(
            Guid.NewGuid().ToString("n"),
            requestId,
            request.RenderedText,
            request.TemplateVersion,
            request.EvidenceManifest.Entries,
            request.EvidenceManifest.ManifestHash,
            actorId,
            channel,
            authenticationAssurance,
            expectedGoalStateVersion,
            response,
            recordedAt,
            null);
        _decisionReceipts[receipt.Id] = receipt;
        return Task.FromResult(receipt);
    }

    public Task<DecisionReceipt> RecordExpiredDefaultDispositionAsync(
        string requestId,
        DateTimeOffset expiredAt,
        CancellationToken cancellationToken = default)
    {
        var request = _decisionRequests[requestId];
        var response = new DecisionResponse(new DecisionActionRef("expired-default"), $"default:{request.DefaultDisposition}", DecisionReuseScope.ThisOccurrence, false);
        return RecordDecisionAsync(requestId, "system:expiry", "system", AuthorizationTier.Answer, null, response, expiredAt, cancellationToken);
    }

    public Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(
        string requestId,
        string decisionReceiptId,
        DecisionActionRef actionRef,
        long? currentGoalStateVersion,
        string result,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken = default)
    {
        var key = $"{requestId}:{decisionReceiptId}:{actionRef.Value}";
        if (_effectReceipts.TryGetValue(key, out var existing))
            return Task.FromResult(new DecisionEffectApplyResult(existing.Status == EffectReceiptStatus.Applied, true, existing, null));

        var effect = new EffectReceipt(
            Guid.NewGuid().ToString("n"),
            requestId,
            decisionReceiptId,
            actionRef,
            EffectReceiptStatus.Applied,
            null,
            currentGoalStateVersion,
            result,
            appliedAt);
        _effectReceipts[key] = effect;
        return Task.FromResult(new DecisionEffectApplyResult(true, false, effect, null));
    }

    private CollaborationDecisionAuditEntry AddAudit(
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
        DateTimeOffset decidedAt)
    {
        var audit = new CollaborationDecisionAuditEntry(
            _audits.Count + 1,
            correlationKey,
            actionIndex,
            actorId,
            interactionId,
            outcome,
            command,
            rejectionReason,
            expectedGoalStateVersion,
            actualGoalStateVersion,
            renderedContentHash,
            decidedAt);
        _audits.Add(audit);
        return audit;
    }
}
