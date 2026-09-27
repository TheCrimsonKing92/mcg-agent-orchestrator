using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsOperatorIntentsAdjudicatePrecondition : ConductorBatchLoopTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ConductorBatchLoopTestsOperatorIntentsAdjudicatePrecondition(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public async Task Between_ticks_version_bump_does_not_reject_reopen_regate()
    {
        using var harness = new StewardHarness("C");
        harness.Seed("C");
        long version = 7;
        var coordinator = new OperatorIntentCoordinator(harness.Intents, decisions: harness.Decisions,
            goalStateVersionResolver: _ => version);
        var escalations = 0;
        OperatorIntentRecord? intent = null;
        var stopPath = Path.Combine(harness.Root, "stop.signal");

        var summary = new ConductorBatchLoop(operatorIntents: coordinator).Run(
            harness.Kernel, MakeDriver(writeEscalation: (_, _, _) => escalations++),
            ConductorAutonomyPolicy.Conservative, stopPath, maxIterations: 2,
            watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false,
            keepAliveWhenIdle: true, persistGoalTick: (_, _) => { },
            onTick: tick =>
            {
                if (tick.Tick == 1)
                {
                    intent = Enqueue(harness, new AdjudicateOperatorIntentPayload(
                        "reopen-regate", "Operator regate", ["receipt-1"], version, harness.Root,
                        Precondition: AdjudicationPrecondition.Capture(harness.Goal, harness.Task)));
                    version++; // Models the applying tick's unrelated goals.version save.
                }
                else File.WriteAllText(stopPath, "stop");
            });

        Assert.Equal(2, summary.Ticks);
        Assert.Equal(1, escalations);
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intent!.Id))!.Status);
        Assert.Equal(EffectReceiptStatus.Applied,
            (await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"))!.Effect!.Status);
        Assert.Equal(GoalStatus.Verified, harness.Goal.Status);
        Assert.Equal(WorkTaskStatus.Completed, harness.Task.Status);
        Assert.Equal(0, harness.Task.LastVerification?.ExitCode);
    }

    [Xunit.Fact]
    public async Task Changed_task_status_rejects_without_changing_task()
    {
        using var harness = new StewardHarness("B");
        harness.Seed("B");
        var coordinator = new OperatorIntentCoordinator(harness.Intents, decisions: harness.Decisions,
            goalStateVersionResolver: _ => 7);
        var intent = Enqueue(harness, new AdjudicateOperatorIntentPayload(
            "close", "Operator closes task", ["receipt-1"], 7, harness.Root,
            Precondition: AdjudicationPrecondition.Capture(harness.Goal, harness.Task)));
        harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Completed, "changed");
        var before = JsonSerializer.Serialize(
            harness.Kernel.ExportGoalSnapshot(harness.Goal.Id) with { Timeline = [] }, JsonOptions);

        _ = new ConductorBatchLoop(operatorIntents: coordinator).Run(
            harness.Kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative,
            Path.Combine(harness.Root, "stop.signal"), maxIterations: 1,
            persistGoalTick: (_, _) => { });

        Assert.Equal(OperatorIntentStatus.Rejected, (await harness.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal("stale-goal-state-version",
            (await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"))!.Effect!.Result);
        Assert.Equal(before, JsonSerializer.Serialize(
            harness.Kernel.ExportGoalSnapshot(harness.Goal.Id) with { Timeline = [] }, JsonOptions));
    }

    [Xunit.Fact]
    public async Task Rejected_adjudication_keeps_hold_and_escalation_across_next_tick()
    {
        using var harness = new StewardHarness("C");
        harness.Seed("C");
        var coordinator = new OperatorIntentCoordinator(harness.Intents, decisions: harness.Decisions,
            goalStateVersionResolver: _ => 7,
            evidenceResolver: new AdjudicationEvidenceResolver(harness.Root));
        var escalations = 0;
        OperatorIntentRecord? intent = null;
        GoalHoldState? hold = null;
        var stopPath = Path.Combine(harness.Root, "stop.signal");

        var summary = new ConductorBatchLoop(operatorIntents: coordinator).Run(
            harness.Kernel, MakeDriver(writeEscalation: (_, _, _) => escalations++),
            ConductorAutonomyPolicy.Conservative, stopPath, maxIterations: 3,
            watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false,
            keepAliveWhenIdle: true, persistGoalTick: (_, _) => { },
            onTick: tick =>
            {
                if (tick.Tick == 1)
                {
                    harness.Kernel.ObserveGoalHold(harness.Goal.Id, "steward-owner-question", "needs owner",
                        DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
                    hold = harness.Goal.CurrentHold;
                    intent = Enqueue(harness, new AdjudicateOperatorIntentPayload(
                        "reopen-regate", "Operator regate", ["trx:missing.trx"], 7, harness.Root,
                        Precondition: AdjudicationPrecondition.Capture(harness.Goal, harness.Task)));
                }
            });

        Assert.Equal(3, summary.Ticks);
        Assert.Equal(1, escalations);
        Assert.Equal(hold, harness.Goal.CurrentHold);
        Assert.Equal(OperatorIntentStatus.Rejected, (await harness.Intents.GetAsync(intent!.Id))!.Status);
        Assert.Equal("evidence-reference-unresolved",
            (await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"))!.Effect!.Result);
    }

    private static OperatorIntentRecord Enqueue(StewardHarness harness, AdjudicateOperatorIntentPayload payload)
    {
        var id = Guid.NewGuid().ToString("N");
        return harness.Intents.EnqueueAsync(new OperatorIntentRecord(
            id, id, OperatorIntentVerbs.Adjudicate, harness.Goal.Id.Value, harness.Task.Id.Value,
            JsonSerializer.Serialize(payload, JsonOptions), [], "operator", "cli", "local-process",
            DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
    }
}
