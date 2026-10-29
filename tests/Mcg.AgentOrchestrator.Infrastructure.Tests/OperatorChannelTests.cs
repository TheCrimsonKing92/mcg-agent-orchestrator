using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorChannelTests : CliCommandTestBase
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
        var catalog = new OperatorChannelCatalog("discord");

        OperatorChannelStore.Save(path, catalog);
        var restored = OperatorChannelStore.Load(path);

        Assert.Equal("discord", restored.ChannelType);
        Assert.False(restored.IsNull);
    }

    [Xunit.Fact]
    public void OperatorChannelStoreLoadsLegacyDashboardUrlAndDropsItWhenSaved()
    {
        var path = Path.Combine(CreateTempDirectory(), "operator-channel.json");
        File.WriteAllText(path, """
            { "channelType": "discord", "dashboardBaseUrl": "https://localhost:5001",
              "forumChannelId": "42", "operatorUserIds": ["user1"] }
            """);

        var catalog = OperatorChannelStore.Load(path);

        Assert.Equal("discord", catalog.ChannelType);
        OperatorChannelStore.Save(path, catalog);
        Assert.DoesNotContain("dashboardBaseUrl", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OperatorChannelStoreLoadsLegacyMuteKeyAndKeepsSupportedFields()
    {
        var path = Path.Combine(CreateTempDirectory(), "operator-channel.json");
        File.WriteAllText(path, """
            { "channelType": "discord", "forumChannelId": "41",
              "progressThreadId": "progress-thread", "deadManHeartbeatEnabled": true,
              "controlPlaneMutedUntil": "2026-07-26T05:00:00+00:00" }
            """);

        var catalog = OperatorChannelStore.Load(path);

        Assert.Equal("discord", catalog.ChannelType);
        Assert.True(catalog.DeadManHeartbeatEnabled);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelStore_load_missing_file_returns_null_default")]
    public void OperatorChannelStoreLoadMissingFileReturnsNullDefault()
    {
        var path = Path.Combine(CreateTempDirectory(), "nonexistent.json");
        var catalog = OperatorChannelStore.Load(path);
        Assert.True(catalog.IsNull);
    }

    // ---- NullOperatorChannel ----

    // Parallel-safe: the catalog and composition use a private temporary directory.
    [Xunit.Fact]
    public void Composition_LegacyDiscordCatalog_ReturnsNullChannel()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "operator-channel.json");
        File.WriteAllText(path, """
            { "channelType": "discord", "forumChannelId": "42",
              "operatorUserIds": ["user1", "user2"], "isNull": false }
            """);

        var catalog = OperatorChannelStore.Load(path);

        // Prove the legacy record loaded instead of falling back after a read failure.
        Assert.Equal("discord", catalog.ChannelType);
        Assert.False(catalog.IsNull);
        var channel = OperatorChannelComposition.Create(catalog, root);
        Assert.Same(NullOperatorChannel.Instance, channel);
    }

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
        Assert.DoesNotContain(actions, action => action.Command == $"operator-inbox-ack {item.Id}");
    }

    [Xunit.Fact(DisplayName = "Projection_ownership_hold_primary_action_resolves_with_supervised_acceptance")]
    public void ProjectionOwnershipHoldPrimaryActionResolvesWithSupervisedAcceptance()
    {
        var item = BuildInboxItem(OperatorInboxKind.OwnershipHold, OperatorInboxSeverity.Blocker,
            suggestedCommand: "acceptance abc123def --autonomy supervised-auto");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        var action = Assert.Single(actions);
        Assert.Equal("Accept Goal", action.Label);
        Assert.Equal("acceptance abc123def --autonomy supervised-auto", action.Command);
        Assert.True(action.RequiresConfirm);
        Assert.DoesNotContain(actions, candidate => candidate.Command.StartsWith("operator-inbox-ack", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Projection_all_blocking_inbox_kinds_have_primary_non_ack_action")]
    public void ProjectionAllBlockingInboxKindsHavePrimaryNonAckAction()
    {
        foreach (var kind in Enum.GetValues<OperatorInboxKind>())
        {
            var item = BuildInboxItem(kind, OperatorInboxSeverity.Blocker, suggestedCommand: SuggestedCommandFor(kind));

            var actions = OperatorEscalationProjection.DeriveActions(item);

            var primary = Assert.Single(actions);
            Assert.False(primary.Command.StartsWith("operator-inbox-ack", StringComparison.OrdinalIgnoreCase), kind.ToString());
            if (kind == OperatorInboxKind.OwnershipHold)
            {
                Assert.Equal("acceptance abc123def --autonomy supervised-auto", primary.Command);
            }
        }
    }

    [Xunit.Fact(DisplayName = "Projection_ack_action_only_secondary_for_non_blocking_informational_item")]
    public void ProjectionAckActionOnlySecondaryForNonBlockingInformationalItem()
    {
        var item = BuildInboxItem(
            OperatorInboxKind.AcceptanceGate,
            OperatorInboxSeverity.Info,
            suggestedCommand: "acceptance abc123def --autonomy supervised-auto");

        var actions = OperatorEscalationProjection.DeriveActions(item);

        Assert.Equal("acceptance abc123def --autonomy supervised-auto", actions[0].Command);
        Assert.Contains(actions.Skip(1), action => action.Command == $"operator-inbox-ack {item.Id}");
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

    // ---- OperatorChannelFactory ----

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_returns_null_channel_when_catalog_is_null_type")]
    public void OperatorChannelFactoryReturnsNullChannelWhenCatalogIsNullType()
    {
        var catalog = OperatorChannelCatalog.Default();
        var channel = OperatorChannelComposition.Create(catalog, CreateTempDirectory());
        Assert.Equal("null", channel.ChannelType);
        Assert.True(channel is NullOperatorChannel);
    }

    [Xunit.Fact(DisplayName = "DeadManHeartbeatClient_default_options_do_not_send")]
    public async Task DeadManHeartbeatClientDefaultOptionsDoNotSend()
    {
        var client = new DeadManHeartbeatClient(new HttpClient(new ThrowingHandler()));

        var sent = await client.SendAsync();

        Assert.False(sent);
    }

    [Xunit.Fact(DisplayName = "OperatorChannelFactory_wires_dead_man_heartbeat_only_when_catalog_enables_valid_url")]
    public void OperatorChannelFactoryWiresDeadManHeartbeatOnlyWhenCatalogEnablesValidUrl()
    {
        var disabled = OperatorChannelFactory.BuildDeadManHeartbeatOptions(OperatorChannelCatalog.Default());
        var enabled = OperatorChannelFactory.BuildDeadManHeartbeatOptions(
            new OperatorChannelCatalog(
                "discord",
                DeadManHeartbeatEnabled: true,
                DeadManHeartbeatUrl: "https://example.test/deadman"));

        Assert.False(disabled.Enabled);
        Assert.True(enabled.Enabled);
        Assert.Equal(new Uri("https://example.test/deadman"), enabled.Endpoint);
    }

    // ---- Escalation path: SendEscalationAsync called on RecordLandingEscalation ----

    [Xunit.Fact(DisplayName = "RecordLandingEscalation_conductor_escalation_uses_state_aware_actionable_content")]
    public void RecordLandingEscalationConductorEscalationUsesStateAwareActionableContent()
    {
        var workspace = BuildTestWorkspace();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Test objective", [
            new TaskSpec(TaskId.New(), "Test task", AgentRole.Developer)
        ]);
        var channel = NullOperatorChannel.Instance;

        Mcg.AgentOrchestrator.App.Orchestration.OperatorInbox.RecordLandingEscalation(
            workspace,
            goal,
            "Acceptance output tail: Unit test failed",
            "conductor:Verified",
            channel);

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

    [Xunit.Fact]
    public void HumanInputInboxSurfacesRepeatedOccurrenceCount()
    {
        var workspace = BuildTestWorkspace();
        var (kernel, goal, task) = BuildKernelWithSingleTaskGoal();
        const string fingerprint = "stable-evidence-fingerprint";
        kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Retrieve the missing receipt.",
            questionFingerprint: fingerprint);
        kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            "Retrieve that receipt from the store.",
            questionFingerprint: fingerprint);

        var item = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8])
            .Items
            .Single(candidate => candidate.Kind == OperatorInboxKind.HumanInput);

        Assert.Contains("Occurrences: 2", item.Message, StringComparison.Ordinal);
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

    [Xunit.Fact(DisplayName = "RecordOwnershipHolds_dedupes_open_hold_refreshes_then_allows_new_after_clear")]
    public void RecordOwnershipHoldsDedupesOpenHoldRefreshesThenAllowsNewAfterClear()
    {
        var workspace = BuildTestWorkspace();
        var (kernel, goal, task) = BuildKernelWithSingleTaskGoal();
        var first = BuildHoldRequest(task, ["src/Mcg.AgentOrchestrator.Infrastructure/First.cs"], "first");
        var second = BuildHoldRequest(task, ["src/Mcg.AgentOrchestrator.Infrastructure/Second.cs"], "second");

        OperatorInbox.RecordOwnershipHolds(workspace, goal, [first]);
        var firstOpen = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8])
            .Items
            .Single(item => item.Kind == OperatorInboxKind.OwnershipHold);
        OperatorInbox.RecordOwnershipHolds(workspace, goal, [second]);

        var refreshed = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8])
            .Items
            .Single(item => item.Kind == OperatorInboxKind.OwnershipHold);
        Assert.Equal(firstOpen.Id, refreshed.Id);
        Assert.Contains("Second.cs", refreshed.Evidence);
        Assert.DoesNotContain("First.cs", refreshed.Evidence);

        OperatorInbox.ClearOwnershipHoldsAfterLanding(workspace, goal, $"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto");
        OperatorInbox.RecordOwnershipHolds(workspace, goal, [first]);

        var raisedAgain = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8])
            .Items
            .Single(item => item.Kind == OperatorInboxKind.OwnershipHold);
        Assert.Contains("First.cs", raisedAgain.Evidence);
    }

    [Xunit.Fact(DisplayName = "RecordOwnershipHolds_retries_bounded_write_and_persists_after_transient_failures")]
    public void RecordOwnershipHoldsRetriesBoundedWriteAndPersistsAfterTransientFailures()
    {
        var workspace = BuildTestWorkspace();
        var (kernel, goal, task) = BuildKernelWithSingleTaskGoal();
        var previousWriter = OperatorInbox.HoldStoreWriteAllText;
        var previousBackoff = OperatorInbox.HoldWriteBackoff;
        var writeAttempts = 0;
        var backoffs = new List<TimeSpan>();
        try
        {
            OperatorInbox.HoldWriteBackoff = backoffs.Add;
            OperatorInbox.HoldStoreWriteAllText = (path, contents) =>
            {
                if (path.EndsWith("ownership-holds.json", StringComparison.OrdinalIgnoreCase) &&
                    Interlocked.Increment(ref writeAttempts) <= 2)
                {
                    throw new IOException("transient hold write failure");
                }

                File.WriteAllText(path, contents);
            };

            OperatorInbox.RecordOwnershipHolds(workspace, goal, [BuildHoldRequest(task)]);

            Assert.Equal(3, writeAttempts);
            Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], backoffs);
            var hold = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8])
                .Items
                .Single(item => item.Kind == OperatorInboxKind.OwnershipHold);
            Assert.Equal(task.Id.Value, hold.TaskId);
        }
        finally
        {
            OperatorInbox.HoldStoreWriteAllText = previousWriter;
            OperatorInbox.HoldWriteBackoff = previousBackoff;
        }
    }

    [Xunit.Fact(DisplayName = "RecordOwnershipHolds_all_retries_failed_raises_distinct_persistence_failure")]
    public void RecordOwnershipHoldsAllRetriesFailedRaisesDistinctPersistenceFailure()
    {
        var workspace = BuildTestWorkspace();
        var (kernel, goal, task) = BuildKernelWithSingleTaskGoal();
        var previousWriter = OperatorInbox.HoldStoreWriteAllText;
        var previousBackoff = OperatorInbox.HoldWriteBackoff;
        var writeAttempts = 0;
        try
        {
            OperatorInbox.HoldWriteBackoff = _ => { };
            OperatorInbox.HoldStoreWriteAllText = (path, contents) =>
            {
                if (path.EndsWith("ownership-holds.json", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref writeAttempts);
                    throw new IOException("persistent hold write failure");
                }

                File.WriteAllText(path, contents);
            };

            OperatorInbox.RecordOwnershipHolds(workspace, goal, [BuildHoldRequest(task)]);

            Assert.Equal(OperatorInbox.MaxHoldWriteRetries, writeAttempts);
            var report = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            Assert.DoesNotContain(report.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            var failure = Assert.Single(report.Items.Where(item => item.Kind == OperatorInboxKind.HoldPersistenceFailure));
            Assert.Equal(OperatorInboxSeverity.Blocker, failure.Severity);
            Assert.Equal(task.Id.Value, failure.TaskId);
            Assert.Contains("persistent hold write failure", failure.Evidence);
            var action = Assert.Single(OperatorEscalationProjection.DeriveActions(failure));
            Assert.Equal($"recover {goal.Id.Value[..8]} <note>", action.Command);
            Assert.True(action.RequiresInput);
            Assert.Contains("HOLD_PERSISTENCE_FAILED", File.ReadAllText(workspace.ConductEventsLogPath));
        }
        finally
        {
            OperatorInbox.HoldStoreWriteAllText = previousWriter;
            OperatorInbox.HoldWriteBackoff = previousBackoff;
        }
    }

    [Xunit.Fact(DisplayName = "ClearOwnershipHoldsAfterLanding_resolves_open_holds_and_emits_receipt")]
    public void ClearOwnershipHoldsAfterLandingResolvesOpenHoldsAndEmitsReceipt()
    {
        var workspace = BuildTestWorkspace();
        var (kernel, goal, task) = BuildKernelWithSingleTaskGoal();
        var command = $"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto";
        OperatorInbox.RecordOwnershipHolds(workspace, goal, [BuildHoldRequest(task)]);

        var receipt = OperatorInbox.ClearOwnershipHoldsAfterLanding(workspace, goal, command);

        Assert.Equal(goal.Id.Value, receipt.GoalId);
        var holdId = Assert.Single(receipt.ClearedHoldIds);
        Assert.StartsWith("inbox-", holdId, StringComparison.Ordinal);
        Assert.Equal(command, receipt.TriggeringCommand);
        var report = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
        Assert.DoesNotContain(report.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
        var eventText = File.ReadAllText(workspace.ConductEventsLogPath);
        Assert.Contains("OWNERSHIP_HOLD_CLEARED", eventText);
        Assert.Contains(holdId, eventText);
        Assert.Contains(command, eventText);
    }

    [Xunit.Fact(DisplayName = "ClearOwnershipHoldsAfterLanding_failure_keeps_landing_visible_with_clearance_failure")]
    public void ClearOwnershipHoldsAfterLandingFailureKeepsLandingVisibleWithClearanceFailure()
    {
        var workspace = BuildTestWorkspace();
        var (kernel, goal, task) = BuildKernelWithSingleTaskGoal();
        var previousWriter = OperatorInbox.HoldStoreWriteAllText;
        var previousBackoff = OperatorInbox.HoldWriteBackoff;
        var command = $"acceptance {goal.Id.Value[..8]} --autonomy supervised-auto";
        try
        {
            OperatorInbox.RecordOwnershipHolds(workspace, goal, [BuildHoldRequest(task)]);
            OperatorInbox.HoldWriteBackoff = _ => { };
            OperatorInbox.HoldStoreWriteAllText = (path, contents) =>
            {
                if (path.EndsWith("ownership-holds.json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("clearance write failure");
                }

                File.WriteAllText(path, contents);
            };

            var receipt = OperatorInbox.ClearOwnershipHoldsAfterLanding(workspace, goal, command);

            Assert.Empty(receipt.ClearedHoldIds);
            var report = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            Assert.Contains(report.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            var failure = Assert.Single(report.Items.Where(item => item.Kind == OperatorInboxKind.HoldClearanceFailure));
            Assert.Equal(OperatorInboxSeverity.Blocker, failure.Severity);
            Assert.Contains(goal.Id.Value, failure.GoalId);
            Assert.Contains("clearance write failure", failure.Evidence);
            var action = Assert.Single(OperatorEscalationProjection.DeriveActions(failure));
            Assert.Equal($"recover {goal.Id.Value[..8]} <note>", action.Command);
            Assert.True(action.RequiresInput);
            Assert.Contains("HOLD_CLEARANCE_FAILED", File.ReadAllText(workspace.ConductEventsLogPath));
        }
        finally
        {
            OperatorInbox.HoldStoreWriteAllText = previousWriter;
            OperatorInbox.HoldWriteBackoff = previousBackoff;
        }
    }

    // ---- Deep link rendered in message content ----

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

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) BuildKernelWithSingleTaskGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Test task", AgentRole.Developer);
        var goal = kernel.CreateGoal("Test objective", [task]);
        return (kernel, goal, task);
    }

    private static OwnershipHoldRequest BuildHoldRequest(
        TaskSpec task,
        IReadOnlyList<string>? paths = null,
        string reason = "test hold") =>
        new(
            task.Id,
            1,
            task.RequiredRole,
            paths ?? ["src/Mcg.AgentOrchestrator.Infrastructure/Protected.cs"],
            reason);

    private static string SuggestedCommandFor(OperatorInboxKind kind) =>
        kind switch
        {
            OperatorInboxKind.HumanInput => "answer abc123def <answer>",
            OperatorInboxKind.FailedTask => "next abc123def",
            OperatorInboxKind.FailedVerification => "verify-needed abc123def",
            OperatorInboxKind.RunningWorker => "refresh-dispatch abc123def 1",
            OperatorInboxKind.MissingVerification => "verify-needed abc123def",
            OperatorInboxKind.AcceptanceGate => "acceptance abc123def --autonomy supervised-auto",
            OperatorInboxKind.SupervisorProposal => "supervisor abc123def",
            OperatorInboxKind.SubscriptionRouteWarning => "subscription-plan abc123def",
            OperatorInboxKind.ReadinessPreflight => "readiness abc123def",
            OperatorInboxKind.BudgetWarning => "subscription-plan abc123def",
            OperatorInboxKind.LandingEscalation => "land abc123def",
            OperatorInboxKind.OwnershipHold => "acceptance abc123def --autonomy supervised-auto",
            OperatorInboxKind.HoldPersistenceFailure => "recover abc123def <note>",
            OperatorInboxKind.HoldClearanceFailure => "recover abc123def <note>",
            _ => "next abc123def"
        };

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Heartbeat should be disabled by default.");
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
            actions ?? [new OperatorEscalationAction("Retry", "next testgoal", RequiresConfirm: false)]);
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
            DateTimeOffset.Parse("2026-07-20T09:00:00Z"),
            acknowledged,
            acknowledged ? DateTimeOffset.UtcNow : null,
            null);
    }
}
