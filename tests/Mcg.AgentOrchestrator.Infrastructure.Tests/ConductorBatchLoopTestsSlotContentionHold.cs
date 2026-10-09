using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Shares the conductor fixture's build-slot collection; all contention is injected.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSlotContentionHold : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsSlotContentionHold(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Advance_BuildContention_HoldsWithoutFaultOrLifecycleChange(bool lockBlocked)
    {
        var (kernel, goal) = SimpleGoal("build contention holds");
        var status = goal.Status;
        var taskStatus = goal.Tasks.Single().Status;
        var attempts = 0;
        var reaps = 0;
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                attempts++;
                throw BuildContention(lockBlocked);
            });
        var loop = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, _) => reaps++,
            detachGoalRunningDispatches: (_, _) => { });

        var summary = loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 3, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false, onTick: tick =>
            {
                ticks.Add(tick);
                Assert.Equal(status, kernel.GetGoal(goal.Id).Status);
                Assert.Equal(taskStatus, kernel.GetGoal(goal.Id).Tasks.Single().Status);
                Assert.Null(kernel.GetGoal(goal.Id).CurrentHold);
            });

        Assert.Equal(3, ticks.Count);
        Assert.All(ticks, tick =>
        {
            Assert.Equal(1, tick.Held);
            Assert.Equal(0, tick.Escalated);
        });
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(0, reaps);
        // An advance-fault set-aside would skip the subsequent passes.
        Assert.Equal(3, attempts);
        Assert.DoesNotContain(Decisions(kernel, goal), IsAdvanceFault);
    }

    [Xunit.Theory(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Advance_ContinuedContention_EmitsOncePerStreakAndRearms(bool lockBlocked)
    {
        var (kernel, goal) = SimpleGoal("persistent build contention");
        var limit = ConductorBatchLoop.SlotContentionAttentionHoldLimit;
        var attempts = 0;
        var ticks = new List<BatchTickSummary>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                attempts++;
                if (attempts == limit + 3)
                    return DispatchStartOutcome.Deferred("slot free");
                throw BuildContention(lockBlocked);
            });

        var summary = new ConductorBatchLoop().Run(kernel, driver,
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 2 * limit + 3,
            watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false, onTick: ticks.Add);

        Assert.Equal(2 * limit + 3, attempts);
        Assert.Equal(2 * limit + 3, ticks.Count);
        Assert.All(ticks, tick => Assert.Equal(0, tick.Escalated));
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        Assert.DoesNotContain(Decisions(kernel, goal), IsAdvanceFault);
        Assert.Empty(AttentionLines(ticks.Take(limit - 1)));
        var firstLine = Assert.Single(AttentionLines(ticks.Take(limit + 2)));
        Assert.Contains($"goal={goal.Id.Value[..8]}", firstLine, StringComparison.Ordinal);
        Assert.Contains(lockBlocked ? "pid 4343 dotnet" : "slot-0:pid-4242",
            firstLine, StringComparison.Ordinal);
        Assert.Contains($"holds={limit}", firstLine, StringComparison.Ordinal);
        // No earlier emission in the second streak; exactly one at the new threshold.
        Assert.Empty(AttentionLines(ticks.Skip(limit + 3).Take(limit - 1)));
        Assert.Single(AttentionLines(ticks.Skip(limit + 3)));
        Assert.Equal(2, AttentionLines(ticks).Length);
    }

    [Xunit.Theory(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Advance_OrdinaryFault_EscalatesReapsAndSetsAside(bool ioFault)
    {
        var (kernel, goal) = SimpleGoal("ordinary advance fault");
        // A dispatchable Active goal is re-admitted by stall reconciliation. Use
        // acceptance-ready state to observe the advance-fault set-aside itself.
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attempts = 0;
        var reaps = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ =>
            {
                attempts++;
                throw ioFault ? new IOException("ordinary IO fault")
                    : new InvalidOperationException("ordinary advance fault");
            },
            isVerificationGateSatisfied: _ => true);
        var loop = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, _) => reaps++,
            detachGoalRunningDispatches: (_, _) => { });

        var summary = loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
            NoStopPath(), maxIterations: 3, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false);

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(2, summary.Rechecks);
        Assert.Equal("max-iter", summary.StopReason);
        Assert.Equal(1, summary.Escalated);
        Assert.Equal(1, reaps);
        Assert.Equal(1, attempts);
        Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
        Assert.Single(Decisions(kernel, goal).Where(IsAdvanceFault));
        Assert.DoesNotContain(Decisions(kernel, goal), message =>
            message.Contains("re-admitted", StringComparison.Ordinal));
    }

    private static Exception BuildContention(bool lockBlocked) => lockBlocked
        ? new BuildLockBlockedException(new BuildLockAttribution(
            "C:\\lock", [new BuildLockHolder(4343, "dotnet", null, false)], "test"))
        : new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
            "test", [new DotnetBuildStableSlotWait(0, 4242)]));

    private static string[] AttentionLines(IEnumerable<BatchTickSummary> ticks) => ticks
        .SelectMany(tick => tick.ProgressLines ?? [])
        .Where(line => line.StartsWith("INFRASTRUCTURE_ATTENTION ", StringComparison.Ordinal))
        .ToArray();

    private static string[] Decisions(AgentOrchestratorKernel kernel, Goal goal) =>
        kernel.GetGoal(goal.Id).Timeline.Where(item => item.Kind == ProgressKind.GoalPolicyDecision)
            .Select(item => item.Message).ToArray();

    private static bool IsAdvanceFault(string message) =>
        message.Contains("fault isolating goal", StringComparison.Ordinal) ||
        message.Contains("advance threw", StringComparison.Ordinal);
}
