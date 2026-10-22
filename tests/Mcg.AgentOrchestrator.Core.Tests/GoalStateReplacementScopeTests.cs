using Mcg.AgentOrchestrator.Core;

public sealed class GoalStateReplacementScopeTests
{
    [Xunit.Fact]
    public void ReplaceGoalState_RepairsTargetWithoutTouchingUnrelatedHumanWait()
    {
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Target", [new TaskSpec(TaskId.New(), "Target task", AgentRole.Developer)]);
        var unrelated = kernel.CreateGoal("Unrelated", [new TaskSpec(TaskId.New(), "Other task", AgentRole.Developer)]);
        kernel.ActivateGoal(target.Id, DefaultAgents());
        kernel.ActivateGoal(unrelated.Id, DefaultAgents());
        var staleTarget = kernel.ExportGoalSnapshot(target.Id);
        staleTarget = staleTarget with { Tasks = staleTarget.Tasks.Select(task => task with { Status = WorkTaskStatus.WaitingForHuman }).ToArray() };
        var staleOther = kernel.ExportGoalSnapshot(unrelated.Id);
        staleOther = staleOther with { Tasks = staleOther.Tasks.Select(task => task with { Status = WorkTaskStatus.WaitingForHuman }).ToArray() };
        kernel.ReplaceGoalWithSnapshot(staleOther);
        var before = kernel.GetGoal(unrelated.Id);

        kernel.ReplaceGoalStateWithSnapshot(staleTarget, []);

        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetGoal(target.Id).Tasks.Single().Status);
        Assert.Same(before, kernel.GetGoal(unrelated.Id));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, before.Tasks.Single().Status);
        Assert.Empty(kernel.HumanInputRequests);
        // Bulk reload retains its existing repair contract.
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot());
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetGoal(unrelated.Id).Tasks.Single().Status);
    }
}
