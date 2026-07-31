using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Net;

public sealed class DiscordGatewayTests
{
    [Xunit.Fact(DisplayName = "DiscordOperatorFaultClassifier_auth_401_and_403_are_non_fatal")]
    public void DiscordOperatorFaultClassifierAuth401And403AreNonFatal()
    {
        DiscordOperatorFaultClassifier.ResetForTests();
        try
        {
            var unauthorized = new HttpRequestException(
                "The server responded with error 401: Unauthorized",
                null,
                HttpStatusCode.Unauthorized);
            var forbidden = new HttpRequestException(
                "The server responded with error 403: Forbidden",
                null,
                HttpStatusCode.Forbidden);

            Assert.True(DiscordOperatorFaultClassifier.IsAuthError(unauthorized));
            Assert.True(DiscordOperatorFaultClassifier.IsAuthError(forbidden));
            Assert.False(DiscordOperatorFaultClassifier.IsFatal(unauthorized));
            Assert.False(DiscordOperatorFaultClassifier.IsFatal(forbidden));
        }
        finally
        {
            DiscordOperatorFaultClassifier.ResetForTests();
        }
    }

    // ---- DiscordInteractionHandler param overload (parity with JSON Process tests) ----

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_direct_action_maps_to_decision")]
    public void DiscordInteractionHandlerOverloadDirectActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var actionIndex = 0;
        var userId = "111222333";
        var interactionId = "interaction-999";
        var customId = DiscordInteractionHandler.BuildDirectCustomId(inboxItemId, actionIndex);

        var result = DiscordInteractionHandler.Process(customId, userId, interactionId, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(inboxItemId, result.Decision!.InboxItemId);
        Assert.Equal(actionIndex, result.Decision.ActionIndex);
        Assert.Equal($"discord:{userId}", result.Decision.ActorId);
        Assert.Equal(interactionId, result.Decision.IdempotencyKey);
        Assert.False(result.RequiresConfirmation);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_confirm_returns_requires_confirmation")]
    public void DiscordInteractionHandlerOverloadConfirmReturnsRequiresConfirmation()
    {
        var inboxItemId = "inbox-abc123def456";
        var actionIndex = 0;
        var userId = "111222333";
        var customId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, actionIndex);

        var result = DiscordInteractionHandler.Process(customId, userId, "interact-001", [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is null);
        Assert.True(result.RequiresConfirmation);
        Assert.True(result.ConfirmationCustomId is not null);
        Assert.Contains("mcgo-confirmed", result.ConfirmationCustomId!);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_confirmed_action_maps_to_decision")]
    public void DiscordInteractionHandlerOverloadConfirmedActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var actionIndex = 0;
        var userId = "111222333";
        var confirmCustomId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, actionIndex);
        var confirmResult = DiscordInteractionHandler.Process(confirmCustomId, userId, "interact-001", [userId]);

        var result = DiscordInteractionHandler.Process(confirmResult.ConfirmationCustomId!, userId, "interact-002", [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(actionIndex, result.Decision!.ActionIndex);
        Assert.Equal(inboxItemId, result.Decision.InboxItemId);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_overload_unauthorized_user_rejected")]
    public void DiscordInteractionHandlerOverloadUnauthorizedUserRejected()
    {
        var customId = DiscordInteractionHandler.BuildDirectCustomId("inbox-abc", 0);

        var result = DiscordInteractionHandler.Process(customId, "unauthorized-999", "interact-001", ["authorized-111"]);

        Assert.True(result.ErrorMessage is not null);
        Assert.Contains("allowlist", result.ErrorMessage!);
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
        var actionIndex = 0;
        var userId = "555666777";
        var interactionId = "888999000";
        var customId = DiscordInteractionHandler.BuildDirectCustomId(inboxItemId, actionIndex);

        var result = DiscordInteractionHandler.Process(customId, userId, interactionId, [userId]);

        Assert.True(result.Decision is not null);
        Assert.Equal(inboxItemId, result.Decision!.InboxItemId);
        Assert.Equal(actionIndex, result.Decision.ActionIndex);
        Assert.Equal($"discord:{userId}", result.Decision.ActorId);
        Assert.Equal(interactionId, result.Decision.IdempotencyKey);
    }

    // ---- DiscordDecisionApplier ----

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_valid_decision_dispatches_acks_and_posts_result")]
    public async Task DiscordDecisionApplierValidDecisionDispatchesAcksAndPostsResult()
    {
        var auditDir = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(auditDir, "items.db"));
        var dispatched = new List<string>();
        var acknowledged = new List<string>();
        var posted = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-apply-001",
            "Apply",
            "Body",
            "inbox-apply-001",
            [new CollaborationActionBinding("Next", "next abc123")]);

        var applier = new DiscordDecisionApplier(
            auditDir,
            dispatch: (cmd, _) => { dispatched.Add(cmd); return Task.CompletedTask; },
            collaborationStore: store,
            allowedUserIds: ["user1"],
            acknowledge: itemId => acknowledged.Add(itemId),
            postResult: (msg, _) => { posted.Add(msg); return Task.CompletedTask; });

        var decision = new OperatorDecision("inbox-apply-001", 0, null, "discord:user1", "key-001");

        var applied = await applier.ApplyAsync(decision);

        Assert.True(applied);
        Assert.Equal(1, dispatched.Count);
        Assert.Equal("next abc123", dispatched[0]);
        Assert.Equal(1, acknowledged.Count);
        Assert.Equal("inbox-apply-001", acknowledged[0]);
        Assert.Equal(1, posted.Count);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Assert.Equal("Applied", audit.Outcome);
        Assert.Equal("discord:user1", audit.ActorId);
        Assert.Equal("key-001", audit.InteractionId);
    }

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_recovery_round_trips_through_operator_intent_with_full_audit")]
    public async Task DiscordDecisionApplierRecoveryRoundTripsThroughOperatorIntentWithFullAudit()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Discord operator-intent recovery");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
        await repository.SaveAsync(kernel);
        using var loopLease = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        var collaborationStore = new CollaborationItemStore(Path.Combine(root, "collaboration-items.db"));
        await collaborationStore.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            goal.Id.Value,
            "Retry",
            "Retry failed task",
            "discord-intent-item",
            [new CollaborationActionBinding("Retry", $"retry {goal.Id.Value[..8]} 1 discord-retry")]);
        var providers = new InMemoryModelProviderRegistry([]);

