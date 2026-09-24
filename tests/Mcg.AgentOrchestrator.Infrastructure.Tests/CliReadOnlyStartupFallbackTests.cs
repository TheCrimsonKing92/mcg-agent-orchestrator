using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliReadOnlyStartupFallbackTests : CliCommandTestBase
{
    [Xunit.Fact]
    public void DeclinedReadRouteHydratesBeforeRunningNormalCommand()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Fallback goal", [new TaskSpec(TaskId.New(), "Fallback task", AgentRole.Developer)]);
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var changed = true;

            var output = CaptureConsole(() => changed = CliReadOnlyStartupHydration.ExecuteStartupCommand(
                ["status", goal.Id.Value[..8], "--tasks-only"], repository,
                OrchestratorWorkspace.ForDirectory(root), ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
                hydrated: false));

            Xunit.Assert.False(changed);
            Xunit.Assert.Contains("Fallback task", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(1, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
