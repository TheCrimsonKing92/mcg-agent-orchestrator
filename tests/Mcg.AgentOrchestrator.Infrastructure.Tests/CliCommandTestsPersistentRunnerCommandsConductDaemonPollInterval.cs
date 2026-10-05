using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsConductDaemonPollInterval : CliCommandTestBase
{
    [Xunit.Fact]
    public void DaemonHonorsPollSecondsWithoutWatch()
    {
        var output = ExecuteLoop(
            ["conduct", "--loop", "--daemon", "--poll-seconds", "7", "--max-iterations", "1"]);

        Xunit.Assert.Contains("Persistent mode; polling every 7s", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DaemonHonorsLegacyWatchIntervalWithoutWatch()
    {
        var output = ExecuteLoop(
            ["conduct", "--loop", "--daemon", "--watch-interval", "9", "--max-iterations", "1"]);

        Xunit.Assert.Contains("Persistent mode; polling every 9s", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DaemonUsesDefaultIntervalWhenNoFlagIsPassed()
    {
        var output = ExecuteLoop(["conduct", "--loop", "--daemon", "--max-iterations", "1"]);

        Xunit.Assert.Contains(
            $"Persistent mode; polling every {ConductorBatchLoop.DefaultWatchIntervalSeconds}s",
            output,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DaemonRejectsZeroPollSecondsWithTheWatchError()
    {
        var watchError = Xunit.Assert.ThrowsAny<ArgumentException>(() => ExecuteLoop(
            ["conduct", "--loop", "--watch", "--poll-seconds", "0", "--max-iterations", "0"]));
        var daemonError = Xunit.Assert.ThrowsAny<ArgumentException>(() => ExecuteLoop(
            ["conduct", "--loop", "--daemon", "--poll-seconds", "0"]));

        Xunit.Assert.Equal("--poll-seconds requires a positive integer value.", watchError.Message);
        Xunit.Assert.Equal(watchError.Message, daemonError.Message);
    }

    private static string ExecuteLoop(string[] args)
    {
        var root = CreateShortAcceptanceRepository();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            // An empty daemon backlog does not consume iterations; request stop before the first tick.
            File.WriteAllText(Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName), "stop");
            var repository = new InMemoryTransactionalStateRepository(new AgentOrchestratorKernel());
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            return CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                args,
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                acceptanceCleanupContext: CreateIsolatedCleanupContext(workspace)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, null);
        }
    }
}
