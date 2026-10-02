using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each test owns its kernel and uses fixed observation times.
public sealed class OwnedHoldStallTrackingTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private static readonly DateTimeOffset FirstObservation =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMinutes(10);

    [Xunit.Fact]
    public void VerifiedBackgroundAttempt_PastThreshold_DoesNotRecordStall()
    {
        AssertOwnedHoldDoesNotStall(
            ConductorHoldOwner.BackgroundAttempt,
            "acceptance verification still running in background");
    }

    [Xunit.Fact]
    public void VerifiedAcceptanceQueue_PastThreshold_DoesNotRecordStall()
    {
        AssertOwnedHoldDoesNotStall(
            ConductorHoldOwner.AcceptanceQueue,
            "acceptance width 1 reached; live acceptance occupants goal:abcd1234; retry on next conduct tick");
    }

    [Xunit.Fact]
    public void WorkspaceReadyOwnerlessHold_PastThreshold_EmitsOneStall()
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        const string reason = "waiting for an operator decision";
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, reason);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);

        Observe(kernel, driver, goal, held, FirstObservation, changed, lines);
        Assert.Empty(lines);
        Assert.NotNull(goal.CurrentHold);
        Assert.Null(goal.CurrentHold.StalledAt);
        Observe(kernel, driver, goal, held, FirstObservation.AddMinutes(11), changed, lines);

        var stalled = Assert.Single(lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
        Assert.Contains("state=WorkspaceReady owner=none ", stalled, StringComparison.Ordinal);
        Assert.Contains("repeatedForSeconds=660", stalled, StringComparison.Ordinal);
        Assert.Contains(
            $"blocker={ConductorBatchLoop.FormatStalledBlockerDetail(reason)}",
            stalled, StringComparison.Ordinal);
        Assert.Equal(FirstObservation.AddMinutes(11), goal.CurrentHold?.StalledAt);
        Assert.Contains(goal.Id, changed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(ConductorHoldOwner.BackgroundAttempt)]
    [Xunit.InlineData(ConductorHoldOwner.AcceptanceQueue)]
    public void OwnedHold_AfterOwnerlessObservation_ClearsAndRestartsWindow(
        ConductorHoldOwner owner)
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "waiting");
        Observe(kernel, driver, goal, held, FirstObservation, changed, lines);
        Assert.NotNull(goal.CurrentHold);
        changed.Clear();

        Observe(kernel, driver, goal, held with { Owner = owner },
            FirstObservation.AddMinutes(11), changed, lines);
        Assert.Null(goal.CurrentHold);
        Assert.Contains(goal.Id, changed);
        Assert.Empty(lines);

        Observe(kernel, driver, goal, held, FirstObservation.AddMinutes(22), changed, lines);
        Assert.Equal(FirstObservation.AddMinutes(22), goal.CurrentHold?.StartedAt);
        Assert.Null(goal.CurrentHold?.StalledAt);
        Assert.Empty(lines);
    }

    private static void AssertOwnedHoldDoesNotStall(ConductorHoldOwner owner, string reason)
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason)
        {
            Owner = owner
        };

        Observe(kernel, driver, goal, held, FirstObservation, changed, lines);
        Observe(kernel, driver, goal, held, FirstObservation.AddMinutes(11), changed, lines);

        Assert.DoesNotContain(lines, line => line.Contains("GOAL_STALLED", StringComparison.Ordinal));
        Assert.Null(goal.CurrentHold);
    }

    private static void Observe(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAdvanceOutcome.Held held,
        DateTimeOffset observedAt,
        HashSet<GoalId> changed,
        List<string> lines) =>
        ConductorBatchLoop.TrackGoalOutcome(
            kernel, driver, goal, held, observedAt, StallThreshold, changed, lines);
}
