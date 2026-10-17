using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: owns its workspace and AsyncLocal live-occupancy override.
public sealed class ConductorExperimentWatchSettleTests
{
    [Fact]
    public void ObserveTick_InconclusiveAtStopRule_SettlesToPriorOnceThenHolds()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var record = Apply(fixture);
        for (var index = 0; index < 6; index++)
            AppendGate(fixture, record, $"gate-{index}", record.CreatedAt.AddMinutes(index + 1),
                index % 2 == 0 ? "off" : "on");
        var now = record.CreatedAt.AddMinutes(7);
        var ledgers = ExperimentHostLedgers.Read(fixture.Workspace.ExecutionDirectory);
        Assert.Equal(6, ledgers.GateLaneTimes.Count);
        var reading = ExperimentReading.Evaluate(record, [], new Dictionary<string, DateTimeOffset>(), [], now, 6, ledgers);
        Assert.Equal("inconclusive", reading.Verdict);
        Assert.True(reading.StopRuleMet);
        Assert.False(reading.GuardrailBreached);
        var probes = 0;
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => { probes++; return []; });

        new ConductorExperimentWatch(fixture.Workspace, null).ObserveTick(now);
        var toggle = Assert.Single(Toggles(fixture));
        Assert.Equal($"experiment-toggle-flag:{record.Id}:gate-5", toggle.IdempotencyKey);
        Assert.Equal(OperatorIntentStatus.Pending, toggle.Status);
        AssertOpen(fixture, record);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(toggle).Status);
        Assert.False(fixture.Flags.Read(record.Spec.Intervention.FlagTarget!));

        AppendGate(fixture, record, "gate-6", record.CreatedAt.AddMinutes(8), "off");
        new ConductorExperimentWatch(fixture.Workspace, null).ObserveTick(record.CreatedAt.AddMinutes(9));
        Assert.Equal(2, probes);
        Assert.Equal(toggle.Id, Assert.Single(Toggles(fixture)).Id);
        Assert.False(fixture.Flags.Read(record.Spec.Intervention.FlagTarget!));
        AssertOpen(fixture, record);
    }

    private static ExperimentRecord Apply(ExperimentFlagTestFixture fixture)
    {
        var record = fixture.Add(ExperimentFlagTestFixture.Spec() with
        {
            Baseline = new(ExperimentBaselineKind.AlternatingGates),
            Metrics = ["gate-seconds"],
            Guardrail = new("gate-seconds", new("gate-seconds", ">", 100)),
            StopRule = new(6, ExperimentStopUnit.Gates),
            DecisionRule = new([new("gate-seconds", "<", 0)], [new("gate-seconds", ">", 0)])
        });
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        record = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.False(record.Spec.Intervention.FlagTarget!.PriorValue);
        Assert.True(fixture.Flags.Read(record.Spec.Intervention.FlagTarget));
        return record;
    }

    private static void AssertOpen(ExperimentFlagTestFixture fixture, ExperimentRecord record)
    {
        var persisted = fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
        Assert.Equal(ExperimentOutcomeState.Open, persisted.Outcome);
        Assert.Null(persisted.Decision);
    }

    private static void AppendGate(ExperimentFlagTestFixture fixture, ExperimentRecord record, string attempt,
        DateTimeOffset at, string arm) => GateLaneTimeLedger.Append(
        GateLaneTimeLedger.ResolveStorePath(fixture.Workspace.ExecutionDirectory), new(at, attempt, null, "completed",
            1000, 1000, 0, 0, ExperimentArms: new Dictionary<string, string> { [record.Id] = arm }));

    private static IReadOnlyList<OperatorIntentRecord> Toggles(ExperimentFlagTestFixture fixture) =>
        fixture.Intents.ListForGoalAsync(OperatorIntentScopes.Workspace).GetAwaiter().GetResult()
            .Where(intent => intent.Verb == OperatorIntentVerbs.ExperimentToggleFlag).ToArray();
}
