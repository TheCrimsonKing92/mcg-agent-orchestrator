using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System.Text;
using System.Text.Json;

public sealed class OperatorChannelTests
{
    // ---- OperatorChannelCatalog / Store ----

    [Xunit.Fact(DisplayName = "OperatorChannelCatalog_default_is_null_channel")]
    public void OperatorChannelCatalogDefaultIsNullChannel()
    {
        var catalog = OperatorChannelCatalog.Default();
        Assert.True(catalog.IsNull);
        Assert.Equal("null", catalog.ChannelType);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelStore_roundtrips_discord_catalog")]
    public void OperatorChannelStoreRoundtripsDiscordCatalog()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "operator-channel.json");
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001");

        OperatorChannelStore.Save(path, catalog);
        var restored = OperatorChannelStore.Load(path);

        Assert.Equal("discord", restored.ChannelType);
        Assert.Equal("https://localhost:5001", restored.DashboardBaseUrl);
        Assert.False(restored.IsNull);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelStore_load_missing_file_returns_null_default")]
    public void OperatorChannelStoreLoadMissingFileReturnsNullDefault()
    {
        var path = Path.Combine(CreateTempDirectory(), "nonexistent.json");
        var catalog = OperatorChannelStore.Load(path);
        Assert.True(catalog.IsNull);
    }

    // ---- NullOperatorChannel ----

    [Xunit.Fact(DisplayName = "NullOperatorChannel_send_is_noop")]
    public async Task NullOperatorChannelSendIsNoop()
    {
        var channel = NullOperatorChannel.Instance;
        var escalation = BuildEscalation("inbox-001");

        await channel.SendEscalationAsync(escalation);

        Assert.Equal("null", channel.ChannelType);
    }

    // ---- OperatorEscalationProjection ----

    [Xunit.Fact(DisplayName = "Projection_blocker_item_maps_to_escalation")]
    public void ProjectionBlockerItemMapsToEscalation()
    {
        var item = BuildInboxItem(OperatorInboxKind.FailedTask, OperatorInboxSeverity.Blocker,
            suggestedCommand: "next abc123def");

        var escalation = OperatorEscalationProjection.Project(item);

        Assert.True(escalation is not null);
        Assert.Equal(item.Id, escalation!.InboxItemId);
        Assert.Equal(item.GoalId, escalation.GoalId);
        Assert.Equal(item.GoalPrefix, escalation.GoalPrefix);
        Assert.Equal("FailedTask", escalation.Kind);
        Assert.Equal(item.Title, escalation.Title);
        Assert.Contains(item.Objective, escalation.Summary);
        Assert.Contains(item.Message, escalation.Summary);
        Assert.Equal(item.Evidence, escalation.KeyEvidence);
        Assert.True(escalation.Actions.Count >= 1);
    }

    [Xunit.Fact(DisplayName = "Projection_formats_actionable_summary_with_goal_reason_and_response")]
    public void ProjectionFormatsActionableSummaryWithGoalReasonAndResponse()
    {
        var item = BuildInboxItem(
            OperatorInboxKind.AcceptanceGate,
            OperatorInboxSeverity.Blocker,
            suggestedCommand: "acceptance goal1234 --autonomy supervised-auto");

        var escalation = OperatorEscalationProjection.Project(item);

        Assert.True(escalation is not null);
        Assert.True(escalation!.Summary.Contains("Goal: Test goal objective", StringComparison.Ordinal));
        Assert.True(escalation.Summary.Contains("Reason: Something needs attention.", StringComparison.Ordinal));
        Assert.True(escalation.Summary.Contains("Response: Suggested action here.", StringComparison.Ordinal));
        Assert.True(escalation.Actions.Any(action =>
            action.Command == "acceptance goal1234 --autonomy supervised-auto"));
    }

    [Xunit.Fact(DisplayName = "Projection_non_blocker_item_returns_null")]
    public void ProjectionNonBlockerItemReturnsNull()
    {
        var item = BuildInboxItem(OperatorInboxKind.RunningWorker, OperatorInboxSeverity.Warning,
            suggestedCommand: "supervisor abc123");

        var escalation = OperatorEscalationProjection.Project(item);

        Assert.True(escalation is null);
    }

    [Xunit.Fact(DisplayName = "Projection_adds_dashboard_deep_link_when_base_url_provided")]
    public void ProjectionAddsDashboardDeepLinkWhenBaseUrlProvided()
    {
        var item = BuildInboxItem(OperatorInboxKind.FailedTask, OperatorInboxSeverity.Blocker,
            suggestedCommand: "next abc123");

        var escalation = OperatorEscalationProjection.Project(item, "https://localhost:5001");

        Assert.True(escalation is not null);
        Assert.Equal($"https://localhost:5001/goals/{item.GoalPrefix}", escalation!.DashboardDeepLink);
    }

