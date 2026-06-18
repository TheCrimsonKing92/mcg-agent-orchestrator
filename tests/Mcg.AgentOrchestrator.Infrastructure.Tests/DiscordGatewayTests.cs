using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordGatewayTests
{
    // ---- DiscordInteractionHandler param overload (parity with JSON Process tests) ----

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_direct_action_maps_to_decision")]
    public void DiscordInteractionHandlerOverloadDirectActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var command = "next abc123";
        var userId = "111222333";
        var interactionId = "interaction-999";
        var customId = DiscordInteractionHandler.BuildDirectCustomId(inboxItemId, command);

        var result = DiscordInteractionHandler.Process(customId, userId, interactionId, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(inboxItemId, result.Decision!.InboxItemId);
        Assert.Equal(command, result.Decision.Command);
        Assert.Equal($"discord:{userId}", result.Decision.ActorId);
        Assert.Equal(interactionId, result.Decision.IdempotencyKey);
        Assert.False(result.RequiresConfirmation);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_confirm_returns_requires_confirmation")]
    public void DiscordInteractionHandlerOverloadConfirmReturnsRequiresConfirmation()
    {
        var inboxItemId = "inbox-abc123def456";
        var command = "land abc123";
        var userId = "111222333";
        var customId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, command);

        var result = DiscordInteractionHandler.Process(customId, userId, "interact-001", [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is null);
        Assert.True(result.RequiresConfirmation);
        Assert.True(result.ConfirmationCustomId is not null);
        Assert.Contains(result.ConfirmationCustomId!, s => s.Contains("mcgo-confirmed"));
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_confirmed_action_maps_to_decision")]
    public void DiscordInteractionHandlerOverloadConfirmedActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var command = "land abc123";
        var userId = "111222333";
        var confirmCustomId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, command);
        var confirmResult = DiscordInteractionHandler.Process(confirmCustomId, userId, "interact-001", [userId]);

        var result = DiscordInteractionHandler.Process(confirmResult.ConfirmationCustomId!, userId, "interact-002", [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(command, result.Decision!.Command);
        Assert.Equal(inboxItemId, result.Decision.InboxItemId);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_unauthorized_user_rejected")]
    public void DiscordInteractionHandlerOverloadUnauthorizedUserRejected()
    {
        var customId = DiscordInteractionHandler.BuildDirectCustomId("inbox-abc", "next abc123");

        var result = DiscordInteractionHandler.Process(customId, "unauthorized-999", "interact-001", ["authorized-111"]);

        Assert.True(result.ErrorMessage is not null);
        Assert.Contains(result.ErrorMessage!, s => s.Contains("allowlist"));
        Assert.True(result.Decision is null);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_malformed_custom_id_rejected")]
    public void DiscordInteractionHandlerOverloadMalformedCustomIdRejected()
    {
        // mcgo| prefix but missing separator between inboxItemId and command
        var customId = "mcgo|inbox-only-no-separator";
        var userId = "111222333";

        var result = DiscordInteractionHandler.Process(customId, userId, "interact-001", [userId]);

        Assert.True(result.ErrorMessage is not null);
        Assert.True(result.Decision is null);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_unrecognised_prefix_rejected")]
    public void DiscordInteractionHandlerOverloadUnrecognisedPrefixRejected()
    {
        var customId = "unknown-prefix|inbox-abc|next";
        var userId = "111222333";

        var result = DiscordInteractionHandler.Process(customId, userId, "interact-001", [userId]);

        Assert.True(result.ErrorMessage is not null);
        Assert.True(result.Decision is null);
    }

    // ---- Interaction→decision adapter: verifies customId+user extraction maps correctly ----

    [Xunit.Fact(DisplayName = "DiscordInteractionAdapter_maps_component_custom_id_and_user_correctly")]
    public void DiscordInteractionAdapterMapsComponentCustomIdAndUserCorrectly()
    {
        // The gateway listener extracts (customId, userId, interactionId) from SocketMessageComponent
        // and calls Process. This test verifies that mapping is correct end-to-end by calling
        // the overload with component-equivalent values.
        var inboxItemId = "inbox-gateway-001";
        var command = "next gw001";
        var userId = "555666777";
        var interactionId = "888999000";
        var customId = DiscordInteractionHandler.BuildDirectCustomId(inboxItemId, command);

        var result = DiscordInteractionHandler.Process(customId, userId, interactionId, [userId]);

        Assert.True(result.Decision is not null);
        Assert.Equal(inboxItemId, result.Decision!.InboxItemId);
        Assert.Equal($"discord:{userId}", result.Decision.ActorId);
        Assert.Equal(interactionId, result.Decision.IdempotencyKey);
    }

    // ---- DiscordDecisionApplier ----

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_valid_decision_dispatches_acks_and_posts_result")]
    public async Task DiscordDecisionApplierValidDecisionDispatchesAcksAndPostsResult()
    {
        var auditDir = CreateTempDirectory();
        var dispatched = new List<string>();
        var acknowledged = new List<string>();
        var posted = new List<string>();

        var applier = new DiscordDecisionApplier(
            auditDir,
            dispatch: (cmd, _) => { dispatched.Add(cmd); return Task.CompletedTask; },
            acknowledge: itemId => acknowledged.Add(itemId),
            postResult: (msg, _) => { posted.Add(msg); return Task.CompletedTask; });

        var decision = new OperatorDecision("inbox-apply-001", "next abc123", null, "discord:user1", "key-001");

        var applied = await applier.ApplyAsync(decision);

        Assert.True(applied);
        Assert.Equal(1, dispatched.Count);
        Assert.Equal("next abc123", dispatched[0]);
        Assert.Equal(1, acknowledged.Count);
        Assert.Equal("inbox-apply-001", acknowledged[0]);
        Assert.Equal(1, posted.Count);
        Assert.True(OperatorDecisionLog.IsRecorded(auditDir, "inbox-apply-001"));
    }

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_duplicate_decision_not_re_executed")]
    public async Task DiscordDecisionApplierDuplicateDecisionNotReExecuted()
    {
        var auditDir = CreateTempDirectory();
        var dispatched = new List<string>();

        var applier = new DiscordDecisionApplier(
            auditDir,
            dispatch: (cmd, _) => { dispatched.Add(cmd); return Task.CompletedTask; });

        var decision = new OperatorDecision("inbox-dup-001", "next abc123", null, "discord:user1", "key-dup-001");

        var first = await applier.ApplyAsync(decision);
        var second = await applier.ApplyAsync(decision);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, dispatched.Count);
    }

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_without_optional_seams_applies_successfully")]
    public async Task DiscordDecisionApplierWithoutOptionalSeamsAppliesSuccessfully()
    {
        var auditDir = CreateTempDirectory();
        var dispatched = new List<string>();

        var applier = new DiscordDecisionApplier(
            auditDir,
            dispatch: (cmd, _) => { dispatched.Add(cmd); return Task.CompletedTask; });

        var decision = new OperatorDecision("inbox-noop-001", "next xyz", null, "discord:user1", "key-noop-001");

        var applied = await applier.ApplyAsync(decision);

        Assert.True(applied);
        Assert.Equal(1, dispatched.Count);
    }

    // ---- Factory no-op when Discord unconfigured ----

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_gateway_listener_returns_null_when_catalog_is_null_type")]
    public void OperatorChannelFactoryGatewayListenerReturnsNullWhenCatalogIsNullType()
    {
        var catalog = OperatorChannelCatalog.Default();
        var auditDir = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(auditDir, "items.db"));

        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, "any-token", store, auditDir);

        Assert.True(listener is null);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_gateway_listener_returns_null_when_token_missing")]
    public void OperatorChannelFactoryGatewayListenerReturnsNullWhenTokenMissing()
    {
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", "123456789", ["user1"]);
        var auditDir = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(auditDir, "items.db"));

        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, null, store, auditDir);

        Assert.True(listener is null);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_gateway_listener_returns_null_when_forum_channel_id_missing")]
    public void OperatorChannelFactoryGatewayListenerReturnsNullWhenForumChannelIdMissing()
    {
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", ForumChannelId: null, OperatorUserIds: ["user1"]);
        var auditDir = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(auditDir, "items.db"));

        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, "fake-token", store, auditDir);

        Assert.True(listener is null);
    }

    // ---- Collaboration spine view ----

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_reconcile_delivers_raised_item_and_marks_delivered")]
    public async Task DiscordCollaborationViewReconcileDeliversRaisedItemAndMarksDelivered()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc123", "Need decision", "Body", "corr-1");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        var items = await store.ListAsync();
        Assert.Equal(CollaborationItemStatus.Delivered, items[0].Status);
        Assert.Equal(1, api.CreatedThreads.Count);
        Assert.Equal(1, api.SentMessages.Count);
        Assert.False(api.SentMessages[0].Buttons[0].Disabled);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_duplicate_tap_resolves_once_and_posts_result_once")]
    public async Task DiscordCollaborationViewDuplicateTapResolvesOnceAndPostsResultOnce()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc123", "Need decision", "Body", "corr-dup");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await view.ReconcileAsync();
        var customId = DiscordInteractionHandler.BuildDirectCustomId("corr-dup", "resolved");

        await view.ApplyInteractionAsync(customId, "user1", "interaction-1");
        await view.ApplyInteractionAsync(customId, "user1", "interaction-2");

        var items = await store.ListAsync();
        Assert.Equal(CollaborationItemStatus.Resolved, items[0].Status);
        Assert.Equal(2, api.SentMessages.Count);
        Assert.Equal(2, api.EditedMessages.Count);
        Assert.True(api.EditedMessages.Last().Buttons[0].Disabled);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_restart_reconcile_refreshes_existing_open_message")]
    public async Task DiscordCollaborationViewRestartReconcileRefreshesExistingOpenMessage()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Verify, "goal-abc123", "Verify result", "Body", "corr-restart");
        var firstView = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await firstView.ReconcileAsync();

        var restartedView = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await restartedView.ReconcileAsync();

        Assert.Equal(1, api.CreatedThreads.Count);
        Assert.Equal(1, api.SentMessages.Count);
        Assert.Equal(1, api.EditedMessages.Count);
        Assert.False(api.EditedMessages[0].Buttons[0].Disabled);
    }

    private sealed class FakeDiscordForumApi : IDiscordForumApi
    {
        public List<(ulong ForumChannelId, string Title, string Content)> CreatedThreads { get; } = [];
        public List<(ulong ThreadId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> SentMessages { get; } = [];
        public List<(ulong ThreadId, ulong MessageId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> EditedMessages { get; } = [];

        public Task<ulong> CreateThreadAsync(
            ulong forumChannelId,
            string title,
            string initialContent,
            CancellationToken cancellationToken = default)
        {
            CreatedThreads.Add((forumChannelId, title, initialContent));
            return Task.FromResult((ulong)CreatedThreads.Count + 1000UL);
        }

        public Task<ulong> SendMessageAsync(
            ulong threadId,
            string content,
            IReadOnlyList<DiscordButtonDefinition> buttons,
            CancellationToken cancellationToken = default)
        {
            SentMessages.Add((threadId, content, buttons));
            return Task.FromResult((ulong)SentMessages.Count + 2000UL);
        }

        public Task EditMessageAsync(
            ulong threadId,
            ulong messageId,
            string content,
            IReadOnlyList<DiscordButtonDefinition> buttons,
            CancellationToken cancellationToken = default)
        {
            EditedMessages.Add((threadId, messageId, content, buttons));
            return Task.CompletedTask;
        }
    }
}