        var applier = new DiscordDecisionApplier(
            root,
            dispatch: (command, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var agents = (IReadOnlyList<AgentDefinition>)AgentCatalog.Default().Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                CliPersistentStateRunner.ExecuteCommand(
                    CliArgumentParser.SplitCommand(command),
                    repository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    DiscordTestOperatorChannel.Instance);
                return Task.CompletedTask;
            },
            collaborationStore,
            allowedUserIds: ["user1"]);
        var decision = new OperatorDecision(
            "discord-intent-item",
            0,
            null,
            "discord:user1",
            "discord-interaction-key");

        Assert.True(await applier.ApplyAsync(decision));
        var intentStore = SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        var intent = Assert.Single(await intentStore.ListForGoalAsync(goal.Id.Value));
        Assert.Equal("discord-interaction-key", intent.IdempotencyKey);
        Assert.Equal("discord:user1", intent.Actor);
        Assert.Equal("discord", intent.Channel);
        Assert.Equal("discord-operator-allowlist", intent.AuthenticationAssurance);

        var liveKernel = await repository.LoadAsync();
        var liveGoal = liveKernel.GetGoal(goal.Id);
        var coordinator = new OperatorIntentCoordinator(intentStore);
        Assert.True(coordinator.ExecutePending(liveKernel, liveGoal).MutatedGoalState);
        await repository.SaveAsync(liveKernel);
        coordinator.CompletePersisted([goal.Id]);