    [Xunit.Fact(DisplayName = "Projection_ProjectAll_excludes_acknowledged_items")]
    public void ProjectionProjectAllExcludesAcknowledgedItems()
    {
        var open = BuildInboxItem(OperatorInboxKind.FailedTask, OperatorInboxSeverity.Blocker, acknowledged: false);
        var acked = BuildInboxItem(OperatorInboxKind.MissingVerification, OperatorInboxSeverity.Blocker, acknowledged: true);

        var escalations = OperatorEscalationProjection.ProjectAll([open, acked]);

        Assert.Equal(1, escalations.Count);
        Assert.Equal(open.Id, escalations[0].InboxItemId);
    }

    // ---- Action derivation / promote-to-main guard ----

    [Xunit.Fact(DisplayName = "Projection_land_command_requires_confirm")]
    public void ProjectionLandCommandRequiresConfirm()
    {
        var item = BuildInboxItem(OperatorInboxKind.LandingEscalation, OperatorInboxSeverity.Blocker,
            suggestedCommand: "land abc123def");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        Assert.True(actions.Count >= 1);
        Assert.True(actions[0].RequiresConfirm);
    }

    [Xunit.Fact(DisplayName = "Projection_acceptance_command_requires_confirm")]
    public void ProjectionAcceptanceCommandRequiresConfirm()
    {
        var item = BuildInboxItem(OperatorInboxKind.AcceptanceGate, OperatorInboxSeverity.Blocker,
            suggestedCommand: "acceptance abc123def --autonomy supervised-auto");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        Assert.True(actions[0].RequiresConfirm);
        Assert.Contains(actions, action => action.Command == $"operator-inbox-ack {item.Id}");
    }

    [Xunit.Fact(DisplayName = "Projection_next_command_does_not_require_confirm")]
    public void ProjectionNextCommandDoesNotRequireConfirm()
    {
        var item = BuildInboxItem(OperatorInboxKind.FailedTask, OperatorInboxSeverity.Blocker,
            suggestedCommand: "next abc123def");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        Assert.True(actions.Count >= 1);
        Assert.False(actions[0].RequiresConfirm);
    }

    [Xunit.Fact(DisplayName = "Projection_answer_command_flagged_requires_input")]
    public void ProjectionAnswerCommandFlaggedRequiresInput()
    {
        var item = BuildInboxItem(OperatorInboxKind.HumanInput, OperatorInboxSeverity.Blocker,
            suggestedCommand: "answer abc123def <answer>");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        Assert.True(actions[0].RequiresInput);
    }

    [Xunit.Fact(DisplayName = "Projection_at_most_three_actions")]
    public void ProjectionAtMostThreeActions()
    {
        var item = BuildInboxItem(OperatorInboxKind.AcceptanceGate, OperatorInboxSeverity.Blocker,
            suggestedCommand: "acceptance abc123def --autonomy supervised-auto");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        Assert.True(actions.Count <= 3);
    }

    // ---- DiscordInteractionHandler (inbound → command mapping) ----

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_direct_action_maps_to_decision")]
    public void DiscordInteractionHandlerDirectActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var actionIndex = 0;
        var userId = "111222333";
        var customId = DiscordInteractionHandler.BuildDirectCustomId(inboxItemId, actionIndex);
        var payload = BuildInteractionPayload(userId, customId);

        var result = DiscordInteractionHandler.Process(payload, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(inboxItemId, result.Decision!.InboxItemId);
        Assert.Equal(actionIndex, result.Decision.ActionIndex);
        Assert.Equal($"discord:{userId}", result.Decision.ActorId);
        Assert.False(result.RequiresConfirmation);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_confirm_action_returns_requires_confirmation")]
    public void DiscordInteractionHandlerConfirmActionReturnsRequiresConfirmation()
    {
        var inboxItemId = "inbox-abc123def456";
        var actionIndex = 0;
        var userId = "111222333";
        var customId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, actionIndex);
        var payload = BuildInteractionPayload(userId, customId);

        var result = DiscordInteractionHandler.Process(payload, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is null);
        Assert.True(result.RequiresConfirmation);
        Assert.True(result.ConfirmationCustomId is not null);
        Assert.Contains("mcgo-confirmed", result.ConfirmationCustomId!);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_confirmed_action_maps_to_decision")]
    public void DiscordInteractionHandlerConfirmedActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var actionIndex = 0;
        var userId = "111222333";
        var confirmCustomId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, actionIndex);
        var confirmPayload = BuildInteractionPayload(userId, confirmCustomId);
        var confirmResult = DiscordInteractionHandler.Process(confirmPayload, [userId]);

