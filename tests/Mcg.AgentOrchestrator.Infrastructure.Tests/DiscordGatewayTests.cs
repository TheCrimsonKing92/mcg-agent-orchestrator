using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Net;

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

    [Xunit.Fact(DisplayName = "DiscordCollaborationView_resolving_last_item_clears_the_goal_message_no_spam")]
    public async Task DiscordCollaborationViewResolvingLastItemClearsTheGoalMessage()
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
        // One aggregated message, edited in place — no per-tap "Applied" reply spam, and once the goal's
        // last item is resolved the message has no buttons.
        Assert.Equal(1, api.SentMessages.Count);
        Assert.True(api.EditedMessages.Count >= 1);
        Assert.Equal(0, api.EditedMessages[^1].Buttons.Count);
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
        Assert.Contains(submit.ErrorMessage!, s => s.Contains("allowlist"));
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
        Assert.Contains(message.Content, s => s.Contains("Landing needs review"));
        Assert.Contains(message.Content, s => s.Contains("LandingEscalation"));
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
        Assert.Contains(message.Content, s => s.Contains("Loop surfaced decision"));
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
        Assert.Contains(message.Content, s => s.Contains("Retry surfaced decision"));
    }

    [Xunit.Fact(DisplayName = "OperatorListen_collaboration_reconcile_loop_exits_on_unauthorized_discord_refresh_error")]
    public async Task OperatorListenCollaborationReconcileLoopExitsOnUnauthorizedDiscordRefreshError()
    {
        var root = CreateTempDirectory();
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

        await Xunit.Assert.ThrowsAsync<HttpRequestException>(() =>
            CliCommandHandlers.RunCollaborationReconcileLoopAsync(view, TimeSpan.FromSeconds(15), CancellationToken.None));

        Assert.Equal(1, api.CreateThreadAttempts);
        Xunit.Assert.Empty(api.SentMessages);
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
