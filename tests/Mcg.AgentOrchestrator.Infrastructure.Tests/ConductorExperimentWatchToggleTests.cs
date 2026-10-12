using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its workspace and AsyncLocal live-occupancy override.
public sealed class ConductorExperimentWatchToggleTests
{
    [Fact]
    public void ObserveTick_QueuesOneToggleAcrossRestartsWithoutDeciding()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        AppendGate(fixture, record, "gate-a", record.CreatedAt.AddMinutes(1), "on");
        var now = record.CreatedAt.AddMinutes(2);
        var ledgers = ExperimentHostLedgers.Read(fixture.Workspace.ExecutionDirectory);
        Assert.Single(ledgers.GateLaneTimes);
        var reading = ExperimentReading.Evaluate(record, [], new Dictionary<string, DateTimeOffset>(), [], now, 1, ledgers);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.False(reading.StopRuleMet);
        Assert.False(reading.GuardrailBreached);
        var probes = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => { probes++; return []; });
        var watch = new ConductorExperimentWatch(fixture.Workspace, null);
        watch.ObserveTick(now);
        var intent = Assert.Single(Toggles(fixture));
        Assert.Equal($"experiment-toggle-flag:{record.Id}:gate-a", intent.IdempotencyKey);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal("conductor", intent.Actor);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal("conductor-experiment-toggle", intent.Channel);
        Assert.Equal(OperatorIntentAdjudication.StewardAssurance, intent.AuthenticationAssurance);
        watch.ObserveTick(now);
        new ConductorExperimentWatch(fixture.Workspace, null).ObserveTick(now);
        Assert.Equal(intent.Id, Assert.Single(Toggles(fixture)).Id);
        Assert.Equal(3, probes);
        var persisted = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, persisted.Outcome);
        Assert.Null(persisted.Decision);
        Assert.Equal(record.Spec.Intervention.FlagTarget, persisted.Spec.Intervention.FlagTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserveTick_AcceptanceOccupantOrProbeErrorSuppressesToggle(bool throws)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        AppendGate(fixture, record, "gate-a", record.CreatedAt.AddMinutes(1), "on");
        var before = fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult().Count;
        var probes = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
        {
            probes++;
            if (throws) throw new GateLoadContextProbe.LoadProbeUnavailableException("test probe failure");
            return [new(null, "test-gate", 0, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.Acceptance)];
        });
        new ConductorExperimentWatch(fixture.Workspace, null).ObserveTick(record.CreatedAt.AddMinutes(2));
        Assert.Equal(1, probes);
        Assert.Empty(Toggles(fixture));
        Assert.Equal(before, fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult().Count);
        var persisted = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, persisted.Outcome);
        Assert.Null(persisted.Decision);
    }

    [Fact]
    public void ObserveTick_FocusedEvidenceOccupantDoesNotBlockToggle()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        AppendGate(fixture, record, "gate-a", record.CreatedAt.AddMinutes(1), "on");
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new(null, "test-evidence", 0, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.FocusedEvidence)]);
        new ConductorExperimentWatch(fixture.Workspace, null).ObserveTick(record.CreatedAt.AddMinutes(2));
        Assert.Single(Toggles(fixture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserveTick_RevertAndKeepRetainPrecedence(bool keep)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture, stopCount: 6);
        for (var index = 0; index < 6; index++)
            AppendGate(fixture, record, $"gate-{index}", record.CreatedAt.AddMinutes(index + 1),
                index < 3 ? "off" : "on", index < 3 ? 1000 : keep ? 500 : 1500);
        var now = record.CreatedAt.AddMinutes(7);
        var ledgers = ExperimentHostLedgers.Read(fixture.Workspace.ExecutionDirectory);
        var reading = ExperimentReading.Evaluate(record, [], new Dictionary<string, DateTimeOffset>(), [], now, 6, ledgers);
        Assert.Equal(keep ? "keep" : "revert", reading.Verdict);
        Assert.True(reading.StopRuleMet);
        Assert.False(reading.GuardrailBreached);
        var probes = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => { probes++; return []; });
        new ConductorExperimentWatch(fixture.Workspace, null).ObserveTick(now);
        Assert.Equal(0, probes);
        Assert.Empty(Toggles(fixture));
        var decided = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(keep ? ExperimentOutcomeState.Confirmed : ExperimentOutcomeState.Refuted, decided.Outcome);
        Assert.NotNull(decided.Decision);
        if (keep) Assert.Single(new BacklogStore(fixture.Workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
        else Assert.Single(fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentRevertFlag));
    }

    private static ExperimentRecord Apply(ExperimentFlagTestFixture fixture, int stopCount = 99)
    {
        var spec = ExperimentFlagTestFixture.Spec() with
        {
            Baseline = new(ExperimentBaselineKind.AlternatingGates),
            Metrics = ["gate-seconds"],
            Guardrail = new("gate-seconds", new("gate-seconds", ">", 100)),
            StopRule = new(stopCount, ExperimentStopUnit.Gates),
            DecisionRule = new([new("gate-seconds", "<", 0)], [new("gate-seconds", ">", 0)])
        };
        var record = fixture.Add(spec);
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.False(record.Spec.Intervention.FlagTarget!.PriorValue);
        Assert.True(fixture.Flags.Read(record.Spec.Intervention.FlagTarget));
        return record;
    }

    private static void AppendGate(ExperimentFlagTestFixture fixture, ExperimentRecord record, string attempt,
        DateTimeOffset at, string arm, double totalMs = 1000) => GateLaneTimeLedger.Append(
        GateLaneTimeLedger.ResolveStorePath(fixture.Workspace.ExecutionDirectory), new(at, attempt, null, "completed",
            totalMs, totalMs, 0, 0, ExperimentArms: new Dictionary<string, string> { [record.Id] = arm }));

    private static IReadOnlyList<OperatorIntentRecord> Toggles(ExperimentFlagTestFixture fixture) =>
        fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentToggleFlag).ToArray();
}
