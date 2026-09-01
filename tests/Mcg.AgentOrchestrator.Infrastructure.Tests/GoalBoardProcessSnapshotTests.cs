using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalBoardProcessSnapshotTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void GoalBoardCommandCapturesProcessSnapshotOnceForMultipleGoals()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "First goal");
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Second goal");
            var calls = 0;

            _ = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () =>
                {
                    calls++;
                    return ProcessCommandLineSnapshot.Empty;
                }));

            Xunit.Assert.Equal(1, calls);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DashboardRender_MultipleGoalsAndTasks_ReusesProcessSnapshot()
    {
        var kernel = new AgentOrchestratorKernel();
        var agents = AgentCatalog.Default().Agents;
        var first = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "First render goal");
        var second = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, agents, "Second render goal");
        kernel.AddTask(first.Id, AgentRole.Tester, "First extra task", agents);
        kernel.AddTask(second.Id, AgentRole.Tester, "Second extra task", agents);
        var calls = 0;

        var html = Mcg.AgentOrchestrator.App.Dashboard.Rendering.DashboardRenderer.Render(
            kernel,
            new Mcg.AgentOrchestrator.App.Dashboard.Rendering.DashboardRenderOptions(),
            () =>
            {
                calls++;
                return ProcessCommandLineSnapshot.Empty;
            });

        Xunit.Assert.Contains("First render goal", html);
        Xunit.Assert.Contains("Second render goal", html);
        Xunit.Assert.Equal(1, calls);
    }
}
