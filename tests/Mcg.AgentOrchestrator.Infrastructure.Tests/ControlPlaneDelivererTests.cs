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
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now.AddHours(-1)),
            Card("goal-b", "FailedVerification", "task-2", "cause-2", "Second", "body", raisedAt: now.AddHours(-1))
        ], now);

        var sent = Assert.Single(transport.Sent);
        Assert.Contains(result.Operations, operation => operation.DedupKey == DiscordControlPlaneDeliverer.PendingRollupKey);
        Assert.Contains("over-budget", sent.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_decision_budget_counts_content_change_edits")]
    public async Task ControlPlaneDelivererDecisionBudgetCountsContentChangeEdits()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 2, SystemicMergeThreshold: 99);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var original = Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now.AddHours(-1));

        await deliverer.DeliverAsync([original], now);
        await deliverer.DeliverAsync([original with { Title = "Updated" }], now.AddMinutes(5));
        var exhausted = await deliverer.DeliverAsync([
            Card("goal-b", "FailedVerification", "task-2", "cause-2", "Second", "body", raisedAt: now.AddHours(-1))
        ], now.AddMinutes(10));

        Assert.Single(transport.Sent);
        Assert.Single(transport.Edited);
        Assert.Equal(2, await store.CountPushesAsync(ControlPlaneDeliveryChannel.Decisions, now.AddHours(-24)));
        Assert.Contains(exhausted.Operations, operation => operation.Reason == "decision-budget");
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

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_incremental_same_kind_storm_sends_only_systemic_card")]
    public async Task ControlPlaneDelivererIncrementalSameKindStormSendsOnlySystemicCard()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var start = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var first = Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: start);
        var second = Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: start.AddMinutes(10));
        var third = Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: start.AddMinutes(20));

        await deliverer.DeliverAsync([first], start);
        await deliverer.DeliverAsync([first, second], start.AddMinutes(10));
        await deliverer.DeliverAsync([first, second, third], start.AddMinutes(20));

        var sent = Assert.Single(transport.Sent);
        Assert.Contains("SystemicFailedTask", sent.Content);
        Assert.DoesNotContain(store.Marks, mark =>
            mark.DedupKey.Contains(":failedtask:", StringComparison.OrdinalIgnoreCase) &&
            !mark.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_existing_systemic_merge_keeps_aged_originals_collapsed")]
    public async Task ControlPlaneDelivererExistingSystemicMergeKeepsAgedOriginalsCollapsed()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var start = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var cards = new[]
        {
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: start),
            Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: start.AddMinutes(1)),
            Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: start.AddMinutes(2))
        };

        await deliverer.DeliverAsync(cards, start.AddMinutes(2));
        var later = await deliverer.DeliverAsync(cards, start.AddHours(2));

        Assert.Single(transport.Sent);
        Assert.DoesNotContain(later.Operations, operation => operation.Reason == "new-card");
        Assert.Contains(later.Operations, operation => operation.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_existing_systemic_merge_keeps_below_threshold_originals_collapsed")]
    public async Task ControlPlaneDelivererExistingSystemicMergeKeepsBelowThresholdOriginalsCollapsed()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var start = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var cards = new[]
        {
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: start),
            Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: start.AddMinutes(1)),
            Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: start.AddMinutes(2))
        };

        await deliverer.DeliverAsync(cards, start.AddMinutes(2));
        var later = await deliverer.DeliverAsync(cards.Take(2), start.AddHours(2));

        Assert.Single(transport.Sent);
        Assert.Single(transport.Edited);
        Assert.DoesNotContain(later.Operations, operation => operation.Reason == "new-card");
        Assert.Contains(later.Operations, operation => operation.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_existing_systemic_merge_resolves_when_underlying_cards_resolve")]
    public async Task ControlPlaneDelivererExistingSystemicMergeResolvesWhenUnderlyingCardsResolve()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var start = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var cards = new[]
        {
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: start),
            Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: start.AddMinutes(1)),
            Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: start.AddMinutes(2))
        };

        await deliverer.DeliverAsync(cards, start.AddMinutes(2));
        var resolved = await deliverer.DeliverAsync(cards.Select(card => card with { IsResolved = true }), start.AddMinutes(10));

        Assert.Single(transport.Sent);
        var edit = Assert.Single(transport.Edited);
        Assert.Contains("~~[SystemicFailedTask] FailedTask escalation storm resolved~~", edit.Content);
        Assert.Empty(edit.Buttons);
        Assert.Contains(resolved.Operations, operation =>
            operation.Reason == "resolved" &&
            operation.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(await store.ListSystemicStormsAsync(), state => state.Kind == "FailedTask");
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_existing_systemic_merge_resolves_when_kind_disappears")]
    public async Task ControlPlaneDelivererExistingSystemicMergeResolvesWhenKindDisappears()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 6, SystemicMergeThreshold: 3);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var start = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var cards = new[]
        {
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: start),
            Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: start.AddMinutes(1)),
            Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: start.AddMinutes(2))
        };

        await deliverer.DeliverAsync(cards, start.AddMinutes(2));
        var resolved = await deliverer.DeliverAsync([], start.AddMinutes(10));

        Assert.Single(transport.Sent);
        var edit = Assert.Single(transport.Edited);
        Assert.Contains("~~[Systemic", edit.Content);
        Assert.Empty(edit.Buttons);
        Assert.Contains(resolved.Operations, operation =>
            operation.Reason == "resolved" &&
            operation.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await store.ListSystemicStormsAsync());
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_unchanged_delivered_cards_do_not_create_pending_rollup")]
    public async Task ControlPlaneDelivererUnchangedDeliveredCardsDoNotCreatePendingRollup()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 3, SystemicMergeThreshold: 99);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var cards = new[]
        {
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now.AddHours(-1)),
            Card("goal-b", "FailedVerification", "task-2", "cause-2", "Second", "body", raisedAt: now.AddHours(-1))
        };

        await deliverer.DeliverAsync(cards, now);
        var unchanged = await deliverer.DeliverAsync(cards, now.AddMinutes(5));

        Assert.Equal(2, transport.Sent.Count);
        Assert.Empty(transport.Edited);
        Assert.DoesNotContain(store.Marks, mark => mark.DedupKey == DiscordControlPlaneDeliverer.PendingRollupKey);
        Assert.All(unchanged.Operations, operation => Assert.Equal("dedup", operation.Reason));
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_resolved_unseen_card_is_suppressed")]
    public async Task ControlPlaneDelivererResolvedUnseenCardIsSuppressed()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var deliverer = new DiscordControlPlaneDeliverer(store, transport);
        var card = Card("goal-a", "Decision", "gate", "cause", "Resolved", "body") with { IsResolved = true };

        var result = await deliverer.DeliverAsync([card], DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        Assert.Empty(transport.Sent);
        Assert.Equal("resolved-unseen", Assert.Single(result.Operations).Reason);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneDeliverer_resolved_existing_card_bypasses_exhausted_budget")]
    public async Task ControlPlaneDelivererResolvedExistingCardBypassesExhaustedBudget()
    {
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var policy = new ControlPlaneDeliveryPolicy(DailyDecisionBudget: 1, SystemicMergeThreshold: 99);
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, policy);
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var card = Card("goal-a", "Decision", "gate", "cause", "Review", "body",
            [new ControlPlaneAction("Resolve", "resolve-1")]);

        await deliverer.DeliverAsync([card], now);
        var resolved = await deliverer.DeliverAsync([card with { IsResolved = true }], now.AddHours(1));

        Assert.Single(transport.Sent);
        var edit = Assert.Single(transport.Edited);
        Assert.Empty(edit.Buttons);
        Assert.Contains("~~[Decision] Review~~", edit.Content);
        Assert.Equal("resolved", Assert.Single(resolved.Operations).Reason);
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
        var card = Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: daytime.AddHours(-1));

        var quietResult = await deliverer.DeliverAsync([
            card,
            Card("goal-b", "BoardWedge", "gate", "cause-2", "Wedge", "body", isBoardIntegrity: true, raisedAt: quiet)
        ], quiet);
        await deliverer.DeliverAsync([card], daytime);
        var earlyReminder = await deliverer.DeliverAsync([card], daytime.AddHours(23));
        var dueReminder = await deliverer.DeliverAsync([card], daytime.AddHours(24));

        Assert.Contains(quietResult.Operations, operation => operation.Reason == "quiet-hours");
        Assert.Equal("dedup", Assert.Single(earlyReminder.Operations).Reason);
        Assert.Equal("reminder", Assert.Single(dueReminder.Operations).Reason);
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
            Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: now.AddHours(-1))
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

        var decisionPushes = report.Operations
            .Where(operation => operation.Channel == ControlPlaneDeliveryChannel.Decisions)
            .Where(operation => operation.Kind is ControlPlaneDeliveryOperationKind.Send or ControlPlaneDeliveryOperationKind.Edit)
            .Select(operation => operation.Reason)
            .ToList();
        Assert.Equal(["new-card", "new-card"], decisionPushes);
        Assert.Equal(2, report.Decisions);
        Assert.Equal(1, report.Board);
        Assert.Equal(1, report.Digest);
    }

    [Xunit.Fact(DisplayName = "ControlPlaneReplayHarness_includes_daily_backlog_digest_when_backlog_items_are_supplied")]
    public async Task ControlPlaneReplayHarnessIncludesDailyBacklogDigestWhenBacklogItemsAreSupplied()
    {
        var from = DateTimeOffset.Parse("2026-07-20T00:00:00Z");
        var backlog = new[]
        {
            new ControlPlaneBacklogDigestItem("backlog-a", "Operator channel follow-up", BacklogItemStatus.Open, from.AddHours(6), "goal-a")
        };

        var report = await new ControlPlaneReplayHarness(new ControlPlaneDeliveryPolicy(SystemicMergeThreshold: 99))
            .ReplayAsync(
                [Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: from.AddHours(10))],
                backlog,
                from,
                from.AddHours(48));

        var daily = Assert.Single(report.Operations, operation => operation.Reason == "daily-backlog-digest");
        Assert.Equal(ControlPlaneDeliveryChannel.Digest, daily.Channel);
        Assert.Contains("Daily backlog digest", daily.Content);
        Assert.Contains("Operator channel follow-up", daily.Content);
        Assert.Equal(2, report.Digest);
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

    [Xunit.Fact(DisplayName = "ControlPlaneReplayHarness_incremental_same_kind_storm_sends_only_systemic_card")]
    public async Task ControlPlaneReplayHarnessIncrementalSameKindStormSendsOnlySystemicCard()
    {
        var from = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var report = await new ControlPlaneReplayHarness()
            .ReplayAsync([
                Card("goal-a", "FailedTask", "task-1", "cause-1", "First", "body", raisedAt: from),
                Card("goal-b", "FailedTask", "task-2", "cause-2", "Second", "body", raisedAt: from.AddMinutes(10)),
                Card("goal-c", "FailedTask", "task-3", "cause-3", "Third", "body", raisedAt: from.AddMinutes(20))
            ], from, from.AddHours(2));

        var decisionPushes = report.Operations
            .Where(operation => operation.Channel == ControlPlaneDeliveryChannel.Decisions)
            .Where(operation => operation.Kind is ControlPlaneDeliveryOperationKind.Send or ControlPlaneDeliveryOperationKind.Edit)
            .ToList();
        var pushed = Assert.Single(decisionPushes);
        Assert.Contains(":systemicfailedtask:", pushed.DedupKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(report.Operations, operation =>
            operation.Reason == "new-card" &&
            operation.DedupKey.Contains(":failedtask:", StringComparison.OrdinalIgnoreCase) &&
            !operation.DedupKey.Contains(":systemicfailedtask:", StringComparison.OrdinalIgnoreCase));
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
                ForumChannelId: "42",
                DeadManHeartbeatEnabled: true,
                DeadManHeartbeatUrl: "https://example.test/deadman"));

        Assert.False(disabled.Enabled);
        Assert.True(enabled.Enabled);
        Assert.Equal(new Uri("https://example.test/deadman"), enabled.Endpoint);
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
            DateTimeOffset.Parse("2026-07-20T09:00:00Z"),
            false,
            null,
            null);

        var card = OperatorInboxControlPlaneProjection.Project(item);

        Assert.Equal(ControlPlaneCardSource.OperatorInboxEscalation, card.Source);
        Assert.Equal(DateTimeOffset.Parse("2026-07-20T09:00:00Z"), card.RaisedAt);
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
            raisedAt ?? DateTimeOffset.Parse("2026-07-20T09:00:00Z"),
            false,
            isBoardIntegrity,
            actions);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Heartbeat should be disabled by default.");
    }
}
