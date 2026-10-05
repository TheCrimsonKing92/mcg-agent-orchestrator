using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class ReadinessOutputStabilityTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoCrossGoalHold_StdoutEqualsMainSingleGoalComposition(bool selfHold)
    {
        var root = CreateTempDirectory();
        try
        {
            var (seed, source, target, seedAgents) = ReadinessTestSeed.Create(selfHold: selfHold);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var path = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(path);
            var repository = new SqliteOrchestratorStateRepository(path);
            await repository.SaveAsync(seed);
            var singleGoalKernel = await repository.LoadGoalsAsync([target.Id]);
            Assert.Single(singleGoalKernel.Goals);
            Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
            var agents = seedAgents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            var command = new[] { "readiness", target.Id.Value[..8] };
            var providers = new InMemoryModelProviderRegistry([]);

            // Main routes this report through a single-goal kernel with no wider reload delegate.
            var baseline = AsyncLocalConsoleRouter.Capture(() => CliCommandDispatcher.ExecuteCommand(
                command, singleGoalKernel, workspace, ref agents, providers, ref profiles, ref current));
            current = null;
            var actual = AsyncLocalConsoleRouter.Capture(() => CliPersistentStateRunner.ExecuteCommand(
                command, repository, workspace, ref agents, providers, ref profiles, ref current));

            Assert.Equal(baseline, actual);
            Assert.DoesNotContain(source.Objective, actual);
            if (selfHold)
                Assert.Contains("Blocker provider-budget-exhausted:", actual);
            else
                Assert.DoesNotContain("provider-budget-exhausted", actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
