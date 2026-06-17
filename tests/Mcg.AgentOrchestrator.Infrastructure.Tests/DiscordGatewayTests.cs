using Mcg.AgentOrchestrator.App.Dashboard.Hosting;
using Mcg.AgentOrchestrator.App.Orchestration;
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
        var applier = new DiscordDecisionApplier(auditDir, (_, _) => Task.CompletedTask);

        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, "any-token", applier);

        Assert.True(listener is null);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_gateway_listener_returns_null_when_token_missing")]
    public void OperatorChannelFactoryGatewayListenerReturnsNullWhenTokenMissing()
    {
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", "123456789", ["user1"]);
        var auditDir = CreateTempDirectory();
        var applier = new DiscordDecisionApplier(auditDir, (_, _) => Task.CompletedTask);

        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, null, applier);

        Assert.True(listener is null);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_gateway_listener_returns_null_when_forum_channel_id_missing")]
    public void OperatorChannelFactoryGatewayListenerReturnsNullWhenForumChannelIdMissing()
    {
        var catalog = new OperatorChannelCatalog("discord", "https://localhost:5001", ForumChannelId: null, OperatorUserIds: ["user1"]);
        var auditDir = CreateTempDirectory();
        var applier = new DiscordDecisionApplier(auditDir, (_, _) => Task.CompletedTask);

        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, "fake-token", applier);

        Assert.True(listener is null);
    }

    // ---- App-layer wiring: DiscordListenerWiring integration ----

    [Xunit.Fact(DisplayName = "DiscordListenerWiring_dispatch_seam_receives_split_command_parts")]
    public async Task DiscordListenerWiringDispatchSeamReceivesSplitCommandParts()
    {
        // Arrange: workspace backed by a temp directory; fake seam captures parsed parts.
        var root = CreateTempDirectory();
        var workspace = Mcg.AgentOrchestrator.App.Orchestration.OrchestratorWorkspace.ForDirectory(root);
        var dispatchedParts = new List<IReadOnlyList<string>>();

        Func<IReadOnlyList<string>, CancellationToken, Task> seam = (parts, _) =>
        {
            dispatchedParts.Add(parts);
            return Task.CompletedTask;
        };

        var applier = DiscordListenerWiring.BuildApplier(workspace, seam);
        var decision = new OperatorDecision("inbox-wire-001", "next abc123", null, "discord:user1", "key-wire-001");

        // Act
        var applied = await applier.ApplyAsync(decision);

        // Assert: dispatch was called with correctly split parts, proving the command string
        // is routed through CliArgumentParser.SplitCommand before reaching the CLI dispatcher.
        Assert.True(applied);
        Assert.Equal(1, dispatchedParts.Count);
        Assert.True(dispatchedParts[0].Count >= 2);
        Assert.Equal("next", dispatchedParts[0][0]);
        Assert.Equal("abc123", dispatchedParts[0][1]);
    }

    [Xunit.Fact(DisplayName = "DiscordListenerWiring_acknowledge_delegate_writes_to_inbox_acks_file")]
    public async Task DiscordListenerWiringAcknowledgeDelegateWritesToInboxAcksFile()
    {
        // Arrange
        var root = CreateTempDirectory();
        var workspace = Mcg.AgentOrchestrator.App.Orchestration.OrchestratorWorkspace.ForDirectory(root);

        var applier = DiscordListenerWiring.BuildApplier(
            workspace,
            (_, _) => Task.CompletedTask);

        var decision = new OperatorDecision("inbox-ack-wire-001", "goals", null, "discord:user1", "key-ack-wire-001");

        // Act
        var applied = await applier.ApplyAsync(decision);

        // Assert: the acks file was written with the inbox item id.
        Assert.True(applied);
        var acksPath = Path.Combine(workspace.OrchestratorDirectory, "operator-inbox-acks.json");
        Assert.True(File.Exists(acksPath));
        var content = File.ReadAllText(acksPath);
        Assert.True(content.Contains("inbox-ack-wire-001"));
    }

    [Xunit.Fact(DisplayName = "DiscordListenerWiring_no_connection_when_discord_unconfigured")]
    public void DiscordListenerWiringNoConnectionWhenDiscordUnconfigured()
    {
        // Arrange: default (null-type) catalog and no bot token.
        var root = CreateTempDirectory();
        var workspace = Mcg.AgentOrchestrator.App.Orchestration.OrchestratorWorkspace.ForDirectory(root);
        var catalog = OperatorChannelCatalog.Default();
        var applier = DiscordListenerWiring.BuildApplier(workspace, (_, _) => Task.CompletedTask);

        // Act: factory returns null — no connection is attempted.
        var listener = OperatorChannelFactory.CreateGatewayListener(catalog, null, applier);

        // Assert
        Assert.True(listener is null);
    }
}
