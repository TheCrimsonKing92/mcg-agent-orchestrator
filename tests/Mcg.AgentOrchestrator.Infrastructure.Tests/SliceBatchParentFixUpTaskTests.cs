using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns an in-memory kernel and uses no process or global state.
public sealed class SliceBatchParentFixUpTaskTests
{
    [Xunit.Fact]
    public void AddFixUp_AppendsPendingDeveloperAfterReviewerAndRefusesDuplicate()
    {
        var kernel = new AgentOrchestratorKernel();
        var parent = CreateParent(kernel);
        const string findings = "Close the uncovered acceptance criterion.";

        var fixUp = kernel.AddSliceBatchParentFixUpTask(parent.Id, findings);

        Xunit.Assert.Equal([AgentRole.Reviewer, AgentRole.Developer], parent.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Same(fixUp, parent.Tasks[1]);
        Xunit.Assert.Equal($"Fix-up: {findings}", fixUp.Description);
        Xunit.Assert.Equal(WorkTaskStatus.Pending, fixUp.Status);
        Xunit.Assert.Null(fixUp.AssignedAgentId);
        Xunit.Assert.Equal(GoalStatus.Draft, parent.Status);
        Xunit.Assert.Contains(parent.Timeline, item => item.Kind == ProgressKind.TaskAdded && item.TaskId == fixUp.Id);

        Xunit.Assert.Throws<InvalidOperationException>(() => kernel.AddSliceBatchParentFixUpTask(parent.Id, "Another finding."));
        Xunit.Assert.Equal(2, parent.Tasks.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(WorkTaskStatus.Pending)]
    [Xunit.InlineData(WorkTaskStatus.Assigned)]
    [Xunit.InlineData(WorkTaskStatus.Running)]
    [Xunit.InlineData(WorkTaskStatus.WaitingForHuman)]
    [Xunit.InlineData(WorkTaskStatus.Completed)]
    [Xunit.InlineData(WorkTaskStatus.Failed)]
    [Xunit.InlineData(WorkTaskStatus.Cancelled)]
    public void AddFixUp_OnlyTerminalFixUpsPermitAnotherAppend(WorkTaskStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var parent = CreateParent(kernel);
        var first = kernel.AddSliceBatchParentFixUpTask(parent.Id, "Initial findings.");
        var snapshot = kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == parent.Id.Value
                ? goal with { Tasks = goal.Tasks.Select(task => task.Id == first.Id.Value ? task with { Status = status } : task).ToArray() }
                : goal).ToArray()
        });

        if (status is WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
        {
            var next = restored.AddSliceBatchParentFixUpTask(parent.Id, "Next findings.");
            Xunit.Assert.Same(next, restored.GetGoal(parent.Id).Tasks[2]);
        }
        else
        {
            Xunit.Assert.Throws<InvalidOperationException>(() => restored.AddSliceBatchParentFixUpTask(parent.Id, "Next findings."));
            Xunit.Assert.Equal(2, restored.GetGoal(parent.Id).Tasks.Count);
        }
    }

    [Xunit.Fact]
    public void AddFixUp_RefusesOrdinaryGoalChildAndBlankFindings()
    {
        var kernel = new AgentOrchestratorKernel();
        var ordinary = kernel.CreateGoal("Ordinary goal.");
        var parent = CreateParent(kernel);
        var child = Xunit.Assert.Single(kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id));

        Xunit.Assert.Throws<InvalidOperationException>(() => kernel.AddSliceBatchParentFixUpTask(ordinary.Id, "Findings."));
        Xunit.Assert.Throws<InvalidOperationException>(() => kernel.AddSliceBatchParentFixUpTask(child.Id, "Findings."));
        Xunit.Assert.Throws<ArgumentException>(() => kernel.AddSliceBatchParentFixUpTask(parent.Id, "  "));
        Xunit.Assert.Single(parent.Tasks);
    }

    private static Goal CreateParent(AgentOrchestratorKernel kernel)
    {
        var parent = kernel.CreateGoal("Whole goal.", [new TaskSpec(TaskId.New(), "Review whole goal.", AgentRole.Reviewer)]);
        kernel.CreateGoal("Child stream.",
            [new TaskSpec(TaskId.New(), "Implement stream.", AgentRole.Developer)], parent.Id);
        return parent;
    }
}
