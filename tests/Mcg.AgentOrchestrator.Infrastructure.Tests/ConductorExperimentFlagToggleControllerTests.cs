using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its flag files and fake intent store.
public sealed class ConductorExperimentFlagToggleControllerTests
{
    [Fact]
    public void CompletedGate_QueuesBoundIntentOnceAcrossControllerLifetimes()
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var record = Record();
        var store = new IntentStore();
        var ledgers = Ledgers(Gate(record));
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);

        Assert.True(controller.TryToggle(record, ledgers, record.CreatedAt.AddMinutes(2)));
        var intent = Assert.Single(store.Records);
        Assert.Equal("experiment-toggle-flag", intent.Verb);
        Assert.Equal(OperatorIntentScopes.Workspace, intent.GoalId);
        Assert.Null(intent.TaskId);
        Assert.Equal("conductor", intent.Actor);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal("conductor-experiment-toggle", intent.Channel);
        Assert.Equal(OperatorIntentAdjudication.StewardAssurance, intent.AuthenticationAssurance);
        Assert.Equal($"experiment-toggle-flag:{record.Id}:gate-a", intent.IdempotencyKey);
        Assert.Equal(record.Id, JsonSerializer.Deserialize<ExperimentToggleFlagOperatorIntentPayload>(
            intent.PayloadJson, OperatorIntentJson.Options)!.ExperimentId);
        Assert.False(controller.TryToggle(record, ledgers, record.CreatedAt.AddMinutes(3)));
        var restarted = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        Assert.False(restarted.TryToggle(record, ledgers, record.CreatedAt.AddMinutes(4)));
        Assert.Single(store.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InFlightOrProbeError_FailsClosedUntilOccupancyClears(bool throws)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var record = Record();
        var store = new IntentStore();
        var occupied = true;
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags,
            () => occupied ? throws ? throw new IOException("probe unavailable") : true : false);
        var ledgers = Ledgers(Gate(record));
        Assert.False(controller.TryToggle(record, ledgers, record.CreatedAt.AddMinutes(2)));
        Assert.Empty(store.Records);
        occupied = false;
        Assert.True(controller.TryToggle(record, ledgers, record.CreatedAt.AddMinutes(2)));
        Assert.Single(store.Records);
    }

    [Theory]
    [InlineData("before-after")]
    [InlineData("not-config")]
    [InlineData("decided")]
    [InlineData("unapplied")]
    [InlineData("missing-target")]
    [InlineData("no-row")]
    [InlineData("no-stamp")]
    [InlineData("other-id")]
    [InlineData("at-created")]
    [InlineData("before-created")]
    [InlineData("already-flipped")]
    [InlineData("missing-flag")]
    public void NonQualifyingInput_QueuesNothing(string reason)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var record = Record();
        var spec = record.Spec;
        record = reason switch
        {
            "before-after" => record with { Spec = spec with { Baseline = new(ExperimentBaselineKind.BeforeAfterWindow) } },
            "not-config" => record with { Spec = spec with { Intervention = new(ExperimentInterventionKind.Policy, "policy") } },
            "decided" => record with { Outcome = ExperimentOutcomeState.Confirmed },
            "unapplied" => record with { Spec = spec with { Intervention = spec.Intervention with
                { FlagTarget = spec.Intervention.FlagTarget! with { PriorValue = null } } } },
            "missing-target" => record with { Spec = spec with { Intervention = spec.Intervention with { FlagTarget = null } } },
            _ => record
        };
        var gate = Gate(record);
        gate = reason switch
        {
            "no-stamp" => gate with { ExperimentArms = null },
            "other-id" => gate with { ExperimentArms = new Dictionary<string, string> { ["another-experiment"] = "on" } },
            "at-created" => gate with { ObservedAt = record.CreatedAt },
            "before-created" => gate with { ObservedAt = record.CreatedAt.AddTicks(-1) },
            "already-flipped" => gate with { ExperimentArms = new Dictionary<string, string> { [record.Id] = "off" } },
            _ => gate
        };
        if (reason == "missing-flag") File.Delete(fixture.PolicyPath);
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        Assert.False(controller.TryToggle(record, reason == "no-row" ? ExperimentHostLedgers.Empty : Ledgers(gate),
            record.CreatedAt.AddMinutes(2)));
        Assert.Empty(store.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewestStampedRowWins_WithOrdinalAttemptTieBreak(bool tied)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(enabled: true));
        var record = Record();
        var older = Gate(record);
        var newer = older with
        {
            GateAttemptId = "gate-z",
            ObservedAt = tied ? older.ObservedAt : older.ObservedAt.AddSeconds(1),
            ExperimentArms = new Dictionary<string, string> { [record.Id] = "off" }
        };
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        Assert.False(controller.TryToggle(record, Ledgers(older, newer), record.CreatedAt.AddMinutes(2)));
        Assert.False(controller.TryToggle(record, Ledgers(newer, older), record.CreatedAt.AddMinutes(2)));
        Assert.Empty(store.Records);
        newer = newer with { ExperimentArms = new Dictionary<string, string> { [record.Id] = "on" } };
        Assert.True(controller.TryToggle(record, Ledgers(older, newer), record.CreatedAt.AddMinutes(2)));
        Assert.Equal($"experiment-toggle-flag:{record.Id}:gate-z", Assert.Single(store.Records).IdempotencyKey);
    }

    [Fact]
    public void OffArmMatchingCurrentState_QueuesNextFlip()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Record();
        var gate = Gate(record) with { ExperimentArms = new Dictionary<string, string> { [record.Id] = "off" } };
        var store = new IntentStore();
        var controller = new ConductorExperimentFlagToggleController(() => store, fixture.Flags, () => false);
        Assert.True(controller.TryToggle(record, Ledgers(gate), record.CreatedAt.AddMinutes(2)));
        Assert.Single(store.Records);
    }

    private static ExperimentRecord Record() => new("experiment-a", ExperimentFlagTestFixture.Spec() with
    {
        Baseline = new(ExperimentBaselineKind.AlternatingGates),
        Intervention = new(ExperimentInterventionKind.ConfigFlag, "toggle", new(
            ExperimentFlagFileKind.ConductorPolicy, "followerGatesEnabled", true, false))
    }, ExperimentFlagTestFixture.Now, ExperimentOutcomeState.Open, null);

    private static GateLaneTimeLedgerRecord Gate(ExperimentRecord record) => new(record.CreatedAt.AddMinutes(1),
        "gate-a", null, "completed", 1000, 1000, 0, 0,
        ExperimentArms: new Dictionary<string, string> { [record.Id] = "on" });

    private static ExperimentHostLedgers Ledgers(params GateLaneTimeLedgerRecord[] gates) => new(gates, []);

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
