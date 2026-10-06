using Mcg.AgentOrchestrator.Core;

public sealed class GoalFindTaskVisibilityTests
{
    [Fact]
    public void FindTask_IsPublic()
    {
        var method = typeof(Goal).GetMethod("FindTask", [typeof(TaskId)]);

        Assert.NotNull(method);
        Assert.True(method.IsPublic);
    }

    [Fact]
    public void FindTask_ExistingTask_ReturnsSameInstance()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Find an existing task", [task]);

        var foundTask = goal.FindTask(task.Id);

        Assert.Same(goal.Tasks.Single(item => item.Id == task.Id), foundTask);
    }

    [Fact]
    public void FindTask_UnknownTask_ThrowsKeyNotFoundException()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reject an unknown task", [task]);

        var exception = Assert.Throws<KeyNotFoundException>(() => goal.FindTask(TaskId.New()));

        Assert.Contains("was not found", exception.Message);
    }
}
