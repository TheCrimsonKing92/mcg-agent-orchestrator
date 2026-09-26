using Mcg.AgentOrchestrator.Core;

public sealed class TerminalGoalReopenGuardTests
{
    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Superseded)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Completed)]
    public void NormalizeDoesNotReopenProtectedTerminalGoal(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Protected terminal goal", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = status }]
        });
        var before = kernel.GetGoal(goal.Id).Timeline.Count;

        Xunit.Assert.False(kernel.NormalizeGoalLifecycleState(goal.Id, "should not reopen"));
        Xunit.Assert.Equal(status, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(before, kernel.GetGoal(goal.Id).Timeline.Count);
        Xunit.Assert.False(kernel.ReconcileGoalVerificationStatus(goal.Id, "do not reconcile terminal goal"));
        Xunit.Assert.False(kernel.NormalizePrematureCompletedGoalToVerified(goal.Id, "do not normalize terminal goal"));
        Xunit.Assert.Throws<InvalidOperationException>(() =>
            kernel.RetryTask(goal.Id, task.Id, "retry", RetryCause.NewSourceFinding));
    }

    [Xunit.Fact]
    public void NormalizeStillReopensFailedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Failed terminal goal", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = GoalStatus.Failed }]
        });

        Xunit.Assert.True(kernel.NormalizeGoalLifecycleState(goal.Id, "reopen failed goal"));
        Xunit.Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
    }
}
