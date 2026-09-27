using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardHarvestEscalation : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsStewardHarvestEscalation(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public async Task Same_tick_escalation_save_does_not_discard_planner_route()
    {
        using var harness = new StewardHarness("B");
        StewardHarvestFixture.SeedPlannerFailure(harness);
        harness.Model.Reply(StewardHarvestFixture.Route(harness));
        long persistedVersion = 7;
        var host = StewardHarvestFixture.CreateHost(harness, _ => persistedVersion);
        var coordinator = new OperatorIntentCoordinator(harness.Intents,
            decisions: harness.Decisions, goalStateVersionResolver: _ => persistedVersion);
        var versionAfterEscalation = 0L;
        var persistedAfterClaim = false;
        var escalations = new List<GoalLifecycleState>();
        var stopPath = Path.Combine(harness.Root, "stop.signal");

        var summary = new ConductorBatchLoop(operatorIntents: coordinator)
            .WithSteward(host).Run(
                harness.Kernel,
                MakeDriver(writeEscalation: (_, state, _) => escalations.Add(state)),
                ConductorAutonomyPolicy.Conservative,
                stopPath, maxIterations: 2, watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ =>
                {
                    if (harness.Task.Status == WorkTaskStatus.Assigned) return true;
                    harness.Model.Started.Task.GetAwaiter().GetResult();
                    host.CurrentRound!.GetAwaiter().GetResult();
                    return false;
                },
                keepAliveWhenIdle: true,
                persistGoalTick: (_, changed) =>
                {
                    if (!changed.Contains(harness.Goal.Id)) return;
                    persistedAfterClaim |= host.CurrentRound is not null;
                    persistedVersion++;
                },
                onTick: tick =>
                {
                    if (tick.Tick == 1)
                    {
                        versionAfterEscalation = persistedVersion;
                        harness.Model.Started.Task.GetAwaiter().GetResult();
                        host.CurrentRound!.GetAwaiter().GetResult();
                    }
                    if (harness.Task.Status == WorkTaskStatus.Assigned)
                        File.WriteAllText(stopPath, "stop");
                });

        Xunit.Assert.InRange(summary.Ticks, 1, 2);
        Xunit.Assert.True(persistedAfterClaim);
        Xunit.Assert.True(versionAfterEscalation > 7);
        Xunit.Assert.Contains(GoalLifecycleState.Failed, escalations);
        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Xunit.Assert.Equal(versionAfterEscalation, payload?.ExpectedGoalStateVersion);
        var decisions = File.ReadAllText(harness.ConductPath);
        Xunit.Assert.Contains("reason=route-submitted", decisions);
        Xunit.Assert.DoesNotContain("reason=trigger-superseded", decisions);
    }
}
