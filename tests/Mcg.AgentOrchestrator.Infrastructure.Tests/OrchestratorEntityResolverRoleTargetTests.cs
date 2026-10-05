using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Pure goal-local resolution; no shared state or I/O, so these tests are parallel-safe.
public sealed class OrchestratorEntityResolverRoleTargetTests
{
    [Theory]
    [InlineData("developer", 3)]
    [InlineData("Developer", 3)]
    [InlineData("reviewer", 5)]
    public void RoleNameResolvesFiveRoleGoal(string value, int number)
    {
        var goal = CreateGoal(AgentRole.Planner, AgentRole.Researcher, AgentRole.Developer,
            AgentRole.Tester, AgentRole.Reviewer);

        Assert.Same(goal.Tasks[number - 1],
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, value));
    }

    [Fact]
    public void RoleNameResolvesDeveloperFirstWorkflow()
    {
        var goal = CreateGoal(AgentRole.Developer, AgentRole.Reviewer);

        Assert.Same(goal.Tasks[0],
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, "developer"));
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Ideation)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void EveryEnumRoleNameResolves(AgentRole role)
    {
        var goal = CreateGoal(role);

        Assert.Same(goal.Tasks[0],
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, role.ToString().ToLowerInvariant()));
    }

    [Fact]
    public void DuplicateRoleListsEveryMatchingDisplayNumberRegardlessOfStatus()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Duplicate developers", CreateTasks(
            AgentRole.Planner, AgentRole.Developer, AgentRole.Reviewer, AgentRole.Developer));
        kernel.ReportTaskProgress(goal.Id, goal.Tasks[1].Id, WorkTaskStatus.Completed, "First done");
        kernel.ReportTaskProgress(goal.Id, goal.Tasks[3].Id, WorkTaskStatus.Failed, "Second failed");

        var error = Assert.Throws<KeyNotFoundException>(() =>
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, "developer"));

        Assert.Contains("Developer", error.Message, StringComparison.Ordinal);
        Assert.Contains("2, 4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRoleNamesTheAbsentTask()
    {
        var goal = CreateGoal(AgentRole.Developer, AgentRole.Reviewer);

        var error = Assert.Throws<KeyNotFoundException>(() =>
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, "tester"));

        Assert.Contains("no Tester task", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegerAndIdPrefixKeepTheirExistingResolution()
    {
        var goal = CreateGoal(AgentRole.Planner, AgentRole.Researcher, AgentRole.Developer,
            AgentRole.Tester, AgentRole.Reviewer);

        Assert.Same(goal.Tasks[2], OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, "3"));
        Assert.Same(goal.Tasks[2], OrchestratorEntityResolver.GetTaskByDisplayNumber(
            goal, goal.Tasks[2].Id.Value[..16].ToUpperInvariant()));
        var error = Assert.Throws<KeyNotFoundException>(() =>
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, "9"));
        Assert.Equal("Task number '9' was not found; goal has 5 task(s).", error.Message);
    }

    [Theory]
    [InlineData("zzzz")]
    [InlineData("dev")]
    [InlineData("planner,developer")]
    [InlineData(" developer ")]
    public void NonRoleNamesKeepTheExistingNotFoundMessage(string value)
    {
        var goal = CreateGoal(AgentRole.Planner, AgentRole.Developer);

        var error = Assert.Throws<KeyNotFoundException>(() =>
            OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, value));

        Assert.Equal($"Task '{value}' was not found.", error.Message);
    }

    private static Goal CreateGoal(params AgentRole[] roles) =>
        new AgentOrchestratorKernel().CreateGoal("Resolve a role target", CreateTasks(roles));

    private static TaskSpec[] CreateTasks(params AgentRole[] roles) =>
        roles.Select(role => new TaskSpec(TaskId.New(), $"Do {role} work", role)).ToArray();
}
