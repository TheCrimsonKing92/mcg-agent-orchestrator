using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel safe: every case owns its kernel and snapshot; no external resources.
public sealed class GoalExplicitDispositionAcceptanceReadmissionTests
{
    [Theory]
    [InlineData(GoalStatus.Verifying, ConductorEscalationKind.BackgroundAcceptanceFailed)]
    [InlineData(GoalStatus.Verified, ConductorEscalationKind.AcceptanceVerificationFailed)]
    public void Unpark_InterruptedAcceptance_ReturnsActiveForOrdinaryReadmission(
        GoalStatus status, ConductorEscalationKind kind)
    {
        var (kernel, goal, _) = GoalExplicitDispositionTests.VerifyingGoal();
        goal.SetStatus(status);
        kernel.RecordGoalPolicyDecision(goal.Id, "Interrupted acceptance episode",
            new ConductorTickOutcomePayload("Escalated", "Verified", kind.ToString()));
        var tasks = JsonSerializer.Serialize(goal.ToSnapshot().Tasks);
        var escalation = goal.Timeline.Last();
        kernel.ParkGoal(goal.Id, "Acknowledge episode");
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(status, goal.LifecycleStatus);
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());

        goal = kernel.UnparkGoal(goal.Id, "Resume ordinary admission");

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(new GoalExplicitDisposition(2, GoalExplicitDispositionAction.Unpark), goal.ExplicitDisposition);
        Assert.Null(goal.ParkedLifecycleStatus);
        Assert.Equal(tasks, JsonSerializer.Serialize(goal.ToSnapshot().Tasks));
        Assert.Contains(escalation, goal.Timeline);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Null(goal.LatestAcceptanceFailure);
        Assert.True(kernel.ReconcileGoalVerificationStatus(goal.Id, "Normal readiness recheck"));
        Assert.Equal(GoalStatus.Verified, goal.Status);
    }

    [Theory]
    [InlineData(GoalStatus.Verifying)]
    [InlineData(GoalStatus.AcceptanceFailed)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    public void Unpark_NewerAcceptanceEvidence_PreservesOutcome(GoalStatus outcome)
    {
        var (kernel, goal, _) = GoalExplicitDispositionTests.VerifyingGoal();
        kernel.RecordGoalPolicyDecision(goal.Id, "Earlier interrupted episode",
            new ConductorTickOutcomePayload("Escalated", "Verified", "BackgroundAcceptanceFailed"));
        kernel.ParkGoal(goal.Id, "Hold");
        if (outcome == GoalStatus.AcceptanceFailed)
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["retained RED"], "Later gate failed"));
        else if (outcome == GoalStatus.Completed)
            kernel.CompleteGoal(goal.Id, "Already owned completion");
        else
            goal.SetStatus(outcome);
        if (outcome == GoalStatus.Verifying)
            kernel.RecordGoalPolicyDecision(goal.Id, "A later episode started",
                new ConductorTickOutcomePayload("Executed", "Verified", null));
        var tasks = JsonSerializer.Serialize(goal.ToSnapshot().Tasks);
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());

        goal = kernel.UnparkGoal(goal.Id, "Release retained outcome");

        Assert.Equal(outcome, goal.Status);
        Assert.Null(goal.ParkedLifecycleStatus);
        Assert.Equal(tasks, JsonSerializer.Serialize(goal.ToSnapshot().Tasks));
        if (outcome == GoalStatus.AcceptanceFailed)
            Assert.Equal(new[] { "retained RED" }, goal.LatestAcceptanceFailure!.FailedChecks);
    }
}
