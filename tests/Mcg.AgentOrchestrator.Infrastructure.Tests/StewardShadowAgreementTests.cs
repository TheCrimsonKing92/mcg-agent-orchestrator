using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class StewardShadowAgreementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T18:00:00Z");

    [Fact]
    public void DailyBriefReceiptHashIncludesReportedShadowRates()
    {
        var composer = new StewardComposer();
        var bundle = Bundle("escalation-1");
        var empty = new StewardShadowAgreementWindowRate(0, 0, 0, 0, null);
        var oneMatch = new StewardShadowAgreementWindowRate(1, 0, 0, 1, 1);
        var oneMismatch = new StewardShadowAgreementWindowRate(0, 1, 0, 1, 0);
        var matching = new[] { new StewardShadowClassAgreementRate(
            StewardShadowEscalationClass.PlannerOutputContractRejected, oneMatch, oneMatch, 0) };
        var differing = new[] { new StewardShadowClassAgreementRate(
            StewardShadowEscalationClass.PlannerOutputContractRejected, oneMismatch, empty, 0) };

        var baseline = composer.ComposeDailyBrief(bundle, "next", 0, Now);
        var withMatchingRate = composer.ComposeDailyBrief(bundle, "next", 0, Now, matching);
        var withDifferingRate = composer.ComposeDailyBrief(bundle, "next", 0, Now, differing);

        Assert.Equal(StewardInputHasher.Hash(new { bundle, next = "next", spend = 0m }),
            baseline.Receipt.InputsHash);
        Assert.NotEqual(baseline.Receipt.InputsHash, withMatchingRate.Receipt.InputsHash);
        Assert.NotEqual(withMatchingRate.Receipt.InputsHash, withDifferingRate.Receipt.InputsHash);
    }

    [Fact]
    public async Task OperatorIntentMatchesOrMismatchesAndHeartbeatReportsClassRates()
    {
        var store = new InMemoryStewardShadowRecommendationStore();
        var reader = new InMemoryStewardOperatorIntentReader();
        var shadow = new StewardShadowAdjudicator(store, reader);
        var dispatcher = new StewardDispatcher(new StewardComposer(),
            new InMemoryStewardTriageReceiptStore(), new RecordingControlPlaneMessageTransport(),
            shadowAdjudicator: shadow);
        await dispatcher.DispatchAsync(Bundle("escalation-1"), Now);
        reader.AddObserved(new StewardObservedOperatorIntent("intent-1", "retry", "goal-123", "task-1",
            "{\"retryCause\":\"ContractClarification\"}", Now.AddMinutes(1), OperatorActorKind.Human));
        await dispatcher.DispatchAsync(Bundle("unused") with { Escalations = [] }, Now.AddMinutes(1));
        Assert.Equal(StewardShadowAgreementOutcome.Match, Assert.Single(store.Agreements).Outcome);

        await dispatcher.DispatchAsync(Bundle("escalation-2"), Now.AddMinutes(2));
        reader.AddObserved(new StewardObservedOperatorIntent("intent-2", "progress", "goal-123", "task-1",
            "{}", Now.AddMinutes(3), OperatorActorKind.Human));
        await dispatcher.DispatchAsync(Bundle("unused") with { Escalations = [] }, Now.AddMinutes(3));
        var mismatch = Assert.Single(store.Agreements.Where(x => x.OperatorIntentId == "intent-2"));
        Assert.Equal(StewardShadowAgreementOutcome.Mismatch, mismatch.Outcome);
        Assert.Equal(StewardShadowDifferingField.Verb, mismatch.PrimaryDifferingField);

        var heartbeat = await dispatcher.GetHeartbeatAsync(Now.AddMinutes(4));
        var classRate = Assert.Single(heartbeat.ShadowAgreement!.Where(x =>
            x.Class == StewardShadowEscalationClass.PlannerOutputContractRejected));
        Assert.Equal(2, classRate.AllTime.N);
        Assert.Equal(0.5, classRate.AllTime.Rate);
        Assert.Equal(2, classRate.Trailing14Days.N);
        Assert.Equal(0, classRate.Pending);
        var brief = (await dispatcher.ComposeDailyBriefAsync(
            Bundle("escalation-2"), "next", 0, Now.AddMinutes(4))).Value;
        var briefRate = Assert.Single(brief.ShadowAgreement!.Where(x =>
            x.Class == StewardShadowEscalationClass.PlannerOutputContractRejected));
        Assert.Equal(2, briefRate.AllTime.N);
        Assert.Equal(0.5, briefRate.AllTime.Rate);
    }

    [Fact]
    public async Task OutsideVocabularyIsRecordedButExcludedFromDenominator()
    {
        var store = new InMemoryStewardShadowRecommendationStore();
        var reader = new InMemoryStewardOperatorIntentReader();
        var shadow = new StewardShadowAdjudicator(store, reader);
        await shadow.RecordAsync(Bundle("escalation-1"), Now);
        reader.AddObserved(new StewardObservedOperatorIntent("intent-1", "park", "goal-123", "task-1",
            "{}", Now.AddMinutes(1), OperatorActorKind.Human));
        await shadow.ReconcileAsync();
        var agreement = Assert.Single(store.Agreements);
        Assert.Equal(StewardShadowAgreementOutcome.NotComparable, agreement.Outcome);
        Assert.Equal("verb-outside-vocabulary", agreement.NotComparableReason);
        var rate = Assert.Single((await shadow.RatesAsync(Now)).Where(x =>
            x.Class == StewardShadowEscalationClass.PlannerOutputContractRejected));
        Assert.Equal(0, rate.AllTime.N);
        Assert.Equal(1, rate.AllTime.NotComparable);
        Assert.Null(rate.AllTime.Rate);
    }

    [Fact]
    public async Task SqliteReaderObservesExistingOperatorIntentWithoutChangingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "steward-intent-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "operator-intents.db");
            var writer = new SqliteOperatorIntentStore(path, directory);
            var original = new OperatorIntentRecord("intent-1", "key-1", "retry", "goal-123", "task-1",
                "{}", [], "human", "cli", "local", Now);
            await writer.EnqueueAsync(original);

            var observed = Assert.Single(await new SqliteStewardOperatorIntentReader(path)
                .ListForGoalAsync("goal-123"));
            Assert.Equal(original.Id, observed.Id);
            Assert.Equal(original.Verb, observed.Verb);
            Assert.Equal(original.TaskId, observed.TaskId);
            Assert.Equal(OperatorActorKind.Human, observed.ActorKind);
            Assert.Equal(OperatorIntentStatus.Pending, (await writer.GetAsync(original.Id))!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OneIntentResolvesOnlyMostRecentUnresolvedRecommendation()
    {
        var store = new InMemoryStewardShadowRecommendationStore();
        var reader = new InMemoryStewardOperatorIntentReader();
        var shadow = new StewardShadowAdjudicator(store, reader);
        await shadow.RecordAsync(Bundle("escalation-1"), Now);
        await shadow.RecordAsync(Bundle("escalation-2"), Now.AddMinutes(1));
        reader.AddObserved(new StewardObservedOperatorIntent("intent-1", "retry", "goal-123", "task-1",
            "{\"retryCause\":\"ContractClarification\"}", Now.AddMinutes(2), OperatorActorKind.Human));

        await shadow.ReconcileAsync();
        await shadow.ReconcileAsync();

        Assert.Single(store.Agreements);
        Assert.Equal("escalation-2", Assert.Single(store.Recommendations.Where(x =>
            x.Id == store.Agreements[0].RecommendationId)).EscalationId);
        var rate = Assert.Single((await shadow.RatesAsync(Now.AddMinutes(3))).Where(x =>
            x.Class == StewardShadowEscalationClass.PlannerOutputContractRejected));
        Assert.Equal(1, rate.Pending);
        Assert.Equal(1, rate.AllTime.N);
    }

    private static StewardBriefingBundle Bundle(string id) => new(
        [new StewardEscalationItem(id, StewardEscalationCategory.Normal, "goal-123",
            "planner-output-contract-rejected", "diagnostic", 2, Now, "title", "diagnostic", "task-1")],
        [], new StewardNumericReceiptSummary(0, 0, [], 0, 0, 0), [],
        new StewardPolicySnapshot("policy", [], Now),
        new StewardInterruptBudgetLedger(1, 0, 1, Now), [], []);
}
