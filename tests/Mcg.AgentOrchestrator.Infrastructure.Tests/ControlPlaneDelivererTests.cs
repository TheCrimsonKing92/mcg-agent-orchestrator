using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ControlPlaneDelivererTests
{
    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_input_surface_excludes_conductor_tick_types")]
    public void ControlPlaneDelivererInputSurfaceExcludesConductorTickTypes()
    {
        var forbidden = new[]
        {
            "ConductorTickEvent",
            "BatchTickSummary",
            "RunEventRecord"
        };
        var publicSurface = typeof(DiscordControlPlaneDeliverer)
            .GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
                .Append(method.ReturnType.FullName ?? method.ReturnType.Name))
            .ToList();

        foreach (var forbiddenName in forbidden)
        {
            Assert.DoesNotContain(publicSurface, name => name.Contains(forbiddenName, StringComparison.Ordinal));
        }
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_same_dedup_key_edits_existing_message")]
    public async Task ControlPlaneDelivererSameDedupKeyEditsExistingMessage()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var deliverer = new DiscordControlPlaneDeliverer(store, transport);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var original = Card("goal-123456", "FailedTask", "task-1", "same-cause", "First", "body");
        var changed = original with { Title = "Updated" };

        await deliverer.DeliverAsync([original], now);
        await deliverer.DeliverAsync([changed], now.AddMinutes(1));

        Assert.Single(transport.Sent);
        var edit = Assert.Single(transport.Edited);
        Assert.Equal(1001UL, edit.MessageId);
        Assert.Single(store.Marks);
        Assert.Contains("Updated", edit.Content);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_resolved_card_strikes_through_and_removes_buttons")]
    public async Task ControlPlaneDelivererResolvedCardStrikesThroughAndRemovesButtons()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var deliverer = new DiscordControlPlaneDeliverer(store, transport);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var open = Card("goal-123456", "Decision", "gate", "cause", "Review", "body",
            [new ControlPlaneAction("Resolve", "resolve-1")]);

        await deliverer.DeliverAsync([open], now);
        await deliverer.DeliverAsync([open with { IsResolved = true }], now.AddMinutes(5));

        var edit = Assert.Single(transport.Edited);
        Assert.Contains("~~[Decision] Review~~", edit.Content);
        Assert.Empty(edit.Buttons);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_budget_folds_overflow_into_one_rollup")]
    public async Task ControlPlaneDelivererBudgetFoldsOverflowIntoOneRollup()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 1, SystemicMergeThreshold: 99);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        var result = await deliverer.DeliverAsync([
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now),
            Card("goal-b", "FailedVerification", "task-2", "cause-2", "Second", "body", raisedAt: now)
        ], now);

        var sent = Assert.Single(transport.Sent);
        Assert.Contains(result.Operations, operation => operation.DedupKey == DiscordControlPlaneDeliverer.PendingRollupKey);
        Assert.Contains("over-budget", sent.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_systemic_merge_collapses_three_same_kind_escalations")]
    public async Task ControlPlaneDelivererSystemicMergeCollapsesThreeSameKindEscalations()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        await deliverer.DeliverAsync([
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now),
            Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: now),
            Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: now)
        ], now);

        var sent = Assert.Single(transport.Sent);
        Assert.Contains("systemic decision", sent.Content);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_systemic_merge_key_is_stable_as_storm_grows")]
    public async Task ControlPlaneDelivererSystemicMergeKeyIsStableAsStormGrows()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T10:29:00Z");
        var firstThree = new[]
        {
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now.AddMinutes(-27)),
            Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: now.AddMinutes(-5)),
            Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: now)
        };

        await deliverer.DeliverAsync(firstThree, now);
        await deliverer.DeliverAsync([
            .. firstThree,
            Card("goal-d", "FailedTask", "task-4", "cause-4", "Fourth", "body", raisedAt: now.AddMinutes(2))
        ], now.AddMinutes(2));

        Assert.Single(transport.Sent);
        var edit = Assert.Single(transport.Edited);
        Assert.Contains("4 FailedTask escalations", edit.Content);
        Assert.Single(store.Marks.Where(mark => mark.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_board_wedge_bypasses_budget_after_collapse")]
    public async Task ControlPlaneDelivererBoardWedgeBypassesBudgetAfterCollapse()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 0, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T23:00:00Z");

        await deliverer.DeliverAsync([
            Card("goal-a", "BoardWedge", "gate", "cause-1", "First", "body", isBoardIntegrity: true, raisedAt: now),
            Card("goal-b", "BoardWedge", "gate", "cause-2", "Second", "body", isBoardIntegrity: true, raisedAt: now),
            Card("goal-c", "BoardWedge", "gate", "cause-3", "Third", "body", isBoardIntegrity: true, raisedAt: now)
        ], now);

        var sent = Assert.Single(transport.Sent);
        Assert.Contains("SystemicBoardWedge", sent.Content);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_quiet_hours_suppress_non_board_and_reminder_caps_at_24h")]
    public async Task ControlPlaneDelivererQuietHoursSuppressNonBoardAndReminderCapsAt24h()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(SystemicMergeThreshold: 99);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var quiet = DateTimeOffset.Parse("2026-07-20T23:00:00Z");
        var daytime = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var card = Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: daytime);

        var quietResult = await deliverer.DeliverAsync([
            card,
            Card("goal-b", "BoardWedge", "gate", "cause-2", "Wedge", "body", isBoardIntegrity: true, raisedAt: quiet)
        ], quiet);
        await deliverer.DeliverAsync([card], daytime);
        await deliverer.DeliverAsync([card], daytime.AddHours(23));
        await deliverer.DeliverAsync([card], daytime.AddHours(24));

        Assert.Contains(quietResult.Operations, operation => operation.Reason == "quiet-hours");
        Assert.Equal(2, transport.Sent.Count);
        Assert.Single(transport.Edited);
        Assert.Equal("reminder", transport.Edited.Count == 1
            ? (await store.TryGetAsync(card.DedupKey))?.LastReminderAt is null ? "missing" : "reminder"
            : "missing");
    }

    [Xunit.Fact(DisplayName = "ControlPlaneMuteControl_parses_slash_mute_and_policy_suppresses_cards")]
    public async Task ControlPlaneMuteControlParsesSlashMuteAndPolicySuppressesCards()
    {
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        Assert.True(ControlPlaneMuteControl.TryParseMuteCommand("/mute 24h", now, out var mutedUntil));
        var transport = new RecordingControlPlaneMessageTransport();
        var deliverer = new DiscordControlPlaneDeliverer(
            new InMemoryControlPlaneDeliveryStore(),
            transport,
            new ControlPlaneDeliveryPolicy(MutedUntil: mutedUntil));

        var result = await deliverer.DeliverAsync([
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now)
        ], now);

        Assert.Empty(transport.Sent);
        Assert.Equal("muted", Assert.Single(result.Operations).Reason);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneReplayHarness_reports_per_channel_would_have_pushed_counts")]
    public async Task ControlPlaneReplayHarnessReportsPerChannelWouldHavePushedCounts()
    {
        var from = DateTimeOffset.Parse("2026-07-20T00:00:00Z");
        var to = from.AddHours(48);
        var report = await new ControlPlaneReplayHarness(new ControlPlaneDeliveryPolicy(SystemicMergeThreshold: 99))
            .ReplayAsync([
                Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: from.AddHours(10)),
                Card("goal-b", "FailedVerification", "task-2", "cause-2", "Second", "body", raisedAt: from.AddHours(11))
            ], from, to);

        Assert.Equal(2, report.Decisions);
        Assert.Equal(1, report.Board);
        Assert.Equal(1, report.Digest);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneReplayHarness_uses_event_times_for_storm_windows")]
    public async Task ControlPlaneReplayHarnessUsesEventTimesForStormWindows()
    {
        var from = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var report = await new ControlPlaneReplayHarness()
            .ReplayAsync([
                Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: from),
                Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: from.AddMinutes(40)),
                Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: from.AddMinutes(50))
            ], from, from.AddHours(2));

        Assert.DoesNotContain(report.Operations, operation => operation.Content.Contains("systemic decision", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "DeadManHeartbeatClient_default_options_do_not_send")]
    public async Task DeadManHeartbeatClientDefaultOptionsDoNotSend()
    {
        var client = new DeadManHeartbeatClient(new HttpClient(new ThrowingHandler()));

        var sent = await client.SendAsync();

        Assert.False(sent);
    }

    [Xunit.Fact(DisplayName = "OperatorInboxControlPlaneProjection_projects_escalation_to_neutral_card")]
    public void OperatorInboxControlPlaneProjectionProjectsEscalationToNeutralCard()
    {
        var item = new OperatorInboxItem(
            "inbox-1",
            OperatorInboxKind.FailedTask,
            OperatorInboxSeverity.Blocker,
            "goal-abcdef",
            "abcdef",
            "objective",
            "task-123",
            1,
            "Task failed",
            "message",
            "evidence",
            "retry",
            "retry abcdef 1",
            "source",
            false,
            null,
            null);

        var card = OperatorInboxControlPlaneProjection.Project(item);

        Assert.Equal(ControlPlaneCardSource.OperatorInboxEscalation, card.Source);
        Assert.Equal("goal-abcdef:failedtask:task-123:" + card.CauseFingerprint, card.DedupKey);
        var action = Assert.Single(card.ActionList);
        var parsed = DiscordInteractionHandler.Process(action.CustomId, "operator-1", "interaction-1", ["operator-1"]);
        Assert.Null(parsed.ErrorMessage);
        Assert.Equal("inbox-1", parsed.Decision?.InboxItemId);
        Assert.Equal(0, parsed.Decision?.ActionIndex);
    }

    private static ControlPlaneDecisionCard Card(
        string goalId,
        string kind,
        string generation,
        string cause,
        string title,
        string body,
        IReadOnlyList<ControlPlaneAction>? actions = null,
        bool isBoardIntegrity = false,
        DateTimeOffset? raisedAt = null) =>
        new(
            ControlPlaneCardSource.OperatorInboxEscalation,
            goalId,
            kind,
            generation,
            cause,
            title,
            body,
            raisedAt ?? DateTimeOffset.Parse("2026-07-20T10:00:00Z"),
            false,
            isBoardIntegrity,
            actions);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Heartbeat should be disabled by default.");
    }
}
