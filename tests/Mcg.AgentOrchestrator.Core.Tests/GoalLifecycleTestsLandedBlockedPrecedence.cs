using Mcg.AgentOrchestrator.Core;

public sealed class GoalLifecycleTestsLandedBlockedPrecedence
{
    [Xunit.Fact]
    public void ResolveState_CompletedMergedBlocked_ReturnsMerged()
    {
        var goal = CreateCompletedGoal();

        Assert.Equal(GoalLifecycleState.Merged, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(IsBlocked: true, IsMerged: true)));
    }

    [Xunit.Fact]
    public void ResolveState_CompletedRecordedBlocked_ReturnsRecorded()
    {
        var goal = CreateCompletedGoal();

        Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(IsBlocked: true, IsMerged: true, IsRecorded: true)));
    }

    [Xunit.Fact]
    public void ResolveState_CompletedCleanedBlocked_ReturnsCleanedUp()
    {
        var goal = CreateCompletedGoal();

        Assert.Equal(GoalLifecycleState.CleanedUp, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(
                IsBlocked: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)));
    }

    [Xunit.Fact]
    public void ResolveState_ActiveBlocked_ReturnsBlocked()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Blocked active goal",
            [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, []);

        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(GoalLifecycleState.Blocked, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(IsBlocked: true)));
        Assert.Equal(GoalLifecycleState.Blocked, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(IsBlocked: true, IsMerged: true)));
    }

    [Xunit.Fact]
    public void ResolveState_CompletedUnmergedBlocked_ReturnsBlocked()
    {
        var goal = CreateCompletedGoal();

        Assert.Equal(GoalLifecycleState.Blocked, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(IsBlocked: true, IsMerged: false)));
        Assert.Equal(GoalLifecycleState.Blocked, GoalLifecycle.ResolveState(
            goal, new GoalLifecycleFacts(
                IsBlocked: true, IsMerged: false, IsRecorded: true, IsCleanedUp: true)));
    }

    private static Goal CreateCompletedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Complete work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Landed goal with deferred cleanup", [task]);
        kernel.ActivateGoal(goal.Id, []);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual", "C:\\repo", 0, "passed", string.Empty, DateTimeOffset.UnixEpoch));
        kernel.CompleteGoal(goal.Id, "Acceptance completed.");
        Assert.Equal(GoalStatus.Completed, goal.Status);
        return goal;
    }
}