        var confirmedPayload = BuildInteractionPayload(userId, confirmResult.ConfirmationCustomId!);
        var result = DiscordInteractionHandler.Process(confirmedPayload, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(actionIndex, result.Decision!.ActionIndex);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_rejects_unauthorized_user")]
    public void DiscordInteractionHandlerRejectsUnauthorizedUser()
    {
        var customId = DiscordInteractionHandler.BuildDirectCustomId("inbox-abc", 0);
        var payload = BuildInteractionPayload("unauthorized-user-999", customId);

        var result = DiscordInteractionHandler.Process(payload, ["authorized-user-111"]);

        Assert.True(result.ErrorMessage is not null);
        Assert.Contains("allowlist", result.ErrorMessage!);
        Assert.True(result.Decision is null);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_rejects_invalid_json")]
    public void DiscordInteractionHandlerRejectsInvalidJson()
    {
        var result = DiscordInteractionHandler.Process("{not valid json", ["user123"]);
        Assert.True(result.ErrorMessage is not null);
    }

    // ---- OperatorDecisionLog (idempotency) ----

    [Xunit.Fact(DisplayName = "OperatorDecisionLog_records_and_detects_duplicate")]
    public void OperatorDecisionLogRecordsAndDetectsDuplicate()
    {
        var dir = CreateTempDirectory();
        var decision = new OperatorDecision("inbox-001", 0, null, "discord:user1", "key-001");

        var first = OperatorDecisionLog.TryRecord(dir, decision, DateTimeOffset.UtcNow);
        var second = OperatorDecisionLog.TryRecord(dir, decision, DateTimeOffset.UtcNow);

        Assert.True(first);
        Assert.False(second);
        Assert.True(OperatorDecisionLog.IsRecorded(dir, "inbox-001"));
    }

    [Xunit.Fact(DisplayName = "OperatorDecisionLog_different_items_both_record")]
    public void OperatorDecisionLogDifferentItemsBothRecord()
    {
        var dir = CreateTempDirectory();
        var d1 = new OperatorDecision("inbox-001", 0, null, "discord:user1", "key-1");
        var d2 = new OperatorDecision("inbox-002", 1, null, "discord:user1", "key-2");

        OperatorDecisionLog.TryRecord(dir, d1, DateTimeOffset.UtcNow);
        OperatorDecisionLog.TryRecord(dir, d2, DateTimeOffset.UtcNow);

        Assert.True(OperatorDecisionLog.IsRecorded(dir, "inbox-001"));
        Assert.True(OperatorDecisionLog.IsRecorded(dir, "inbox-002"));
        Assert.Equal(2, OperatorDecisionLog.LoadAll(dir).Count);
    }

    [Xunit.Fact(DisplayName = "OperatorDecisionLog_unrecorded_item_returns_false")]
    public void OperatorDecisionLogUnrecordedItemReturnsFalse()
    {
        var dir = CreateTempDirectory();
        Assert.False(OperatorDecisionLog.IsRecorded(dir, "inbox-not-present"));
    }

    [Xunit.Fact(DisplayName = "OperatorDecisionLog_audit_persists_across_loads")]
    public void OperatorDecisionLogAuditPersistsAcrossLoads()
    {
        var dir = CreateTempDirectory();
        var decision = new OperatorDecision("inbox-persist-001", 0, null, "discord:op1", "idem-key-1");
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        OperatorDecisionLog.TryRecord(dir, decision, now);
        var entries = OperatorDecisionLog.LoadAll(dir);

        Assert.Equal(1, entries.Count);
        Assert.Equal("inbox-persist-001", entries[0].InboxItemId);
        Assert.Equal(0, entries[0].ActionIndex);
        Assert.Equal("discord:op1", entries[0].ActorId);
        Assert.Equal(now, entries[0].DecidedAt);
    }

    // ---- DiscordSignatureVerifier ----

    [Xunit.Fact(DisplayName = "DiscordSignatureVerifier_valid_signature_returns_true")]
    public void DiscordSignatureVerifierValidSignatureReturnsTrue()
    {
        var (publicKey, privateKey) = GenerateEd25519KeyPair();
        var timestamp = "1234567890";
        var body = """{"type":1}""";
        var message = Encoding.UTF8.GetBytes(timestamp + body);
        var signature = Sign(privateKey, message);

        var result = DiscordSignatureVerifier.Verify(
            Convert.ToHexString(publicKey).ToLowerInvariant(),
            timestamp,
            body,
            Convert.ToHexString(signature).ToLowerInvariant());

        Assert.True(result);
    }

    [Xunit.Fact(DisplayName = "DiscordSignatureVerifier_tampered_body_returns_false")]
    public void DiscordSignatureVerifierTamperedBodyReturnsFalse()
    {
        var (publicKey, privateKey) = GenerateEd25519KeyPair();
        var timestamp = "1234567890";
        var body = """{"type":1}""";
        var message = Encoding.UTF8.GetBytes(timestamp + body);
        var signature = Sign(privateKey, message);

        var result = DiscordSignatureVerifier.Verify(
            Convert.ToHexString(publicKey).ToLowerInvariant(),
            timestamp,
            """{"type":2}""",
            Convert.ToHexString(signature).ToLowerInvariant());

        Assert.False(result);
    }

    [Xunit.Fact(DisplayName = "DiscordSignatureVerifier_wrong_key_returns_false")]
    public void DiscordSignatureVerifierWrongKeyReturnsFalse()
    {
        var (_, privateKey) = GenerateEd25519KeyPair();
        var (wrongPublicKey, _) = GenerateEd25519KeyPair();
        var timestamp = "1234567890";
        var body = """{"type":1}""";
        var message = Encoding.UTF8.GetBytes(timestamp + body);
        var signature = Sign(privateKey, message);

        var result = DiscordSignatureVerifier.Verify(
            Convert.ToHexString(wrongPublicKey).ToLowerInvariant(),
            timestamp,
            body,
            Convert.ToHexString(signature).ToLowerInvariant());

        Assert.False(result);
    }

    [Xunit.Fact(DisplayName = "DiscordSignatureVerifier_invalid_hex_returns_false")]
    public void DiscordSignatureVerifierInvalidHexReturnsFalse()
    {
        var result = DiscordSignatureVerifier.Verify("not-hex", "ts", "body", "also-not-hex");
        Assert.False(result);
    }

    // ---- DiscordOperatorChannel (outbound, via collaboration store) ----

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_send_raises_decision_item")]
    public async Task DiscordOperatorChannelSendRaisesDecisionItem()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = BuildEscalation("inbox-101", goalPrefix: "abc123");

        await channel.SendEscalationAsync(escalation);

        var item = (await store.GetAttentionQueueAsync()).Single();
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal(escalation.GoalId, item.GoalId);
        Assert.Equal(escalation.InboxItemId, item.CorrelationKey);
        Assert.Equal(escalation.Title, item.Subject);
        var action = (await store.ListActionsAsync(escalation.InboxItemId)).Single();
        Assert.Equal("Retry", action.Label);
        Assert.Equal("next testgoal", action.Command);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_no_action_fallback_binds_cli_ack_command")]
    public async Task DiscordOperatorChannelNoActionFallbackBindsCliAckCommand()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = BuildEscalation("inbox-no-action", actions: []);

        await channel.SendEscalationAsync(escalation);

        var action = (await store.ListActionsAsync(escalation.InboxItemId)).Single();
        Assert.Equal("Resolve", action.Label);
        Assert.Equal("operator-inbox-ack inbox-no-action", action.Command);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_second_escalation_raises_second_queue_item")]
    public async Task DiscordOperatorChannelSecondEscalationRaisesSecondQueueItem()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var e1 = BuildEscalation("inbox-201", goalPrefix: "abc123");
        var e2 = BuildEscalation("inbox-202", goalPrefix: "abc123");

        await channel.SendEscalationAsync(e1);
        await channel.SendEscalationAsync(e2);

        var items = await store.GetAttentionQueueAsync();
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.Equal("goal-test-123", item.GoalId);
        }
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_confirm_action_is_preserved_in_item_body")]
    public async Task DiscordOperatorChannelConfirmActionIsPreservedInItemBody()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = new OperatorEscalation(
            "inbox-301",
            "goal-id-123",
            "abc123",
            "LandingEscalation",
            "Land required",
            "Promote to main requested",
            "escalated at ...",
            [new OperatorEscalationAction("Promote to Main", "land abc123", RequiresConfirm: true)],
            null);

        await channel.SendEscalationAsync(escalation);

        var item = (await store.GetAttentionQueueAsync()).Single();
        Assert.Contains("`land abc123`", item.Body);
        Assert.Contains("**Response:**", item.Body);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_message_includes_goal_reason_command_and_response")]
    public async Task DiscordOperatorChannelMessageIncludesGoalReasonCommandAndResponse()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = new OperatorEscalation(
            "inbox-actionable",
            "goal-id-abc123456",
            "abc12345",
            "AcceptanceGate",
            "Acceptance blocked for goal",
            "Goal: Improve escalation content\nReason: Acceptance output tail: test failure",
            "tail: Xunit failed in OperatorChannelTests",
            [new OperatorEscalationAction("Accept Goal", "acceptance abc12345 --autonomy supervised-auto", RequiresConfirm: true)],
            null);

        await channel.SendEscalationAsync(escalation);

        var content = (await store.GetAttentionQueueAsync()).Single().Body;
        Assert.Contains("`abc12345`", content);
        Assert.Contains("Improve escalation content", content);
        Assert.Contains("Acceptance output tail: test failure", content);
        Assert.Contains("`acceptance abc12345 --autonomy supervised-auto`", content);
        Assert.Contains("**Response:**", content);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_binds_current_goal_state_version_to_actions")]
    public async Task DiscordOperatorChannelBindsCurrentGoalStateVersionToActions()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(
            store,
            (goalId, _) => Task.FromResult<long?>(goalId == "goal-id-abc123456" ? 12 : null));
        var escalation = new OperatorEscalation(
            "inbox-versioned",
            "goal-id-abc123456",
            "abc12345",
            "AcceptanceGate",
            "Acceptance ready",
            "Goal ready",
            "evidence",
            [new OperatorEscalationAction("Accept Goal", "acceptance abc12345 --autonomy supervised-auto", RequiresConfirm: true)],
            null);

        await channel.SendEscalationAsync(escalation);

        var action = (await store.ListActionsAsync("inbox-versioned")).Single();
        Assert.Equal(12, action.ExpectedGoalStateVersion);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_prefixes_task_scoped_commands_before_persisting")]
    public async Task DiscordOperatorChannelPrefixesTaskScopedCommandsBeforePersisting()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = new OperatorEscalation(
            "inbox-prefixed",
            "goal-id-abc123456",
            "abc12345",
            "FailedTask",
            "Task action",
            "summary",
            "evidence",
            [
                new OperatorEscalationAction("Retry", "retry 2 <note> --autonomy safe-auto"),
                new OperatorEscalationAction("Refresh", "refresh-dispatch 3"),
                new OperatorEscalationAction("Re-delegate", "re-delegate 4 --autonomy safe-auto")
            ],
            null);

        await channel.SendEscalationAsync(escalation);

        var commands = (await store.ListActionsAsync("inbox-prefixed"))
            .Select(action => action.Command)
            .ToArray();
        Assert.Equal(
            [
                "retry abc12345 2 <note> --autonomy safe-auto",
                "refresh-dispatch abc12345 3",
                "re-delegate abc12345 4 --autonomy safe-auto"
            ],
            commands);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_preserves_already_goal_scoped_commands")]
    public async Task DiscordOperatorChannelPreservesAlreadyGoalScopedCommands()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = new OperatorEscalation(
            "inbox-already-prefixed",
            "goal-id-abc123456",
            "abc12345",
            "AcceptanceGate",
            "Acceptance",
            "summary",
            "evidence",
            [new OperatorEscalationAction("Accept", "acceptance abc12345 --autonomy supervised-auto")],
            null);

        await channel.SendEscalationAsync(escalation);

        var action = (await store.ListActionsAsync("inbox-already-prefixed")).Single();
        Assert.Equal("acceptance abc12345 --autonomy supervised-auto", action.Command);
    }

