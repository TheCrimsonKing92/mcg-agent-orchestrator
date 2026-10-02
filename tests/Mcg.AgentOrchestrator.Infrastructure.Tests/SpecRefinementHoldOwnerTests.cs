using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the producer/dispatch seams use test-owned state and fixed observation times.
public sealed class SpecRefinementHoldOwnerTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Theory]
    [Xunit.InlineData(OrchestratorStateOutboxStatus.Pending, true)]
    [Xunit.InlineData(OrchestratorStateOutboxStatus.Processing, true)]
    [Xunit.InlineData(OrchestratorStateOutboxStatus.Failed, false)]
    [Xunit.InlineData(OrchestratorStateOutboxStatus.Quarantined, false)]
    public void PendingRefinement_OutboxStatus_ControlsOwnershipAndStall(
        OrchestratorStateOutboxStatus status, bool hasOwner)
    {
        const string reason = "SPEC_REFINEMENT_PENDING goal=deadbeef owner=durable-outbox detail=waiting";
        var exception = GoalDispatchOperations.SpecRefinementPendingException(reason, status);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(reason, exception.Message);
        var dispatch = DispatchStartOutcome.FromDispatchException(exception, "dispatch failed");
        Assert.Equal(DispatchStartOutcomeCategory.Deferred, dispatch.Category);
        var (kernel, goal) = SimpleGoal();
        var dispatchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { dispatchCalls++; return dispatch; });
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(1, dispatchCalls);
        Assert.Equal(reason, held.Reason);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(hasOwner ? ConductorHoldOwner.DurableOutbox : ConductorHoldOwner.None, held.Owner);
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var changed = new HashSet<GoalId>();
        var lines = new List<string>();
        foreach (var minutes in new[] { 0, 11 })
            ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
                start.AddMinutes(minutes), TimeSpan.FromMinutes(10), changed, lines);

        if (hasOwner)
        {
            Assert.Null(goal.CurrentHold);
            Assert.DoesNotContain(lines, line => line.Contains("GOAL_STALLED", StringComparison.Ordinal));
        }
        else
        {
            var stalled = Assert.Single(lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
            Assert.Contains("owner=none", stalled, StringComparison.Ordinal);
            Assert.Equal(start.AddMinutes(11), goal.CurrentHold?.StalledAt);
        }
    }

    [Xunit.Fact]
    public void DurableOutboxHold_AfterOwnerlessObservation_ClearsAndRestartsWindow()
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, "pending refinement");
        var changed = new HashSet<GoalId>();
        var lines = new List<string>();
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
            start, TimeSpan.FromMinutes(10), changed, lines);
        Assert.NotNull(goal.CurrentHold);
        changed.Clear();
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held with { Owner = ConductorHoldOwner.DurableOutbox },
            start.AddMinutes(11), TimeSpan.FromMinutes(10), changed, lines);
        Assert.Null(goal.CurrentHold);
        Assert.Contains(goal.Id, changed);
        Assert.Empty(lines);
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
            start.AddMinutes(22), TimeSpan.FromMinutes(10), changed, lines);
        Assert.Equal(start.AddMinutes(22), goal.CurrentHold?.StartedAt);
        Assert.Null(goal.CurrentHold?.StalledAt);
        Assert.Empty(lines);
    }

    [Xunit.Fact]
    public void DeferredWithoutTypedOutboxOwner_RemainsOwnerless()
    {
        var exception = new InvalidOperationException("SPEC_REFINEMENT_PENDING owner=durable-outbox");
        var mapped = DispatchStartOutcome.FromDispatchException(exception, "dispatch failed");
        Assert.Equal(ConductorHoldOwner.None, mapped.HoldOwner);
        Assert.Equal(ConductorHoldOwner.None, DispatchStartOutcome.Deferred("unrelated lease hold").HoldOwner);
    }
}
