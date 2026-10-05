using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePrecheckCancelledReviewerTests
{
    [Xunit.Fact]
    public void CancelledReviewerBlocksAcceptancePrecheck()
    {
        var goal = GoalWithCancelledTask(AgentRole.Reviewer);

        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void CancelledNonReviewerStillPassesAcceptancePrecheck(AgentRole cancelledRole)
    {
        var goal = GoalWithCancelledTask(cancelledRole);

        Assert.True(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    private static Goal GoalWithCancelledTask(AgentRole cancelledRole)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Require review at admission",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
             new TaskSpec(TaskId.New(), "Test", AgentRole.Tester),
             new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var at = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != cancelledRole))
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Work finished.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", @"C:\repo", 0, "", "", at));
        }
        var cancelled = goal.Tasks.Single(task => task.RequiredRole == cancelledRole);
        kernel.ReportTaskProgress(goal.Id, cancelled.Id, WorkTaskStatus.Cancelled, "Operator cancelled task.");
        Assert.Equal(WorkTaskStatus.Cancelled, cancelled.Status);
        Assert.All(goal.Tasks.Where(task => task.Id != cancelled.Id), task =>
        {
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.True(task.LastVerification!.Succeeded);
        });
        return goal;
    }
}