    // ---- OperatorChannelFactory ----

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_returns_null_channel_when_catalog_is_null_type")]
    public void OperatorChannelFactoryReturnsNullChannelWhenCatalogIsNullType()
    {
        var catalog = OperatorChannelCatalog.Default();
        var channel = OperatorChannelFactory.Create(catalog, "any-token", CreateTempDirectory());
        Assert.Equal("null", channel.ChannelType);
        Assert.True(channel is NullOperatorChannel);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_returns_null_channel_when_token_missing")]
    public void OperatorChannelFactoryReturnsNullChannelWhenTokenMissing()
    {
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", "123456789");
        var channel = OperatorChannelFactory.Create(catalog, null, CreateTempDirectory());
        Assert.Equal("null", channel.ChannelType);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_returns_null_channel_when_forum_channel_id_missing")]
    public void OperatorChannelFactoryReturnsNullChannelWhenForumChannelIdMissing()
    {
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", ForumChannelId: null);
        var channel = OperatorChannelFactory.Create(catalog, "token", CreateTempDirectory());
        Assert.Equal("null", channel.ChannelType);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_returns_discord_channel_when_fully_configured")]
    public void OperatorChannelFactoryReturnsDiscordChannelWhenFullyConfigured()
    {
        var fakeApi = new FakeDiscordForumApi(nextThreadId: 1UL);
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", "42");
        var channel = OperatorChannelFactory.CreateWithApi(catalog, fakeApi, CreateTempDirectory());
        Assert.Equal("discord", channel.ChannelType);
    }

    // ---- OperatorChannelCatalog ForumChannelId / OperatorUserIds roundtrip ----

    [Xunit.Fact(DisplayName = "OperatorChannelStore_roundtrips_discord_catalog_with_forum_channel_id")]
    public void OperatorChannelStoreRoundtripsDiscordCatalogWithForumChannelId()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "operator-channel.json");
        var catalog = new OperatorChannelCatalog(
            "discord",
            "https://localhost:5001",
            ForumChannelId: "987654321",
            OperatorUserIds: ["111", "222"]);

        OperatorChannelStore.Save(path, catalog);
        var restored = OperatorChannelStore.Load(path);

        Assert.Equal("discord", restored.ChannelType);
        Assert.Equal("987654321", restored.ForumChannelId);
        Assert.Equal(2, restored.OperatorUserIds?.Count ?? 0);
        Assert.False(restored.IsNull);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelStore_roundtrips_catalog_with_forum_channel_id_X_and_user_ids_A_B")]
    public void OperatorChannelStoreRoundtripsCatalogWithForumChannelIdXAndUserIdsAB()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "operator-channel.json");
        var catalog = new OperatorChannelCatalog("discord", null, "X", ["A", "B"]);

        OperatorChannelStore.Save(path, catalog);
        var restored = OperatorChannelStore.Load(path);

        Assert.Equal("X", restored.ForumChannelId);
        Assert.Equal(2, restored.OperatorUserIds?.Count ?? 0);
        Assert.True(restored.OperatorUserIds!.Contains("A"));
        Assert.True(restored.OperatorUserIds!.Contains("B"));
    }

