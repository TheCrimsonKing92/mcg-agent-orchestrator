using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsGoalLeftMidTick : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsGoalLeftMidTick(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void DispatchEvictionSkipsGoalAndDispatchesNeighborInSameTick()
    {
        var kernel = new AgentOrchestratorKernel();
        var firstGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "first goal");
        var secondGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "second goal");
        GoalId? evictedGoalId = null;
        var dispatchedGoalIds = new List<GoalId>();
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                if (evictedGoalId is null)
                {
                    evictedGoalId = goal.Id;
                    kernel.CancelGoal(goal.Id, "goal-left-mid-tick-test", allowLiveDispatches: true);
                    kernel.EvictTerminalGoalAggregates([goal.Id]);
                    return ConductorFindingEvidenceLoopFixture.HeldDispatch();
                }

                dispatchedGoalIds.Add(goal.Id);
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Permissive, NoStopPath(),
            maxIterations: 1, onTick: ticks.Add);

        Assert.Equal(1, summary.Ticks);
        Assert.NotNull(evictedGoalId);
        var evicted = evictedGoalId;
        var otherGoal = firstGoal.Id == evicted ? secondGoal : firstGoal;
        Assert.Equal(otherGoal.Id, Assert.Single(dispatchedGoalIds));
        Assert.DoesNotContain(kernel.Goals, goal => goal.Id == evicted);
        var tick = Assert.Single(ticks);
        var skipLine = Assert.Single(tick.ProgressLines!, line =>
            line.StartsWith("GOAL_LEFT_WORKING_SET ", StringComparison.Ordinal));
        Assert.Contains($"goal={evicted.Value[..8]}", skipLine, StringComparison.Ordinal);
        Assert.Equal(1, tick.Advanced);
        Assert.Equal(0, tick.Held);
        Assert.Equal(0, tick.Escalated);
        Assert.Equal(0, tick.Done);
    }

    [Xunit.Fact]
    public void AcceptanceFaultAfterEvictionSkipsWithoutEscalationOrReaping()
    {
        var (kernel, goal) = SimpleGoal("acceptance fault after eviction");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var reapedGoalIds = new List<GoalId>();
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            runAcceptance: accepting =>
            {
                kernel.CancelGoal(accepting.Id, "goal-left-mid-tick-test", allowLiveDispatches: true);
                kernel.EvictTerminalGoalAggregates([accepting.Id]);
                throw new InvalidOperationException("acceptance fault after eviction");
            },
            isVerificationGateSatisfied: _ => true);

        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, reaped) => reapedGoalIds.Add(reaped.Id)).Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                maxIterations: 1, onTick: ticks.Add);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(0, summary.Escalated);
        Assert.Empty(reapedGoalIds);
        Assert.DoesNotContain(kernel.Goals, candidate => candidate.Id == goal.Id);
        var tick = Assert.Single(ticks);
        var skipLine = Assert.Single(tick.ProgressLines!, line =>
            line.StartsWith("GOAL_LEFT_WORKING_SET ", StringComparison.Ordinal));
        Assert.Contains($"goal={goal.Id.Value[..8]}", skipLine, StringComparison.Ordinal);
        Assert.Contains("stage=advance-fault", skipLine, StringComparison.Ordinal);
        Assert.Equal(0, tick.Advanced);
        Assert.Equal(0, tick.Held);
        Assert.Equal(0, tick.Escalated);
        Assert.Equal(0, tick.Done);
    }
}
