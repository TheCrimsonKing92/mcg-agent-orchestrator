using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliAttentionNextReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("attention")]
    [Xunit.InlineData("attention", "show")]
    [Xunit.InlineData("next", "--full", "abc10000")]
    public void PureReadFormSkipsStartupHydration(params string[] args) =>
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Xunit.Fact]
    public void AttentionReadsAllStoredGoalsWithoutAHiddenCountCap()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < 257; index++)
                kernel.CreateGoal($"Goal {index}");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var output = CaptureConsole(() => Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                ["attention"], repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out var changed) && !changed));
            Xunit.Assert.Equal(CliAttentionNextGoldenFixtures.EmptyAttention, output);
            Xunit.Assert.Equal(257, repository.LoadedGoalIds.Count);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
