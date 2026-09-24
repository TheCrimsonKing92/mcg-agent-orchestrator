using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class StewardShadowAdjudicationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T18:00:00Z");

    [Theory]
    [InlineData("planner-output-contract-rejected", "diagnostic", "retry", "ContractClarification")]
    [InlineData("required-file-change-evidence-missing", "no-change", "progress", null)]
    [InlineData("gate-failure-untouched-test", "known-flake", "retry", "UnchangedContextRepeat")]
    [InlineData("obligation-bound-to-pre-rebase-commit", "binding", "criterion-evidence-map", null)]
    public async Task MatchingEscalationRecordsShadowDecisionWithoutSubmittingIntent(
        string kind, string cause, string verb, string? expectedCause)
    {
        var item = Escalation(kind, cause);
        var bundle = Bundle(item);
        var store = new InMemoryStewardShadowRecommendationStore();
        var reader = new InMemoryStewardOperatorIntentReader();
        var transport = new RecordingControlPlaneMessageTransport();
        var shadow = new StewardShadowAdjudicator(store, reader,
            new StewardShadowAdjudicationOptions(["known-flake"], TimeSpan.FromDays(14)));
        var dispatcher = new StewardDispatcher(new StewardComposer(),
            new InMemoryStewardTriageReceiptStore(), transport, shadowAdjudicator: shadow);

        await dispatcher.DispatchAsync(bundle, Now);

        var receipt = Assert.Single(store.Recommendations);
        Assert.Equal(bundle.InputsHash(), receipt.InputsHash);
        Assert.Equal("goal-123", receipt.Decision.GoalId);
        Assert.Equal("task-1", receipt.Decision.TaskId);
        Assert.Equal(verb, receipt.Decision.Verb);
        Assert.Equal(expectedCause, receipt.Decision.Cause);
        Assert.Empty(await reader.ListForGoalAsync("goal-123"));
        Assert.DoesNotContain(transport.Sent, x => x.Content.Contains("shadow", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(StewardShadowAdjudicator).Assembly.GetReferencedAssemblies(),
            x => x.Name == "Mcg.AgentOrchestrator.Infrastructure");
    }

    [Fact]
    public async Task UnknownAndIncompleteEscalationsAbstainAndRepeatedWakeIsIdempotent()
    {
        var store = new InMemoryStewardShadowRecommendationStore();
        var shadow = new StewardShadowAdjudicator(store, new InMemoryStewardOperatorIntentReader());
        var unknown = Bundle(Escalation("unknown-kind", "cause"));
        await shadow.RecordAsync(unknown, Now);
        await shadow.RecordAsync(Bundle(Escalation("planner-output-contract-rejected", "diagnostic") with { TaskId = null }), Now);
        Assert.Empty(store.Recommendations);

        var known = Bundle(Escalation("planner-output-contract-rejected", "diagnostic"));
        await shadow.RecordAsync(known, Now);
        await shadow.RecordAsync(known, Now);
        Assert.Single(store.Recommendations);
    }

    [Fact]
    public async Task AmbiguousEscalationAbstainsAndShadowStoreFailureDoesNotChangeTriage()
    {
        var ambiguous = Bundle(Escalation("planner-output-contract-rejected",
            "required-file-change-evidence-missing"));
        var store = new InMemoryStewardShadowRecommendationStore();
        var shadow = new StewardShadowAdjudicator(store, new InMemoryStewardOperatorIntentReader());
        await shadow.RecordAsync(ambiguous, Now);
        Assert.Empty(store.Recommendations);

        var bundle = Bundle(Escalation("planner-output-contract-rejected", "diagnostic"));
        var ordinary = await new StewardDispatcher(new StewardComposer(),
            new InMemoryStewardTriageReceiptStore(), new RecordingControlPlaneMessageTransport())
            .DispatchAsync(bundle, Now);
        var shadowed = await new StewardDispatcher(new StewardComposer(),
            new InMemoryStewardTriageReceiptStore(), new RecordingControlPlaneMessageTransport(),
            shadowAdjudicator: new StewardShadowAdjudicator(new ThrowingStewardShadowStore(),
                new InMemoryStewardOperatorIntentReader())).DispatchAsync(bundle, Now);
        Assert.Equal(ordinary.FailedOpen, shadowed.FailedOpen);
        Assert.Equal(ordinary.Cards.Count, shadowed.Cards.Count);
        Assert.Equal(ordinary.Receipts.Select(x => x.InputsHash), shadowed.Receipts.Select(x => x.InputsHash));
    }

    [Fact]
    public void NewShadowTypesRemainInsideExistingStructuralScan()
    {
        var scanned = typeof(StewardComposer).Assembly.GetTypes()
            .Where(x => x.Namespace == "Mcg.AgentOrchestrator.Infrastructure" &&
                        x.Name.Contains("Steward", StringComparison.Ordinal)).ToHashSet();
        Assert.Contains(typeof(StewardShadowAdjudicator), scanned);
        Assert.Contains(typeof(SqliteStewardShadowRecommendationStore), scanned);
        Assert.Contains(typeof(IStewardOperatorIntentReader), scanned);
        Assert.Contains(typeof(StewardDispatcher), scanned);
        Assert.DoesNotContain(typeof(IStewardOperatorIntentReader).GetMethods(),
            x => new[] { "Enqueue", "Submit", "Apply", "Claim", "Complete" }
                .Any(word => x.Name.Contains(word, StringComparison.Ordinal)));
    }

    private static StewardEscalationItem Escalation(string kind, string cause) => new(
        "escalation-1", StewardEscalationCategory.Normal, "goal-123", kind, cause, 2, Now,
        "needs review", kind == "obligation-bound-to-pre-rebase-commit"
            ? "passed-commit=abcdef1234567" : "persisted diagnostic", "task-1");

    private static StewardBriefingBundle Bundle(StewardEscalationItem item) => new(
        [item], [], new StewardNumericReceiptSummary(0, 0, [], 0, 0, 0), [],
        new StewardPolicySnapshot("policy", [], Now),
        new StewardInterruptBudgetLedger(1, 0, 1, Now), [],
        [new StewardQuotedWorkerProse("Developer task-1", "WORKER_RESULT:\ncommit: abcdef1234567")]);

    private sealed class ThrowingStewardShadowStore : IStewardShadowRecommendationStore
    {
        public Task AppendRecommendationAsync(StewardShadowRecommendation receipt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("shadow store unavailable");
        public Task AppendAgreementAsync(StewardShadowAgreementRecord record, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("shadow store unavailable");
        public Task<IReadOnlyList<StewardShadowRecommendation>> ListRecommendationsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("shadow store unavailable");
        public Task<IReadOnlyList<StewardShadowAgreementRecord>> ListAgreementsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("shadow store unavailable");
    }
}
