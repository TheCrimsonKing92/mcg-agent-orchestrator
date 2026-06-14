using Mcg.AgentOrchestrator.App.Orchestration;
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
        Assert.Equal(item.Message, escalation.Summary);
        Assert.Equal(item.Evidence, escalation.KeyEvidence);
        Assert.True(escalation.Actions.Count >= 1);
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
        var command = "next abc123";
        var userId = "111222333";
        var customId = DiscordInteractionHandler.BuildDirectCustomId(inboxItemId, command);
        var payload = BuildInteractionPayload(userId, customId);

        var result = DiscordInteractionHandler.Process(payload, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(inboxItemId, result.Decision!.InboxItemId);
        Assert.Equal(command, result.Decision.Command);
        Assert.Equal($"discord:{userId}", result.Decision.ActorId);
        Assert.False(result.RequiresConfirmation);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_confirm_action_returns_requires_confirmation")]
    public void DiscordInteractionHandlerConfirmActionReturnsRequiresConfirmation()
    {
        var inboxItemId = "inbox-abc123def456";
        var command = "land abc123";
        var userId = "111222333";
        var customId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, command);
        var payload = BuildInteractionPayload(userId, customId);

        var result = DiscordInteractionHandler.Process(payload, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is null);
        Assert.True(result.RequiresConfirmation);
        Assert.True(result.ConfirmationCustomId is not null);
        Assert.Contains(result.ConfirmationCustomId!, s => s.Contains("mcgo-confirmed"));
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_confirmed_action_maps_to_decision")]
    public void DiscordInteractionHandlerConfirmedActionMapsToDecision()
    {
        var inboxItemId = "inbox-abc123def456";
        var command = "land abc123";
        var userId = "111222333";
        var confirmCustomId = DiscordInteractionHandler.BuildConfirmCustomId(inboxItemId, command);
        var confirmPayload = BuildInteractionPayload(userId, confirmCustomId);
        var confirmResult = DiscordInteractionHandler.Process(confirmPayload, [userId]);

        var confirmedPayload = BuildInteractionPayload(userId, confirmResult.ConfirmationCustomId!);
        var result = DiscordInteractionHandler.Process(confirmedPayload, [userId]);

        Assert.True(result.ErrorMessage is null);
        Assert.True(result.Decision is not null);
        Assert.Equal(command, result.Decision!.Command);
    }

    [Xunit.Fact(DisplayName = "DiscordInteractionHandler_rejects_unauthorized_user")]
    public void DiscordInteractionHandlerRejectsUnauthorizedUser()
    {
        var customId = DiscordInteractionHandler.BuildDirectCustomId("inbox-abc", "next abc123");
        var payload = BuildInteractionPayload("unauthorized-user-999", customId);

        var result = DiscordInteractionHandler.Process(payload, ["authorized-user-111"]);

        Assert.True(result.ErrorMessage is not null);
        Assert.Contains(result.ErrorMessage!, s => s.Contains("allowlist"));
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
        var decision = new OperatorDecision("inbox-001", "next abc123", null, "discord:user1", "key-001");

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
        var d1 = new OperatorDecision("inbox-001", "next abc", null, "discord:user1", "key-1");
        var d2 = new OperatorDecision("inbox-002", "next def", null, "discord:user1", "key-2");

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
        var decision = new OperatorDecision("inbox-persist-001", "next xyz", null, "discord:op1", "idem-key-1");
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        OperatorDecisionLog.TryRecord(dir, decision, now);
        var entries = OperatorDecisionLog.LoadAll(dir);

        Assert.Equal(1, entries.Count);
        Assert.Equal("inbox-persist-001", entries[0].InboxItemId);
        Assert.Equal("next xyz", entries[0].Command);
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

    // ---- DiscordOperatorChannel (outbound, with mocked IDiscordForumApi) ----

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_send_creates_thread_and_posts")]
    public async Task DiscordOperatorChannelSendCreatesThreadAndPosts()
    {
        var fakeApi = new FakeDiscordForumApi(nextThreadId: 999UL);
        var stateDir = CreateTempDirectory();
        var channel = new DiscordOperatorChannel(fakeApi, forumChannelId: 42UL, stateDir);
        var escalation = BuildEscalation("inbox-101", goalPrefix: "abc123");

        await channel.SendEscalationAsync(escalation);

        Assert.Equal(1, fakeApi.CreatedThreads.Count);
        Assert.Equal(1, fakeApi.SentMessages.Count);
        Assert.Contains(fakeApi.CreatedThreads[0].Title, s => s.Contains("abc123"));
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_second_escalation_reuses_existing_thread")]
    public async Task DiscordOperatorChannelSecondEscalationReusesExistingThread()
    {
        var fakeApi = new FakeDiscordForumApi(nextThreadId: 888UL);
        var stateDir = CreateTempDirectory();
        var channel = new DiscordOperatorChannel(fakeApi, forumChannelId: 42UL, stateDir);
        var e1 = BuildEscalation("inbox-201", goalPrefix: "abc123");
        var e2 = BuildEscalation("inbox-202", goalPrefix: "abc123");

        await channel.SendEscalationAsync(e1);
        await channel.SendEscalationAsync(e2);

        Assert.Equal(1, fakeApi.CreatedThreads.Count);
        Assert.Equal(2, fakeApi.SentMessages.Count);
    }

    [Xunit.Fact(DisplayName = "DiscordOperatorChannel_confirm_button_uses_confirm_custom_id")]
    public async Task DiscordOperatorChannelConfirmButtonUsesConfirmCustomId()
    {
        var fakeApi = new FakeDiscordForumApi(nextThreadId: 777UL);
        var stateDir = CreateTempDirectory();
        var channel = new DiscordOperatorChannel(fakeApi, forumChannelId: 42UL, stateDir);
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

        var buttons = fakeApi.SentMessages[0].Buttons;
        Assert.Equal(1, buttons.Count);
        Assert.Contains(buttons[0].CustomId, s => s.StartsWith("mcgo-confirm|", StringComparison.Ordinal));
        Assert.Equal(DiscordButtonStyle.Danger, buttons[0].Style);
    }

    // ---- helpers ----

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

        public Task<ulong> CreateThreadAsync(
            ulong forumChannelId,
            string title,
            string initialContent,
            CancellationToken cancellationToken = default)
        {
            CreatedThreads.Add((title, initialContent));
            return Task.FromResult(_nextThreadId);
        }

        public Task SendMessageAsync(
            ulong threadId,
            string content,
            IReadOnlyList<DiscordButtonDefinition> buttons,
            CancellationToken cancellationToken = default)
        {
            SentMessages.Add((threadId, content, buttons));
            return Task.CompletedTask;
        }
    }
}
