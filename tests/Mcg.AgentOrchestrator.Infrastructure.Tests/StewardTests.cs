using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class StewardTests
{
    [Xunit.Fact(DisplayName = "StewardWakeScheduler_dispatches_only_event_reasons_and_debounces")]
    public void StewardWakeSchedulerDispatchesOnlyEventReasonsAndDebounces()
    {
        var scheduler = new StewardWakeScheduler(new StewardDispatchOptions(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5)));
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        var dispatch = scheduler.Evaluate(
            [new StewardWakeSignal(StewardWakeReason.EscalationInboxItem, "inbox-1", now)],
            [],
            now);
        var debounced = scheduler.Evaluate(
            [new StewardWakeSignal(StewardWakeReason.DigestSchedule, "daily", now.AddSeconds(30))],
            [Receipt(createdAt: now)],
            now.AddSeconds(30));
        var noPerTick = scheduler.Evaluate([], [], now.AddMinutes(10));

        Assert.Equal(StewardWakeDecisionKind.Dispatch, dispatch.Kind);
        Assert.Equal(StewardWakeDecisionKind.Debounced, debounced.Kind);
        Assert.Equal(StewardWakeDecisionKind.NoWakeSignal, noPerTick.Kind);
    }

    [Xunit.Fact(DisplayName = "StewardDispatchOptions_rejects_non_v0_debounce_windows")]
    public void StewardDispatchOptionsRejectsNonV0DebounceWindows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StewardDispatchOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StewardDispatchOptions(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(5)));
    }

    [Xunit.Fact(DisplayName = "StewardDispatcher_fail_open_routes_raw_when_engine_times_out")]
    public async Task StewardDispatcherFailOpenRoutesRawWhenEngineTimesOut()
    {
        var store = new InMemoryStewardTriageReceiptStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var dispatcher = new StewardDispatcher(
            new DelayingEngine(TimeSpan.FromSeconds(30)),
            store,
            transport,
            new StewardDispatchOptions(TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(10)));

        var result = await dispatcher.DispatchAsync(Bundle(), DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        Assert.True(result.FailedOpen);
        Assert.Single(transport.Sent);
        Assert.Contains("RAW fail-open", transport.Sent.Single().Content);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal(StewardOutputKind.FailOpenRawEscalation, receipt.OutputKind);
        Assert.NotEmpty(receipt.InputsHash);
        Assert.Equal(StewardDispositionKind.RaisedRaw, Assert.Single(receipt.Dispositions).Kind);
    }

    [Xunit.Fact(DisplayName = "StewardDispatcher_fail_open_routes_raw_when_engine_errors")]
    public async Task StewardDispatcherFailOpenRoutesRawWhenEngineErrors()
    {
        var store = new InMemoryStewardTriageReceiptStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var dispatcher = new StewardDispatcher(
            new ThrowingEngine(),
            store,
            transport,
            new StewardDispatchOptions(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5)));

        var result = await dispatcher.DispatchAsync(Bundle(), DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        Assert.True(result.FailedOpen);
        Assert.Single(transport.Sent);
        Assert.Equal(StewardOutputKind.FailOpenRawEscalation, Assert.Single(store.Receipts).OutputKind);
    }

    [Xunit.Fact(DisplayName = "StewardBriefingBundle_carries_typed_numeric_fields_and_labeled_worker_quotes")]
    public void StewardBriefingBundleCarriesTypedNumericFieldsAndLabeledWorkerQuotes()
    {
        var bundle = Bundle();

        Assert.Equal(2, bundle.Receipts.TrxCount);
        Assert.Equal([0, 1], bundle.Receipts.ExitCodes);
        Assert.Equal("FailedTask", Assert.Single(bundle.MatchedPrecedents).Kind);
        Assert.Equal("safe-auto", bundle.PolicySnapshot.AutonomyPolicyName);
        Assert.Equal(3, bundle.InterruptBudget.Remaining);
        Assert.Equal("tester", Assert.Single(bundle.WorkerProse).Label);
        Assert.NotEmpty(bundle.InputsHash());
    }

    [Xunit.Fact(DisplayName = "StewardComposer_every_output_is_receipt_with_inputs_hash")]
    public void StewardComposerEveryOutputIsReceiptWithInputsHash()
    {
        var composer = new StewardComposer();
        var bundle = Bundle();
        var now = DateTimeOffset.Parse("2026-07-20T10:00:00Z");

        var outputs = new[]
        {
            composer.ComposeDecisionCard(bundle, bundle.Escalations[0], now).Receipt,
            composer.ComposeStormCollapseJudgment(bundle, "FailedTask", 1, now).Receipt,
            composer.ComposeDailyBacklogDigest(bundle, [new StewardBacklogCandidate("bcfa", "Steward v0", 10, 3, ["operator-comms"])], now).Receipt,
            composer.ComposeDailyBrief(bundle, "inspect", 12.34m, now).Receipt,
            composer.ComposeCatchUpReplay(
                bundle,
                now.AddHours(-1),
                now,
                new StewardBoardStateDiff(1, -1, 1),
                [new StewardAutonomousAction("action-1", "dispatch", "receipt-1", now.AddMinutes(-10))]).Receipt
        };

        Assert.All(outputs, receipt => Assert.NotEmpty(receipt.InputsHash));
    }

    [Xunit.Fact(DisplayName = "StewardComposer_composes_numbers_not_prose_decision_card_with_quoted_worker_prose")]
    public void StewardComposerComposesNumbersNotProseDecisionCardWithQuotedWorkerProse()
    {
        var output = new StewardComposer().ComposeDecisionCard(
            Bundle(),
            Bundle().Escalations[0],
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        Assert.Equal(ControlPlaneCardSource.StewardTriage, output.Value.Source);
        Assert.Contains("trx=2 failedTrx=1 exitCodes=0,1", output.Value.Body);
        Assert.Contains("diffFiles=4 insertions=30 deletions=2", output.Value.Body);
        Assert.Contains("worker-prose[tester] quoted:", output.Value.Body);
        Assert.Equal(StewardOutputKind.DecisionCard, output.Receipt.OutputKind);
    }

    [Xunit.Fact(DisplayName = "StewardConservationRule_has_no_dismissed_without_disposition_transition")]
    public void StewardConservationRuleHasNoDismissedWithoutDispositionTransition()
    {
        var names = Enum.GetNames<StewardDispositionKind>();

        Assert.DoesNotContain("Dismissed", names);
        Assert.DoesNotContain("DismissedWithoutDisposition", names);
        Assert.Throws<ArgumentException>(() => StewardInboxDisposition.CardCreated("inbox-1", ""));
    }

    [Xunit.Fact(DisplayName = "StewardBypassList_routes_categories_raw_even_when_engine_would_compose")]
    public async Task StewardBypassListRoutesCategoriesRawEvenWhenEngineWouldCompose()
    {
        var store = new InMemoryStewardTriageReceiptStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var dispatcher = new StewardDispatcher(
            new StewardComposer(),
            store,
            transport,
            new StewardDispatchOptions(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5)));
        var bundle = Bundle(escalations:
        [
            Escalation("board-1", StewardEscalationCategory.BoardWedge, "BoardWedge"),
            Escalation("sec-1", StewardEscalationCategory.SecurityOwnership, "SecurityOwnership"),
            Escalation("deny-1", StewardEscalationCategory.DenylistHitLanding, "DenylistHitLanding"),
            Escalation("steward-1", StewardEscalationCategory.StewardFailure, "StewardFailure")
        ]);

        var result = await dispatcher.DispatchAsync(bundle, DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        Assert.False(result.FailedOpen);
        Assert.Empty(result.Cards);
        Assert.Equal(4, transport.Sent.Count);
        Assert.All(store.Receipts, receipt => Assert.Equal(StewardOutputKind.RawBypassEscalation, receipt.OutputKind));
    }

    [Xunit.Fact(DisplayName = "Steward_has_no_task_verification_acceptance_or_landing_mutation_dependency")]
    public void StewardHasNoTaskVerificationAcceptanceOrLandingMutationDependency()
    {
        var forbiddenNames = new[]
        {
            "TaskStore",
            "Verification",
            "Acceptance",
            "Landing",
            "GoalAcceptanceVerifier",
            "LandingExecutor",
            "ConductorDriver",
            "AgentOrchestratorKernel"
        };
        var stewardTypes = typeof(StewardComposer).Assembly.GetTypes()
            .Where(type => type.Namespace == "Mcg.AgentOrchestrator.Infrastructure" &&
                type.Name.Contains("Steward", StringComparison.Ordinal))
            .ToList();
        var dependencyNames = stewardTypes
            .SelectMany(type => type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();
        dependencyNames.AddRange(stewardTypes
            .SelectMany(type => type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            .Select(field => field.FieldType.Name));

        foreach (var forbidden in forbiddenNames)
            Assert.DoesNotContain(dependencyNames, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("steward-triage", ModelFunctionPurposes.StewardTriage);
    }

    [Xunit.Fact(DisplayName = "StewardHeartbeat_reports_triaged_acted_raised_ratios")]
    public void StewardHeartbeatReportsTriagedActedRaisedRatios()
    {
        var heartbeat = StewardHeartbeatCalculator.FromReceipts(
        [
            Receipt("receipt-1", ["inbox-1", "inbox-2"], [
                StewardInboxDisposition.CardCreated("inbox-1", "card-1"),
                StewardInboxDisposition.RaisedRaw("inbox-2", "raw-1")
            ])
        ]);

        Assert.Equal(2, heartbeat.Triaged);
        Assert.Equal(1, heartbeat.Acted);
        Assert.Equal(1, heartbeat.Raised);
        Assert.Equal(0.5, heartbeat.ActedRatio);
        Assert.Equal(0.5, heartbeat.RaisedRatio);
    }

    [Xunit.Fact(DisplayName = "StewardMeasurement_emits_cards_per_landed_goal_split_novel_and_precedent")]
    public void StewardMeasurementEmitsCardsPerLandedGoalSplitNovelAndPrecedent()
    {
        var novel = new StewardComposer().ComposeDecisionCard(
            Bundle(precedents: []),
            Escalation("inbox-2", StewardEscalationCategory.Normal, "FailedVerification", "new-cause"),
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        var covered = new StewardComposer().ComposeDecisionCard(
            Bundle(),
            Bundle().Escalations[0],
            DateTimeOffset.Parse("2026-07-20T10:00:00Z"));

        Assert.Equal(1, novel.Receipt.CardLoadMeasurement?.NovelCards);
        Assert.Equal(0.5, novel.Receipt.CardLoadMeasurement?.NovelCardsPerLandedGoal);
        Assert.Equal(1, covered.Receipt.CardLoadMeasurement?.PrecedentCoveredCards);
        Assert.Equal(0.5, covered.Receipt.CardLoadMeasurement?.PrecedentCoveredCardsPerLandedGoal);
    }

    [Xunit.Fact(DisplayName = "SqliteStewardTriageReceiptStore_appends_queryable_receipts")]
    public async Task SqliteStewardTriageReceiptStoreAppendsQueryableReceipts()
    {
        var db = Path.Combine(Path.GetTempPath(), $"steward-{Guid.NewGuid():N}", "receipts.db");
        var store = new SqliteStewardTriageReceiptStore(db);
        var receipt = Receipt();

        await store.AppendAsync(receipt);
        var loaded = Assert.Single(await store.ListAsync());

        Assert.Equal(receipt.Id, loaded.Id);
        Assert.Equal(receipt.InputsHash, loaded.InputsHash);
        Assert.Equal(receipt.OutputKind, loaded.OutputKind);
    }

    private static StewardBriefingBundle Bundle(
        IReadOnlyList<StewardEscalationItem>? escalations = null,
        IReadOnlyList<StewardPrecedentMatch>? precedents = null) =>
        new(
            escalations ?? [Escalation("inbox-1", StewardEscalationCategory.Normal, "FailedTask")],
            [
                new StewardGoalTaskRecord("goal-123456", "task-1", "Landed", 0, 0, 0),
                new StewardGoalTaskRecord("goal-222222", "task-2", "Landed", 0, 0, 0)
            ],
            new StewardNumericReceiptSummary(2, 1, [0, 1], 4, 30, 2),
            precedents ?? [new StewardPrecedentMatch("FailedTask", "cause", 3, 2)],
            new StewardPolicySnapshot("safe-auto", ["land-deny"], DateTimeOffset.Parse("2026-07-20T09:00:00Z")),
            new StewardInterruptBudgetLedger(6, 3, 3, DateTimeOffset.Parse("2026-07-20T00:00:00Z")),
            [new StewardProvenanceLink("tester", "trx-1", 1, DateTimeOffset.Parse("2026-07-20T09:30:00Z"))],
            [new StewardQuotedWorkerProse("tester", "I saw one failing assertion.")]);

    private static StewardEscalationItem Escalation(
        string id,
        StewardEscalationCategory category,
        string kind,
        string cause = "cause") =>
        new(
            id,
            category,
            "goal-123456",
            kind,
            cause,
            3,
            DateTimeOffset.Parse("2026-07-20T09:00:00Z"),
            $"{kind} needs review",
            "evidence=1");

    private static StewardTriageReceipt Receipt(
        string id = "receipt-1",
        IReadOnlyList<string>? inputs = null,
        IReadOnlyList<StewardInboxDisposition>? dispositions = null,
        DateTimeOffset? createdAt = null) =>
        new(
            id,
            StewardOutputKind.DecisionCard,
            "hash",
            createdAt ?? DateTimeOffset.Parse("2026-07-20T10:00:00Z"),
            inputs ?? ["inbox-1"],
            dispositions ?? [StewardInboxDisposition.CardCreated("inbox-1", "card-1")],
            "summary");

    private sealed class DelayingEngine : IStewardTriageEngine
    {
        private readonly TimeSpan _delay;

        public DelayingEngine(TimeSpan delay)
        {
            _delay = delay;
        }

        public async Task<StewardTriageBatch> TriageAsync(
            StewardBriefingBundle bundle,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new StewardTriageBatch([], []);
        }
    }

    private sealed class ThrowingEngine : IStewardTriageEngine
    {
        public Task<StewardTriageBatch> TriageAsync(
            StewardBriefingBundle bundle,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
