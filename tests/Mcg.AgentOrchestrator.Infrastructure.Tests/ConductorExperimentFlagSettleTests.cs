using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its flag files, database and fake intent store.
public sealed class ConductorExperimentFlagSettleTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void StopRuleInconclusive_SettlesTowardPriorOnce(bool applied, bool expected)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(applied));
        var record = Record();
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        var ledgers = Ledgers(record, applied);
        var now = record.CreatedAt.AddMinutes(2);

        Assert.Equal(expected, controller.TryToggle(record, ledgers, now, Reading("inconclusive")));
        Assert.Equal(expected ? 1 : 0, store.Records.Count);
        if (expected)
        {
            var intent = Assert.Single(store.Records);
            Assert.Equal(OperatorIntentVerbs.ExperimentToggleFlag, intent.Verb);
            Assert.Equal($"experiment-toggle-flag:{record.Id}:gate-a", intent.IdempotencyKey);
        }
        var restarted = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        Assert.False(restarted.TryToggle(record, ledgers, now, Reading("inconclusive")));
        Assert.Equal(expected ? 1 : 0, store.Records.Count);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void StopRuleKeep_SettlesTowardDecidedArm(bool applied, bool breached, bool expected)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(applied));
        var record = Record();
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);

        Assert.Equal(expected, controller.TryToggle(record, Ledgers(record, applied),
            record.CreatedAt.AddMinutes(2), Reading("keep", breached: breached)));
        Assert.Equal(expected ? 1 : 0, store.Records.Count);
        if (expected) Assert.Equal(OperatorIntentVerbs.ExperimentToggleFlag, Assert.Single(store.Records).Verb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("inconclusive")]
    [InlineData("keep")]
    public void NullOrUnmetReading_KeepsAlternating(string? verdict)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var record = Record();
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        var now = record.CreatedAt.AddMinutes(2);

        Assert.True(verdict is null
            ? controller.TryToggle(record, Ledgers(record, true), now)
            : controller.TryToggle(record, Ledgers(record, true), now, Reading(verdict, stopRuleMet: false)));
        Assert.Equal($"experiment-toggle-flag:{record.Id}:gate-a", Assert.Single(store.Records).IdempotencyKey);
    }

    [Theory]
    [InlineData("in-flight")]
    [InlineData("probe-error")]
    [InlineData("stale-stamp")]
    public void StopRuleSettle_UnsafeGateState_QueuesNothing(string reason)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var record = Record();
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags,
            () => reason == "probe-error" ? throw new IOException("probe unavailable") : reason == "in-flight");

        Assert.False(controller.TryToggle(record, Ledgers(record, reason != "stale-stamp"),
            record.CreatedAt.AddMinutes(2), Reading("inconclusive")));
        Assert.Empty(store.Records);
    }

    [Theory]
    [InlineData(ExperimentBaselineKind.AlternatingGates, true)]
    [InlineData(ExperimentBaselineKind.BeforeAfterWindow, false)]
    public void Revert_FlagInPriorArm_RefutesOnlyAlternating(ExperimentBaselineKind baseline, bool expected)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var spec = ExperimentFlagTestFixture.Spec();
        if (baseline == ExperimentBaselineKind.AlternatingGates)
            spec = spec with { Baseline = new(baseline) };
        var record = fixture.Add(spec);
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.False(record.Spec.Intervention.FlagTarget!.PriorValue);
        Assert.True(fixture.Flags.Read(record.Spec.Intervention.FlagTarget));
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: false));
        var before = File.ReadAllBytes(fixture.PolicyPath);
        var controller = new ConductorExperimentFlagRevertController(fixture.Experiments,
            () => fixture.Intents, fixture.Flags);

        Assert.Equal(expected, controller.TryRevert(record, Reading("revert"), record.CreatedAt.AddMinutes(2)));
        var reverts = fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentRevertFlag).ToArray();
        var persisted = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(expected ? ExperimentOutcomeState.Refuted : ExperimentOutcomeState.Open, persisted.Outcome);
        if (expected)
        {
            var revert = Assert.Single(reverts);
            Assert.Equal($"experiment-revert-flag:{record.Id}", revert.IdempotencyKey);
            Assert.NotNull(persisted.Decision);
            fixture.Tick();
            Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(revert).Status);
        }
        else
        {
            Assert.Empty(reverts);
            Assert.Null(persisted.Decision);
        }
        Assert.Equal(before, File.ReadAllBytes(fixture.PolicyPath));
    }

    private static ExperimentReadingResult Reading(string verdict, bool stopRuleMet = true, bool breached = false) =>
        new(6, new(6, ExperimentStopUnit.Gates), stopRuleMet, [], verdict, "test reading", breached);

    private static ExperimentRecord Record() => new("experiment-a", ExperimentFlagTestFixture.Spec() with
    {
        Baseline = new(ExperimentBaselineKind.AlternatingGates),
        Intervention = new(ExperimentInterventionKind.ConfigFlag, "toggle", new(
            ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true, false))
    }, ExperimentFlagTestFixture.Now, ExperimentOutcomeState.Open, null);

    private static ExperimentHostLedgers Ledgers(ExperimentRecord record, bool applied) => new(
        [new(record.CreatedAt.AddMinutes(1), "gate-a", null, "completed", 1000, 1000, 0, 0,
            ExperimentArms: new Dictionary<string, string> { [record.Id] = applied ? "on" : "off" })], []);

    private sealed class IntentStore : IOperatorIntentStore
    {
        internal List<OperatorIntentRecord> Records { get; } = [];
        public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default)
        {
            var existing = Records.SingleOrDefault(row => row.IdempotencyKey == intent.IdempotencyKey);
            if (existing is not null) return Task.FromResult(existing);
            Records.Add(intent);
            return Task.FromResult(intent);
        }
        public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextPendingAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome, DateTimeOffset completedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AcknowledgeWake(string intentId) => throw new NotSupportedException();
    }
}
