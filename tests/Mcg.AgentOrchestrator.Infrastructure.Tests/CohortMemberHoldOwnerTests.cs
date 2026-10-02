using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: every test owns its kernel and supplies the observation clock.
public sealed class CohortMemberHoldOwnerTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void CohortMemberHeld_PastStallThreshold_ClearsHoldWithoutStalling()
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            ConductorBatchLoop.CohortMemberHeld(
                goal, ConductorAutonomyPolicy.Conservative, "cohort=abc running").Outcome);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal("Acceptance cohort gate owns this member: cohort=abc running", held.Reason);
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var threshold = TimeSpan.FromMinutes(10);
        var changed = new HashSet<GoalId>();
        var lines = new List<string>();

        // An owned cohort also clears a previously ownerless observation.
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held with { Owner = ConductorHoldOwner.None },
            start, threshold, changed, lines);
        Assert.NotNull(goal.CurrentHold);
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
            start.AddMinutes(11), threshold, changed, lines);
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
            start.AddMinutes(22), threshold, changed, lines);

        Assert.Null(goal.CurrentHold);
        Assert.DoesNotContain(lines, line => line.Contains("GOAL_STALLED", StringComparison.Ordinal));
    }
}
