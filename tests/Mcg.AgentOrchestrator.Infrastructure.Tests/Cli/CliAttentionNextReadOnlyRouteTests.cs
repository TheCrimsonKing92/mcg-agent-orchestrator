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

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void AttentionReadsAllStoredGoalsWithoutAHiddenCountCap(bool supportsAttentionProjection)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < 257; index++)
                kernel.CreateGoal($"Goal {index}");
            var repository = new ProbeStateRepository(kernel)
            {
                ThrowOnOutbox = true,
                SupportsAttentionProjection = supportsAttentionProjection
            };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);
            var output = CaptureConsole(() => Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(
                ["attention"], repository, workspace, providers, null,
                ref agents, ref profiles, ref currentGoal, out var changed) && !changed));
            Xunit.Assert.Equal(CliAttentionNextGoldenFixtures.EmptyAttention, output);
            if (supportsAttentionProjection)
            {
                Xunit.Assert.Empty(repository.RequestedGoalIds);
                Xunit.Assert.Empty(repository.LoadedGoalIds);
                Xunit.Assert.Equal(0, repository.LoadGoalsCount);
            }
            else
            {
                // Unsupported projections require every snapshot to be checked for later duties.
                var expectedIds = kernel.Goals.Select(goal => goal.Id.Value).OrderBy(id => id).ToArray();
                Xunit.Assert.Equal(expectedIds, repository.RequestedGoalIds.OrderBy(id => id));
                Xunit.Assert.Equal(expectedIds, repository.LoadedGoalIds.OrderBy(id => id));
                Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            }
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
