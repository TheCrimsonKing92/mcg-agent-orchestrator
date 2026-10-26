using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsDashboardRemoval : CliCommandTestBase
{
    [Theory]
    [InlineData("http://localhost:5087")]
    [InlineData("https://localhost:5087")]
    public void MonitorGoalRejectsHttpUrlAsInvalidGoal(string url)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        using var writer = new StringWriter();
        var error = Assert.ThrowsAny<ArgumentException>(() =>
            GoalMonitoringSubscriptionCommand.RunAsync(
                ["monitor-goal", url, "abc123"], writer, new AgentOrchestratorKernel(), workspace,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default()).GetAwaiter().GetResult());
        Assert.Contains($"Invalid goal argument '{url}'.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorGoalRetainsLocalSnapshotAndLifecycleEvents()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Local lifecycle", AgentRole.Developer);
        var goal = kernel.CreateGoal("Local monitor", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "Started local work.");

        using var writer = new StringWriter();
        GoalMonitoringSubscriptionCommand.RunAsync(
            ["monitor-goal", goal.Id.Value, "--once"], writer, kernel, workspace,
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default()).GetAwaiter().GetResult();
        var output = writer.ToString();

        Assert.Contains("event: goal.snapshot", output, StringComparison.Ordinal);
        Assert.Contains("event: task.status", output, StringComparison.Ordinal);
        Assert.Contains("Started local work.", output, StringComparison.Ordinal);
    }
}
