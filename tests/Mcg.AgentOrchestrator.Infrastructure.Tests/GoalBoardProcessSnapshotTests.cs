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
}
