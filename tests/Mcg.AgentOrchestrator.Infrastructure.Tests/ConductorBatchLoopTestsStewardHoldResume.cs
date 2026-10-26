using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStewardHoldResume : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsStewardHoldResume(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public Task Recover_retry_of_trigger_task_is_walked_on_the_next_tick() => RunScenario(retry: true);

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public Task Unresolved_steward_hold_stays_excluded_and_visible() => RunScenario(retry: false);

    private async Task RunScenario(bool retry)
    {
        using var harness = new StewardHarness("A");
        try
        {
            harness.Seed("A");
            harness.Model.Reply("""{"kind":"ask-owner","question":"Which repair?","evidenceReferences":["worker-output=receipt-1"]}""");
            harness.Host.ServiceTick(harness.Kernel);
            await harness.Model.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await harness.Host.CurrentRound!.WaitAsync(TimeSpan.FromSeconds(30));
            harness.Host.ServiceTick(harness.Kernel);
            var goal = harness.Goal;
            var original = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
            Xunit.Assert.Equal("steward-owner-question", original.State);
            Xunit.Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
            var other = harness.Kernel.CreateGoal("Keep actual walk ticks observable",
                [new TaskSpec(TaskId.New(), "Plan work", AgentRole.Planner)]);
            harness.Kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);

            var now = DateTimeOffset.UtcNow;
            var currentTick = 1;
            var ticks = new List<int>();
            var dispatches = new List<(int Tick, string? HoldState)>();
            var persisted = new List<(int Tick, GoalHoldState? Hold)>();
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    if (candidate.Id == goal.Id) dispatches.Add((currentTick, candidate.CurrentHold?.State));
                    return DispatchStartOutcome.Deferred("Fixture records dispatch without starting a worker");
                }, utcNow: () => now);

            var summary = new ConductorBatchLoop(utcNow: () => now).WithSteward(harness.Host)
                .Run(harness.Kernel, driver, ConductorAutonomyPolicy.Conservative,
                    Path.Combine(harness.Root, "stop.signal"), maxIterations: 6,
                    watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false, keepAliveWhenIdle: true,
                    persistGoalTick: (_, changed) =>
                    {
                        if (changed.Contains(goal.Id)) persisted.Add((currentTick, goal.CurrentHold));
                    },
                    onTick: tick =>
                    {
                        ticks.Add(tick.Tick);
                        if (tick.Tick <= 2 || !retry)
                        {
                            var hold = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
                            Xunit.Assert.Equal("steward-owner-question", hold.State);
                            Xunit.Assert.Equal(original.Identity, hold.Identity);
                            Xunit.Assert.Equal(original.StartedAt, hold.StartedAt);
                            Xunit.Assert.Empty(dispatches);
                        }
                        if (tick.Tick == 2 && retry)
                        {
                            // Same kernel operation used by HandleRecover for a Failed task.
                            harness.Kernel.RetryTask(goal.Id, harness.Task.Id, "Recover trigger task",
                                retryCause: RetryCause.EnvironmentApparatusFailure,
                                invalidateDownstream: true, retryRoundKind: null);
                            Xunit.Assert.Equal(WorkTaskStatus.Assigned, harness.Task.Status);
                            Xunit.Assert.True(harness.Task.LatestRetryAt > original.StartedAt);
                        }
                        if (tick.Tick == 3 && retry)
                        {
                            Xunit.Assert.Null(goal.CurrentHold);
                            Xunit.Assert.Contains(persisted, save => save.Tick == 3 && save.Hold is null);
                            Xunit.Assert.Empty(dispatches);
                            Xunit.Assert.Single(ReconciledEvents(harness));
                        }
                        if (!retry)
                        {
                            Xunit.Assert.Empty(ReconciledEvents(harness));
                            using var json = JsonDocument.Parse(AttentionAgentReadView.Render([], [],
                                goals: harness.Kernel.Goals.ToArray()));
                            var row = Xunit.Assert.Single(json.RootElement.GetProperty("payload")
                                .GetProperty("ownerQuestionHolds").EnumerateArray());
                            Xunit.Assert.Equal(goal.Id.Value, row.GetProperty("goalId").GetString());
                            Xunit.Assert.Equal(original.Identity, row.GetProperty("identity").GetString());
                            Xunit.Assert.Equal(new[] { "steward-owner-question" },
                                new GoalOperatorDispositionSurface().Evaluate(goal, 0, false).Blockers);
                        }
                        now += TimeSpan.FromSeconds(1);
                        currentTick = tick.Tick + 1;
                    });

            Xunit.Assert.Equal(6, summary.Ticks);
            Xunit.Assert.Equal(Enumerable.Range(1, 6), ticks);
            if (retry)
            {
                Xunit.Assert.NotEmpty(dispatches);
                Xunit.Assert.InRange(dispatches[0].Tick, 4, 5);
                Xunit.Assert.All(dispatches, dispatch => Xunit.Assert.NotEqual("steward-owner-question", dispatch.HoldState));
                var entry = Xunit.Assert.Single(ReconciledEvents(harness));
                Xunit.Assert.Equal(goal.Id.Value, entry.GetProperty("goalId").GetString());
            }
            else
            {
                Xunit.Assert.Empty(dispatches);
                Xunit.Assert.Empty(ReconciledEvents(harness));
            }
        }
        finally { harness.Host.Stop(); }
    }

    private static JsonElement[] ReconciledEvents(StewardHarness harness) =>
        File.ReadAllLines(harness.ConductPath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(entry => entry.GetProperty("eventKind").GetString() == "owner-question-hold-reconciled" &&
            entry.GetProperty("goalId").GetString() == harness.Goal.Id.Value).ToArray();
}
