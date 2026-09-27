using Mcg.AgentOrchestrator.Core;

public sealed class GoalLifecycleTestsParallelAcceptanceStaleReopen
{
    [Fact]
    public void RetryingTaskAfterCappedStaleAcceptanceStartsANewConsecutiveSequence()
    {
        var (kernel, goal, task) = CappedStaleAcceptanceFailure();

        kernel.RetryTask(goal.Id, task.Id, "Correct the failed acceptance round.");

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(0, goal.ConsecutiveAcceptanceIdentityStaleCount);
        Assert.Null(goal.LastAcceptanceIdentityStaleAttemptId);
        Assert.Equal(1, kernel.RecordAcceptanceIdentityStale(goal.Id, "new-attempt"));
    }

    [Fact]
    public void RestoringSupersededAcceptanceFailureStartsANewConsecutiveSequence()
    {
        var (kernel, goal, _) = CappedStaleAcceptanceFailure();

        var restored = kernel.RestoreVerifiedForSupersededAcceptanceFailure(
            goal.Id, "branch-sha", "advanced-main-sha");

        Assert.True(restored);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(0, goal.ConsecutiveAcceptanceIdentityStaleCount);
        Assert.Null(goal.LastAcceptanceIdentityStaleAttemptId);
        Assert.Equal(1, kernel.RecordAcceptanceIdentityStale(goal.Id, "new-attempt"));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CappedStaleAcceptanceFailure()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement fix", AgentRole.Developer);
        var goal = kernel.CreateGoal("Capped stale acceptance", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "verification", "C:\\repo", 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance started.");
        for (var number = 1; number <= 3; number++)
        {
            Assert.Equal(number, kernel.RecordAcceptanceIdentityStale(goal.Id, $"stale-{number}"));
        }

        Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id, ["stale cap reached"], "Three consecutive stale attempts.", "branch-sha", "main-sha"));
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        return (kernel, goal, task);
    }
}
