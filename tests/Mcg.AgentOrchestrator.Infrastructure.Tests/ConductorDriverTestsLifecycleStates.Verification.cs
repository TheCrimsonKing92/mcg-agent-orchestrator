using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

public sealed partial class ConductorDriverTestsLifecycleStates
{
    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingVerification_names_missing_task_evidence")]
    public void ConductorDriverAwaitingVerificationNamesMissingTaskEvidence()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");

        var result = MakeDriver(getFacts: _ => GoalLifecycleFacts.None)
            .AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, held.State);
        Assert.Contains(task.Id.Value[..8], held.Reason, StringComparison.Ordinal);
        Assert.Contains(nameof(VerificationGateStatus.MissingVerification), held.Reason, StringComparison.Ordinal);
        Assert.Contains("no verification evidence", held.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-reconcile", held.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