        var outcome = await intentStore.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
        var audit = Assert.Single(await collaborationStore.ListDecisionAuditAsync());
        Assert.Equal("discord:user1", audit.ActorId);
        Assert.Equal("discord-interaction-key", audit.InteractionId);
    }

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_duplicate_decision_not_re_executed")]
    public async Task DiscordDecisionApplierDuplicateDecisionNotReExecuted()
    {
        var auditDir = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(auditDir, "items.db"));
        var dispatched = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-dup-001",
            "Apply",
            "Body",
            "inbox-dup-001",
            [new CollaborationActionBinding("Next", "next abc123")]);

        var applier = new DiscordDecisionApplier(
            auditDir,
            dispatch: (cmd, _) => { dispatched.Add(cmd); return Task.CompletedTask; },
            collaborationStore: store,
            allowedUserIds: ["user1"]);

        var decision = new OperatorDecision("inbox-dup-001", 0, null, "discord:user1", "key-dup-001");

        var first = await applier.ApplyAsync(decision);
        var second = await applier.ApplyAsync(decision);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, dispatched.Count);
    }

    [Xunit.Fact(DisplayName = "DiscordDecisionApplier_fabricated_wire_command_is_inert")]
    public async Task DiscordDecisionApplierFabricatedWireCommandIsInert()
    {
        var auditDir = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(auditDir, "items.db"));
        var dispatched = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-safe-001",
            "Safe",
            "Body",
            "inbox-noop-001",
            [new CollaborationActionBinding("Next", "next xyz")]);

        var applier = new DiscordDecisionApplier(
            auditDir,
            dispatch: (cmd, _) => { dispatched.Add(cmd); return Task.CompletedTask; },
            collaborationStore: store,
            allowedUserIds: ["user1"]);

        var payload = """
            {
              "id": "key-noop-001",
              "member": { "user": { "id": "user1" } },
              "data": {
                "custom_id": "mcgo|inbox-noop-001|0",
                "command": "recover compromised",
                "values": ["land unsafe"]
              }
            }
            """;
        var parsed = DiscordInteractionHandler.Process(payload, ["user1"]);

        var applied = await applier.ApplyAsync(parsed.Decision!);

        Assert.True(applied);
        Assert.Equal(1, dispatched.Count);
        Assert.Equal("next xyz", dispatched[0]);
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

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_failed_render_does_not_persist_render_hash")]
    public async Task DiscordCollaborationViewFailedRenderDoesNotPersistRenderHash()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        api.CreateThreadFailures.Enqueue(new InvalidOperationException("Discord rejected render."));
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-abc123",
            "Need decision",
            "Body",
            "corr-render-fail",
            [new CollaborationActionBinding("Next", "next abc123")]);
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        var action = (await store.ListActionsAsync("corr-render-fail")).Single();
        Assert.Null(action.RenderedContentHash);
        Assert.Empty(api.SentMessages);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_aggregates_same_goal_items_into_one_message")]
    public async Task DiscordCollaborationViewAggregatesSameGoalItemsIntoOneMessage()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc123", "First escalation", "Body", "corr-a");
        await store.RaiseAsync(CollaborationItemType.Clarification, "goal-abc123", "Second escalation", "Body", "corr-b");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        // Both items for goal-abc123 share ONE aggregated message (not a message per item), each with a button.
        Assert.Equal(1, api.CreatedThreads.Count);
        Assert.Equal(1, api.SentMessages.Count);
        Assert.Equal(2, api.SentMessages[0].Buttons.Count);
        Assert.True(api.SentMessages[0].Content.Contains("First escalation"));
        Assert.True(api.SentMessages[0].Content.Contains("Second escalation"));
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_names_action_overflow_beyond_five_buttons")]
    public async Task DiscordCollaborationViewNamesActionOverflowBeyondFiveButtons()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-two-actions",
            "Acceptance decision",
            "Body",
            "corr-two-actions",
            [
                new CollaborationActionBinding("Apply 1", "next goal-two-actions 1"),
                new CollaborationActionBinding("Apply 2", "next goal-two-actions 2"),
                new CollaborationActionBinding("Apply 3", "next goal-two-actions 3"),
                new CollaborationActionBinding("Apply 4", "next goal-two-actions 4"),
                new CollaborationActionBinding("Apply 5", "next goal-two-actions 5"),
                new CollaborationActionBinding("Apply 6", "next goal-two-actions 6")
            ]);
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        var message = api.SentMessages.Single();
        Assert.Equal(5, message.Buttons.Count);
        Assert.Contains("Apply 6", message.Content);
        Assert.Contains("corr-two-actions", message.Content);
        Assert.Contains("+1 more (no button)", message.Content);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_preserves_all_twenty_overflow_actions_within_discord_limit")]
    public async Task DiscordCollaborationViewPreservesAllTwentyOverflowActionsWithinDiscordLimit()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        var actions = Enumerable.Range(1, 25)
            .Select(index => new CollaborationActionBinding(
                $"A{index:D2}-" + new string('a', 80),
                $"next goal-max-actions {index}"))
            .ToList();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-max-actions",
            new string('s', 200),
            new string('b', 300),
            "corr-max-actions-" + new string('c', 40),
            actions);
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        var message = api.SentMessages.Single();
        Assert.Equal(5, message.Buttons.Count);
        Assert.Contains("+20 more (no button)", message.Content);
        for (var index = 6; index <= 25; index++)
            Assert.Contains($"A{index:D2}-", message.Content);
        Assert.Contains("answer via attention verbs or terminal.", message.Content);
        Assert.True(message.Content.Length <= DiscordCollaborationViewService.DiscordMessageLimit);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_six_open_items_names_button_overflow")]
    public async Task DiscordCollaborationViewSixOpenItemsNamesButtonOverflow()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        for (var i = 0; i < 5; i++)
        {
            await store.RaiseWithActionsAsync(
                CollaborationItemType.Decision,
                "goal-six-items",
                $"Decision {i}",
                "Body",
                $"corr-{i}",
                [new CollaborationActionBinding($"Apply {i}", $"next goal-six-items {i}")]);
        }
        const string overflowKey = "spec-clarification:goal-six-items:scope:deadbeefcafebabe";
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            "goal-six-items",
            "Overflow clarification",
            "Body",
            overflowKey);
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        var message = api.SentMessages.Single();
        Assert.Equal(5, message.Buttons.Count);
        Assert.Contains("+1 more (no button)", message.Content);
        Assert.Contains("deadbeef", message.Content);
        Assert.Contains("Overflow clarification", message.Content);
        Assert.DoesNotContain("spec-clarificati…", message.Content);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_caps_oversized_goal_message_to_discord_limit")]
    public async Task DiscordCollaborationViewCapsOversizedGoalMessageToDiscordLimit()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        // Eight long clarifications for one goal would otherwise blow past Discord's 2000-char limit (50035).
        var longBody = new string('x', 600);
        for (var i = 0; i < 8; i++)
        {
            await store.RaiseAsync(
                CollaborationItemType.Clarification, "goal-abc123",
                $"Spec clarification {i}: " + new string('q', 180), longBody, $"corr-{i}");
        }
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        Assert.Equal(1, api.SentMessages.Count);
        Assert.True(api.SentMessages[0].Content.Length <= DiscordCollaborationViewService.DiscordMessageLimit);
        Assert.True(api.SentMessages[0].Content.Contains("truncated to fit"));
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_resolving_last_item_clears_the_goal_message_no_spam")]
    public async Task DiscordCollaborationViewResolvingLastItemClearsTheGoalMessage()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc123", "Need decision", "Body", "corr-dup");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await view.ReconcileAsync();
        var customId = DiscordInteractionHandler.BuildDirectCustomId("corr-dup", 0);

        await view.ApplyInteractionAsync(customId, "user1", "interaction-1");
        await view.ApplyInteractionAsync(customId, "user1", "interaction-2");

        var items = await store.ListAsync();
        Assert.Equal(CollaborationItemStatus.Resolved, items[0].Status);
        // One aggregated message, edited in place — no per-tap "Applied" reply spam, and once the goal's
        // last item is resolved the message has no buttons.
        Assert.Equal(1, api.SentMessages.Count);
        Assert.True(api.EditedMessages.Count >= 1);
        Assert.Equal(0, api.EditedMessages[^1].Buttons.Count);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_applied_action_acks_inbox_and_returns_ephemeral_reply_text")]
    public async Task DiscordCollaborationViewAppliedActionAcksInboxAndReturnsReplyText()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        var acknowledged = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-live-ack",
            "Apply live action",
            "Body",
            "corr-live-ack",
            [new CollaborationActionBinding("Apply", "next goal-live-ack")]);
        var catalog = new OperatorChannelCatalog(
            "discord",
            ForumChannelId: "42",
            OperatorUserIds: ["user1"]);
        var view = OperatorChannelFactory.CreateCollaborationViewWithApi(
            catalog,
            api,
            store,
            root,
            dispatchAction: (_, _) => Task.CompletedTask,
            acknowledge: acknowledged.Add)!;
        await view.ReconcileAsync();

        var deferredEphemerally = false;
        var followups = new List<(string Message, bool Ephemeral)>();
        var component = new DiscordGatewayListener.ButtonInteractionContext(
            api.SentMessages.Single().Buttons.Single().CustomId,
            "user1",
            "interaction-live-ack",
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            ephemeral =>
            {
                deferredEphemerally = ephemeral;
                return Task.CompletedTask;
            },
            (message, ephemeral) =>
            {
                followups.Add((message, ephemeral));
                return Task.CompletedTask;
            },
            (_, _, _) => Task.CompletedTask);

        await DiscordGatewayListener.HandleButtonExecutedAsync(
            view,
            component);

        Assert.Equal(["corr-live-ack"], acknowledged);
        Assert.True(deferredEphemerally);
        var followup = Assert.Single(followups);
        Assert.Contains("Applied", followup.Message);
        Assert.True(followup.Ephemeral);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_rejects_task_scoped_dispatch_verbs_at_bind_composition")]
    public void DiscordOperatorChannelRejectsTaskScopedDispatchVerbsAtBindComposition()
    {
        foreach (var verb in new[] { "subscription-dispatch", "execute-dispatch", "start-dispatch" })
        {
            var exception = Assert.Throws<ArgumentException>(
                () => DiscordOperatorChannel.BindTaskScopedCommandToGoal($"{verb} 7", "goal1234"));
            Assert.Contains(verb, exception.Message);
            Assert.Contains("Forbidden verbs", exception.Message);
        }
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_non_allowlisted_user_rejected_and_logged")]
    public async Task DiscordCollaborationViewNonAllowlistedUserRejectedAndLogged()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc123", "Need decision", "Body", "corr-auth");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await view.ReconcileAsync();
        var customId = api.SentMessages.Single().Buttons.Single().CustomId;

        var result = await view.ApplyInteractionAsync(customId, "intruder", "interaction-auth-1");

        Assert.True(result.ErrorMessage is not null);
        Assert.Contains("allowlist", result.ErrorMessage);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Assert.Equal("Rejected", audit.Outcome);
        Assert.Equal("discord:intruder", audit.ActorId);
        Assert.Equal("interaction-auth-1", audit.InteractionId);
        Assert.Equal(
            DiscordCollaborationViewService.ComputeContentHash(api.SentMessages.Single().Content),
            audit.RenderedContentHash);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_applied_decision_audit_records_actor_interaction_and_render_hash")]
    public async Task DiscordCollaborationViewAppliedDecisionAuditRecordsActorInteractionAndRenderHash()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-abc123", "Need decision", "Body", "corr-audit");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await view.ReconcileAsync();
        var sent = api.SentMessages.Single();

        var result = await view.ApplyInteractionAsync(sent.Buttons.Single().CustomId, "user1", "interaction-audit-1");

        Assert.True(result.ErrorMessage is null);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Assert.Equal("Applied", audit.Outcome);
        Assert.Equal("discord:user1", audit.ActorId);
        Assert.Equal("interaction-audit-1", audit.InteractionId);
        Assert.Equal(DiscordCollaborationViewService.ComputeContentHash(sent.Content), audit.RenderedContentHash);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_applies_bound_command_through_server_dispatch")]
    public async Task DiscordCollaborationViewAppliesBoundCommandThroughServerDispatch()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        var dispatched = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-abc123",
            "Need decision",
            "Body",
            "corr-dispatch",
            [new CollaborationActionBinding("Next", "next abc123")]);
        var view = new DiscordCollaborationViewService(
            store,
            api,
            42UL,
            root,
            ["user1"],
            dispatchAction: (command, _) =>
            {
                dispatched.Add(command);
                return Task.CompletedTask;
            });
        await view.ReconcileAsync();

        var result = await view.ApplyInteractionAsync(
            api.SentMessages.Single().Buttons.Single().CustomId,
            "user1",
            "interaction-dispatch-1");

        Assert.True(result.ErrorMessage is null);
        Assert.Equal(["next abc123"], dispatched);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_stale_goal_state_version_rejects_before_dispatch")]
    public async Task DiscordCollaborationViewStaleGoalStateVersionRejectsBeforeDispatch()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        var dispatched = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-abc123",
            "Need decision",
            "Body",
            "corr-stale-view",
            [new CollaborationActionBinding("Accept", "acceptance abc123", ExpectedGoalStateVersion: 3)]);
        var view = new DiscordCollaborationViewService(
            store,
            api,
            42UL,
            root,
            ["user1"],
            currentGoalStateVersion: (_, _) => Task.FromResult<long?>(4),
            dispatchAction: (command, _) =>
            {
                dispatched.Add(command);
                return Task.CompletedTask;
            });
        await view.ReconcileAsync();

        var result = await view.ApplyInteractionAsync(
            api.SentMessages.Single().Buttons.Single().CustomId,
            "user1",
            "interaction-stale-view-1");

        Assert.True(result.ErrorMessage is not null);
        Assert.Contains("Stale goal state version", result.ErrorMessage);
        Xunit.Assert.Empty(dispatched);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Assert.Equal("Rejected", audit.Outcome);
        Assert.Equal(3, audit.ExpectedGoalStateVersion);
        Assert.Equal(4, audit.ActualGoalStateVersion);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_action_modal_text_flows_only_to_text_file")]
    public async Task DiscordCollaborationViewActionModalTextFlowsOnlyToTextFile()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        var dispatched = new List<string>();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-abc123",
            "Need answer",
            "Body",
            "corr-input",
            [new CollaborationActionBinding("Answer", "answer request123 <answer>", RequiresInput: true)]);
        var view = new DiscordCollaborationViewService(
            store,
            api,
            42UL,
            root,
            ["user1"],
            dispatchAction: (command, _) =>
            {
                dispatched.Add(command);
                return Task.CompletedTask;
            });
        await view.ReconcileAsync();
        var button = api.SentMessages.Single().Buttons.Single();
        var modal = view.TryBuildActionInputModalRequest(button.CustomId, "user1", out var modalError);

        var result = await view.ApplyActionInputModalAsync(
            modal!.ModalCustomId,
            "malicious --autonomy unsafe",
            "user1",
            "interaction-input-1");

        Assert.True(modalError is null);
        Assert.True(result.ErrorMessage is null);
        var command = dispatched.Single();
        Assert.Contains("answer request123 --text-file", command);
        Assert.DoesNotContain("malicious --autonomy unsafe", command);
        var pathStart = command.IndexOf('"') + 1;
        var pathEnd = command.IndexOf('"', pathStart);
        var textFile = command[pathStart..pathEnd];
        Assert.Equal("malicious --autonomy unsafe", File.ReadAllText(textFile));
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_action_input_button_rejection_is_logged")]
    public async Task DiscordCollaborationViewActionInputButtonRejectionIsLogged()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseWithActionsAsync(
            CollaborationItemType.Decision,
            "goal-abc123",
            "Need answer",
            "Body",
            "corr-input-auth",
            [new CollaborationActionBinding("Answer", "answer request123 <answer>", RequiresInput: true)]);
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        await view.ReconcileAsync();
        var sent = api.SentMessages.Single();
        var button = sent.Buttons.Single();

        var modal = view.TryBuildActionInputModalRequest(button.CustomId, "intruder", out var modalError);
        await view.RecordRejectedActionReferenceAsync(
            button.CustomId,
            "intruder",
            "interaction-input-auth-1",
            modalError!);

        Assert.Null(modal);
        Assert.Contains("allowlist", modalError);
        var audit = (await store.ListDecisionAuditAsync()).Single();
        Assert.Equal("Rejected", audit.Outcome);
        Assert.Equal("discord:intruder", audit.ActorId);
        Assert.Equal("interaction-input-auth-1", audit.InteractionId);
        Assert.Equal(DiscordCollaborationViewService.ComputeContentHash(sent.Content), audit.RenderedContentHash);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_modal_answer_writes_back_spec_and_unblocks_clarification")]
    public async Task DiscordCollaborationViewModalAnswerWritesBackSpecAndUnblocksClarification()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var api = new FakeDiscordForumApi();
        var json = """
            ```json
            {
              "behavioralContract": "Integrates with an external API.",
              "acceptanceCriteria": ["Endpoint responds correctly"],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": [{"kind": "external-contract", "refinerConfidence": "low", "blastRadius": "high", "question": "Which API version to target?", "choice": "", "rationale": "Unclear from objective."}]
            }
            ```
            """;
        var provider = new FakeSmokeProvider(text: json, providerName: "fake-refiner");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Integrate API with unspecified version");
        var service = new GoalRefinementService(
            providers,
            catalog,
            store,
            new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath));
        await service.RefineAsync(kernel, goal.Id);
        var view = new DiscordCollaborationViewService(
            store,
            api,
            42UL,
            root,
            ["user1"],
            (correlationKey, answer, cancellationToken) =>
                service.TryResolveOpenClarificationAsync(kernel, correlationKey, answer, cancellationToken));

        await view.ReconcileAsync();
        var button = api.SentMessages.Single().Buttons.Single();
        var modal = view.TryBuildAnswerModalRequest(button.CustomId, "user1", out var modalError);

        Assert.True(modalError is null);
        Assert.True(modal is not null);
        var result = await view.ApplyAnswerModalAsync(
            modal!.ModalCustomId,
            "Use v2.",
            "user1",
            "interaction-modal-1");

        Assert.True(result.ErrorMessage is null);
        var refreshed = kernel.GetGoal(goal.Id).RefinedSpec!;
        Assert.False(refreshed.HasOpenQuestions);
        Assert.Equal("Answered", refreshed.OpenQuestions.Single().Status);
        Assert.Equal("Use v2.", refreshed.OpenQuestions.Single().Answer);
        Assert.Equal("Use v2.", refreshed.Decisions.Single().Choice);
        Assert.False(GoalRefinementGate.HasOpenClarification(workspace, goal));
        Assert.Equal(0, api.EditedMessages[^1].Buttons.Count);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_modal_submit_rejects_unauthorized_user")]
    public void DiscordInteractionHandlerModalSubmitRejectsUnauthorizedUser()
    {
        var submit = DiscordInteractionHandler.ProcessAnswerModalSubmit(
            "mcgo-modal|spec-clarification:goal:external-contract:abc",
            "Use v2.",
            "intruder",
            "interaction-1",
            ["user1"]);

        Assert.True(submit.ErrorMessage is not null);
        Assert.Contains("allowlist", submit.ErrorMessage!);
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

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_separate_goals_get_separate_messages")]
    public async Task DiscordCollaborationViewSeparateGoalsGetSeparateMessages()
    {
        var root = CreateTempDirectory();
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var api = new FakeDiscordForumApi();
        await store.RaiseAsync(CollaborationItemType.Decision, "goal-aaaaaaa", "Goal A needs a decision", "Body", "corr-a");
        await store.RaiseAsync(CollaborationItemType.Verify, "goal-bbbbbbb", "Goal B needs verify", "Body", "corr-b");
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await view.ReconcileAsync();

        // Two distinct goals → two messages (one per goal), each in its own thread.
        Assert.Equal(2, api.CreatedThreads.Count);
        Assert.Equal(2, api.SentMessages.Count);
        Assert.True(api.SentMessages[0].ThreadId != api.SentMessages[1].ThreadId);
    }

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_renders_escalation_raised_by_operator_channel")]
    public async Task DiscordCollaborationViewRendersEscalationRaisedByOperatorChannel()
    {
        var root = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(root);
        var api = new FakeDiscordForumApi();
        var channel = new DiscordOperatorChannel(store);
        var escalation = new OperatorEscalation(
            "inbox-render-001",
            "goal-render-123456",
            "render12",
            "LandingEscalation",
            "Landing needs review",
            "Goal: Render escalation through queue\nReason: integration conflict",
            "acceptance output tail",
            [new OperatorEscalationAction("Accept Goal", "acceptance render12 --autonomy supervised-auto", RequiresConfirm: true)],
            null);
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);

        await channel.SendEscalationAsync(escalation);
        await view.ReconcileAsync();

        Assert.Equal(1, api.CreatedThreads.Count);
        var message = api.SentMessages.Single();
        Assert.Contains("Landing needs review", message.Content);
        Assert.Contains("LandingEscalation", message.Content);
        Assert.Equal(1, message.Buttons.Count);
    }

    [Xunit.Fact(DisplayName = "OperatorListen_collaboration_reconcile_loop_surfaces_newly_raised_item")]
    public async Task OperatorListenCollaborationReconcileLoopSurfacesNewlyRaisedItem()
    {
        var root = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(root);
        var api = new FakeDiscordForumApi();
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        using var cts = new CancellationTokenSource();
        var delayCalls = 0;

        Task Delay(TimeSpan _, CancellationToken cancellationToken)
        {
            delayCalls++;
            if (delayCalls == 1)
            {
                return store.RaiseAsync(
                    CollaborationItemType.Decision,
                    "goal-loop-123",
                    "Loop surfaced decision",
                    "Body",
                    "corr-loop-1",
                    cancellationToken);
            }

            cts.Cancel();
            return Task.FromCanceled(cts.Token);
        }

        await CliCommandHandlers.RunCollaborationReconcileLoopAsync(view, TimeSpan.FromSeconds(15), cts.Token, Delay);

        var message = api.SentMessages.Single();
        Assert.Contains("Loop surfaced decision", message.Content);
    }

    [Xunit.Fact(DisplayName = "OperatorListen_collaboration_reconcile_loop_retries_transient_discord_refresh_error")]
    public async Task OperatorListenCollaborationReconcileLoopRetriesTransientDiscordRefreshError()
    {
        var root = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(root);
        var api = new FakeDiscordForumApi();
        api.CreateThreadFailures.Enqueue(new HttpRequestException(
            "The server responded with error 503: ServiceUnavailable",
            null,
            HttpStatusCode.ServiceUnavailable));
        var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
        using var cts = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        await store.RaiseAsync(
            CollaborationItemType.Decision,
            "goal-transient-123",
            "Retry surfaced decision",
            "Body",
            "corr-transient-1");

        Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            delays.Add(delay);
            if (delays.Count == 2)
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            }

            return Task.CompletedTask;
        }

        await CliCommandHandlers.RunCollaborationReconcileLoopAsync(view, TimeSpan.FromSeconds(15), cts.Token, Delay);

        Assert.Equal(2, api.CreateThreadAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), delays[1]);
        var message = api.SentMessages.Single();
        Assert.Contains("Retry surfaced decision", message.Content);
    }

    [Xunit.Fact(DisplayName = "OperatorListen_collaboration_reconcile_loop_exits_on_unauthorized_discord_refresh_error")]
    public async Task OperatorListenCollaborationReconcileLoopExitsOnUnauthorizedDiscordRefreshError()
    {
        DiscordOperatorFaultClassifier.ResetForTests();
        var root = CreateTempDirectory();
        try
        {
            var warnings = new List<DiscordOperatorAuthWarning>();
            DiscordOperatorFaultClassifier.AuthWarningSink = warnings.Add;
            var store = CollaborationItemStore.ForDirectory(root);
            var api = new FakeDiscordForumApi();
            api.CreateThreadFailures.Enqueue(new HttpRequestException(
                "The server responded with error 401: Unauthorized",
                null,
                HttpStatusCode.Unauthorized));
            var view = new DiscordCollaborationViewService(store, api, 42UL, root, ["user1"]);
            await store.RaiseAsync(
                CollaborationItemType.Decision,
                "goal-auth-123",
                "Unauthorized decision",
                "Body",
                "corr-auth-1");
            using var cts = new CancellationTokenSource();

            Task Delay(TimeSpan _, CancellationToken cancellationToken)
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            }

            await CliCommandHandlers.RunCollaborationReconcileLoopAsync(
                view,
                TimeSpan.FromSeconds(15),
                cts.Token,
                Delay);
            await view.ReconcileAsync();

            Assert.True(DiscordOperatorFaultClassifier.IsAuthDisabledForProcess);
            Assert.Equal(1, warnings.Count);
            Assert.Equal(HttpStatusCode.Unauthorized, warnings[0].StatusCode);
            Assert.Equal(1, api.CreateThreadAttempts);
            Xunit.Assert.Empty(api.SentMessages);
        }
        finally
        {
            DiscordOperatorFaultClassifier.ResetForTests();
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "DiscordProgressView_reconcile_skips_identical_status_content")]
    public async Task DiscordProgressViewReconcileSkipsIdenticalStatusContent()
    {
        var root = CreateTempDirectory();
        var catalogPath = Path.Combine(root, "operator-channel.json");
        OperatorChannelStore.Save(catalogPath, new OperatorChannelCatalog("discord", ForumChannelId: "42"));
        var api = new FakeDiscordForumApi();
        var view = new DiscordProgressViewService(api, 42UL, catalogPath);
        var projection = StatusProjector.Project(new StatusProjectionInput(
            [new StatusProjectionGoal(
                "goal-123456",
                "Ship progress view",
                GoalStatus.Active,
                [new StatusProjectionTask(AgentRole.Developer, WorkTaskStatus.Running)],
                DateTimeOffset.UtcNow)],
            0,
            null,
            DateTimeOffset.UtcNow));

        var first = await view.ReconcileAsync(projection);
        var second = await view.ReconcileAsync(projection);

        Assert.False(first.Unchanged);
        Assert.True(first.CreatedThread);
        Assert.True(first.SentMessage);
        Assert.True(second.Unchanged);
        Assert.Equal(1, api.CreatedThreads.Count);
        Assert.Equal(1, api.SentMessages.Count);
        Assert.Equal(0, api.EditedMessages.Count);
    }

    [Xunit.Fact(DisplayName = "DiscordProgressView_truncates_oversized_content_before_api_call_with_omission_counts")]
    public async Task DiscordProgressViewTruncatesOversizedContentBeforeApiCallWithOmissionCounts()
    {
        var root = CreateTempDirectory();
        var catalogPath = Path.Combine(root, "operator-channel.json");
        OperatorChannelStore.Save(catalogPath, new OperatorChannelCatalog("discord", ForumChannelId: "42"));
        var api = new FakeDiscordForumApi();
        var longContent = "**Progress**" + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                Enumerable.Range(0, 30).Select(index => $"- goal-{index:00} developing / running: {new string('x', 120)}"));
        var projection = new StatusProjection(
            longContent,
            Unchanged: false,
            new StatusProjectionBuckets([], [], []),
            OpenEscalationCount: 0);
        var view = new DiscordProgressViewService(api, 42UL, catalogPath);

        await view.ReconcileAsync(projection);

        var content = api.SentMessages.Single().Content;
        Assert.True(content.Length <= DiscordCollaborationViewService.DiscordMessageLimit);
        Assert.Contains("truncated:", content);
        Assert.Contains("goal(s)", content);
        Assert.Contains("line(s)", content);
        Assert.Contains("goal-00", content);
        Assert.DoesNotContain("goal-29", content);
    }

    private sealed class DiscordTestOperatorChannel : IOperatorChannel
    {
        public static readonly DiscordTestOperatorChannel Instance = new();

        public string ChannelType => "discord";

        public Task SendEscalationAsync(
            OperatorEscalation escalation,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDiscordForumApi : IDiscordForumApi
    {
        public List<(ulong ForumChannelId, string Title, string Content)> CreatedThreads { get; } = [];
        public List<(ulong ThreadId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> SentMessages { get; } = [];
        public List<(ulong ThreadId, ulong MessageId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> EditedMessages { get; } = [];
        public Queue<Exception> CreateThreadFailures { get; } = [];
        public int CreateThreadAttempts { get; private set; }

        public Task<ulong> CreateThreadAsync(
            ulong forumChannelId,
            string title,
            string initialContent,
            CancellationToken cancellationToken = default)
        {
            CreateThreadAttempts++;
            if (CreateThreadFailures.TryDequeue(out var exception))
                return Task.FromException<ulong>(exception);

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