    [Xunit.Fact(DisplayName = "OperatorChannelCatalog_three_user_ids_from_csv_A_B_C")]
    public void OperatorChannelCatalogThreeUserIdsFromCsvABC()
    {
        var csv = "A,B,C";
        var userIds = (IReadOnlyList<string>)csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var catalog = new OperatorChannelCatalog("discord", null, null, userIds);

        Assert.Equal(3, catalog.OperatorUserIds?.Count ?? 0);
        Assert.True(catalog.OperatorUserIds!.Contains("A"));
        Assert.True(catalog.OperatorUserIds!.Contains("B"));
        Assert.True(catalog.OperatorUserIds!.Contains("C"));
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_SendTestEscalation_null_channel_prints_guidance_and_does_not_throw")]
    public async Task OperatorChannelFactorySendTestEscalationNullChannelPrintsGuidanceAndDoesNotThrow()
    {
        var channel = NullOperatorChannel.Instance;
        var output = new StringWriter();

        await OperatorChannelFactory.SendTestEscalationAsync(channel, output);

        var text = output.ToString();
        Assert.Contains("not configured", text);
        Assert.Contains("MCGO_DISCORD_BOT_TOKEN", text);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_SendTestEscalation_configured_channel_raises_queue_item")]
    public async Task OperatorChannelFactorySendTestEscalationConfiguredChannelRaisesQueueItem()
    {
        var fakeApi = new FakeDiscordForumApi(nextThreadId: 100UL);
        var catalog = new OperatorChannelCatalog("discord", null, "42");
        var stateDir = CreateTempDirectory();
        var channel = OperatorChannelFactory.CreateWithApi(catalog, fakeApi, stateDir);
        var output = new StringWriter();

        await OperatorChannelFactory.SendTestEscalationAsync(channel, output);

        Assert.Equal(0, fakeApi.SentMessages.Count);
        var item = (await CollaborationItemStore.ForDirectory(stateDir).GetAttentionQueueAsync()).Single();
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal("test-goal-id", item.GoalId);
        Assert.True(item.CorrelationKey is not null && item.CorrelationKey.StartsWith("test-", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "OperatorChannel_spine_test_seed_raises_attention_queue_item_with_correlation_key")]
    public async Task OperatorChannelSpineTestSeedRaisesAttentionQueueItem()
    {
        var workspace = BuildTestWorkspace();

        var item = CliCommandHandlers.RaiseOperatorChannelSpineTestItem(workspace.OrchestratorDirectory);

        Assert.Equal(CollaborationItemType.Verify, item.Type);
        Assert.Equal("operator-channel-test", item.GoalId);
        Assert.False(string.IsNullOrWhiteSpace(item.CorrelationKey));
        Assert.True(item.CorrelationKey!.StartsWith("operator-channel-test-spine-", StringComparison.Ordinal));

        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var queue = await store.GetAttentionQueueAsync();
        var queued = queue.Single();
        Assert.Equal(item.Id, queued.Id);
        Assert.Equal(item.CorrelationKey, queued.CorrelationKey);
        Assert.Equal(CollaborationItemStatus.Raised, queued.Status);
    }

    // ---- Escalation path: SendEscalationAsync called on RecordLandingEscalation ----

    [Xunit.Fact(DisplayName = "RecordLandingEscalation_raises_collaboration_item_when_configured")]
    public async Task RecordLandingEscalationRaisesCollaborationItemWhenConfigured()
    {
        var workspace = BuildTestWorkspace();
        var goal = BuildTestGoal();
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var channel = new DiscordOperatorChannel(store);

        Mcg.AgentOrchestrator.App.Orchestration.OperatorInbox.RecordLandingEscalation(
            workspace, goal, "conflict on integration", "integration", channel);

        var item = (await store.GetAttentionQueueAsync())
            .First(item =>
                item.Type == CollaborationItemType.Decision &&
                item.GoalId == goal.Id.Value &&
                item.Body.Contains("conflict on integration", StringComparison.Ordinal) &&
                item.Body.Contains("**Response:**", StringComparison.Ordinal));
        Assert.Equal(goal.Id.Value, item.GoalId);
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Assert.False(string.IsNullOrWhiteSpace(item.CorrelationKey));
        Assert.Contains("LandingEscalation", item.Body);
        var action = (await store.ListActionsAsync(item.CorrelationKey!)).Single();
        Assert.Equal($"land {goal.Id.Value[..8]}", action.Command);
    }

    [Xunit.Fact(DisplayName = "RecordLandingEscalation_conductor_escalation_uses_state_aware_actionable_content")]
    public async Task RecordLandingEscalationConductorEscalationUsesStateAwareActionableContent()
    {
        var workspace = BuildTestWorkspace();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Test objective", [
            new TaskSpec(TaskId.New(), "Test task", AgentRole.Developer)
        ]);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var channel = new DiscordOperatorChannel(store);

        Mcg.AgentOrchestrator.App.Orchestration.OperatorInbox.RecordLandingEscalation(
            workspace,
            goal,
            "Acceptance output tail: Unit test failed",
            "conductor:Verified",
            channel);

        var queued = (await store.GetAttentionQueueAsync())
            .First(item =>
                item.Type == CollaborationItemType.Decision &&
                item.GoalId == goal.Id.Value &&
                item.Body.Contains("Acceptance output tail: Unit test failed", StringComparison.Ordinal) &&
                item.Body.Contains("**Response:**", StringComparison.Ordinal));
        var content = queued.Body;
        Assert.Equal(goal.Id.Value, queued.GoalId);
        Assert.Equal(CollaborationItemType.Decision, queued.Type);
        Assert.Contains(goal.Id.Value[..8], content);
        Assert.Contains("Acceptance output tail: Unit test failed", content);
        Assert.Contains($"`acceptance {goal.Id.Value[..8]} --autonomy supervised-auto`", content);
        Assert.Contains("**Response:**", content);
        var action = (await store.ListActionsAsync(queued.CorrelationKey!)).Single();
        Assert.Equal($"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto", action.Command);
        Assert.True(action.RequiresConfirmation);

        var report = Mcg.AgentOrchestrator.App.Orchestration.OperatorInbox.Build(
            kernel,
            [],
            WorkerProfileCatalog.Default(),
            workspace,
            goal.Id.Value[..8]);
        var item = report.Items.Single(item => item.Kind == OperatorInboxKind.LandingEscalation);
        Assert.Equal(OperatorInboxSeverity.Blocker, item.Severity);
        Assert.Contains("Acceptance output tail: Unit test failed", item.Message);
        Assert.Equal($"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto", item.SuggestedCommand);
    }

    [Xunit.Fact(DisplayName = "RecordLandingEscalation_inbox_still_recorded_when_channel_throws")]
    public void RecordLandingEscalationInboxStillRecordedWhenChannelThrows()
    {
        var workspace = BuildTestWorkspace();
        var goal = BuildTestGoal();
        var throwingChannel = new ThrowingFakeChannel();

        Mcg.AgentOrchestrator.App.Orchestration.OperatorInbox.RecordLandingEscalation(
            workspace, goal, "conflict reason", "integration", throwingChannel);

        var escapedPath = Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json");
        Assert.True(File.Exists(escapedPath));
    }

    // ---- Deep link rendered in message content ----

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_renders_deep_link_in_content")]
    public async Task DiscordOperatorChannelRendersDeepLinkInContent()
    {
        var stateDir = CreateTempDirectory();
        var store = CollaborationItemStore.ForDirectory(stateDir);
        var channel = new DiscordOperatorChannel(store);
        var escalation = BuildEscalation("inbox-deep-link", goalPrefix: "abc123")
            with { DashboardDeepLink = "https://localhost:5001/goals/abc123" };

        await channel.SendEscalationAsync(escalation);

        var sentContent = (await store.GetAttentionQueueAsync()).Single().Body;
        Assert.Contains("https://localhost:5001/goals/abc123", sentContent);
    }

    // ---- helpers ----

    private static Mcg.AgentOrchestrator.App.Orchestration.OrchestratorWorkspace BuildTestWorkspace()
    {
        var dir = CreateTempDirectory();
        return Mcg.AgentOrchestrator.App.Orchestration.OrchestratorWorkspace.ForDirectory(dir);
    }

    private static Mcg.AgentOrchestrator.Core.Goal BuildTestGoal()
    {
        var id = Mcg.AgentOrchestrator.Core.GoalId.New();
        var task = new Mcg.AgentOrchestrator.Core.TaskSpec(
            Mcg.AgentOrchestrator.Core.TaskId.New(),
            "Test task",
            Mcg.AgentOrchestrator.Core.AgentRole.Developer);
        return new Mcg.AgentOrchestrator.Core.Goal(id, "Test objective", [task]);
    }

    private sealed class ThrowingFakeChannel : IOperatorChannel
    {
        public string ChannelType => "throwing-fake";
        public Task SendEscalationAsync(OperatorEscalation escalation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated channel failure.");
    }

    private static OperatorEscalation BuildEscalation(
        string inboxItemId,
        string goalId = "goal-test-123",
        string goalPrefix = "testgoal",
        string kind = "FailedTask",
        string title = "Test escalation",
        string summary = "Something went wrong.",
        string keyEvidence = "task failed after 3 retries",
        IReadOnlyList<OperatorEscalationAction>? actions = null)
    {
        return new OperatorEscalation(
            inboxItemId, goalId, goalPrefix, kind, title, summary, keyEvidence,
            actions ?? [new OperatorEscalationAction("Retry", "next testgoal", RequiresConfirm: false)],
            null);
    }

    private static OperatorInboxItem BuildInboxItem(
        OperatorInboxKind kind,
        OperatorInboxSeverity severity,
        string? suggestedCommand = null,
        bool acknowledged = false)
    {
        return new OperatorInboxItem(
            "inbox-test-abc",
            kind,
            severity,
            "goal-id-abc123",
            "goal1234",
            "Test goal objective",
            null,
            null,
            $"{kind} title",
            "Something needs attention.",
            "evidence line here",
            "Suggested action here.",
            suggestedCommand ?? "next abc123",
            $"{kind}:source",
            acknowledged,
            acknowledged ? DateTimeOffset.UtcNow : null,
            null);
    }

    private static string BuildInteractionPayload(string userId, string customId, string interactionId = "interaction-001")
    {
        return JsonSerializer.Serialize(new
        {
            id = interactionId,
            type = 3,
            data = new { custom_id = customId, component_type = 2 },
            member = new { user = new { id = userId } }
        });
    }

    private static (byte[] PublicKey, Ed25519PrivateKeyParameters PrivateKey) GenerateEd25519KeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 256));
        var pair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        var publicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        return (publicKey, privateKey);
    }

    private static byte[] Sign(Ed25519PrivateKeyParameters privateKey, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private sealed class FakeDiscordForumApi : IDiscordForumApi
    {
        private readonly ulong _nextThreadId;

        public FakeDiscordForumApi(ulong nextThreadId) => _nextThreadId = nextThreadId;

        public List<(string Title, string Content)> CreatedThreads { get; } = [];
        public List<(ulong ThreadId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> SentMessages { get; } = [];
        public List<(ulong ThreadId, ulong MessageId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> EditedMessages { get; } = [];

        public Task<ulong> CreateThreadAsync(
            ulong forumChannelId,
            string title,
            string initialContent,
            CancellationToken cancellationToken = default)
        {
            CreatedThreads.Add((title, initialContent));
            return Task.FromResult(_nextThreadId);
        }

        public Task<ulong> SendMessageAsync(
            ulong threadId,
            string content,
            IReadOnlyList<DiscordButtonDefinition> buttons,
            CancellationToken cancellationToken = default)
        {
            SentMessages.Add((threadId, content, buttons));
            return Task.FromResult((ulong)SentMessages.Count);
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
