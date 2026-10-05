using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsCancelledReviewerBlocker
{
    [Xunit.Fact]
    public void EmptyBatch_CancelledReviewer_ReportsTaskAndRerunCommands()
    {
        var goal = GoalWithCancelledReviewer();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var effects = new List<string>();
        string? escalationReason = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { effects.Add("start"); return DispatchStartOutcome.EmptyBatch("No tasks in ready batch"); },
            evaluateReadiness: _ => { effects.Add("readiness"); return new DispatchReadinessBlocked("No assigned dispatch candidates"); },
            writeEscalation: (_, _, reason) => { effects.Add("escalate"); escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(new[] { "start", "readiness", "escalate" }, effects);
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, escalated.State);
        Assert.Contains(reviewer.Id.Value, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains($"adjudicate --goal {goal.Id.Value} 3 route --cause <cause>", escalated.Reason, StringComparison.Ordinal);
        Assert.Contains($"retry --goal {goal.Id.Value} 3 --cause <cause>", escalated.Reason, StringComparison.Ordinal);
        Assert.False(escalated.Reason.StartsWith(ConductorDriver.NoReadyBatchHoldPrefix, StringComparison.Ordinal));
        Assert.Equal(escalated.Reason, escalationReason);
        var decision = Assert.IsType<PolicyDecisionRecord>(escalated.Decision);
        Assert.Equal("dispatch-start", decision.Stage);
        Assert.Equal(7, decision.Rung);
        Assert.Equal(13, decision.Facts.Count);
        var blocker = Assert.Single(decision.Facts, fact => fact.Name == "cancelledPredecessorBlocker").Value;
        Assert.Contains(reviewer.Id.Value, blocker, StringComparison.Ordinal);
        Assert.Contains("route --cause", blocker, StringComparison.Ordinal);
        Assert.Contains("retry", blocker, StringComparison.Ordinal);
        var replay = DispatchStartPolicy.Evaluate(DispatchStartFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(DispatchStartAction.Escalate, replay.Action);
        Assert.Equal(escalated.Reason, replay.Reason);
    }

    [Xunit.Fact]
    public void EmptyBatch_OtherTaskAssigned_DoesNotReportReviewerAsOnlyBlocker()
    {
        var goal = GoalWithCancelledReviewer(WorkTaskStatus.Assigned);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch("No tasks in ready batch"),
            evaluateReadiness: _ => new DispatchReadinessBlocked("Tester route blocked", HasCandidates: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("", Assert.Single(decision.Facts, fact => fact.Name == "cancelledPredecessorBlocker").Value);
    }

    private static Goal GoalWithCancelledReviewer(WorkTaskStatus testerStatus = WorkTaskStatus.Completed)
    {
        var at = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var verification = new TaskVerificationSnapshot("manual", @"C:\repo", 0, "", "", at);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot("goal-cancelled-reviewer", "Require review before acceptance", GoalStatus.Active,
                [new TaskSnapshot(TaskId.New().Value, "Implementation finished.", AgentRole.Developer, WorkTaskStatus.Completed,
                    null, null, verification, [], null, null),
                 new TaskSnapshot(TaskId.New().Value, "Tests finished.", AgentRole.Tester, testerStatus,
                    null, null, verification, [], null, null),
                 new TaskSnapshot(TaskId.New().Value, "Review interrupted.", AgentRole.Reviewer, WorkTaskStatus.Cancelled,
                    null, null, null, [], null, null)], [])], []));
        return kernel.Goals.Single();
    }
}
