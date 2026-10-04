using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalStopParkRouteParityTests : CliGoalStopAliasTestSupport
{
    [Xunit.Fact]
    public async Task StopAsPark_InMemoryTerminatesEachLiveDispatchOnce_AndMatchesPersistentStdout()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(Path.Combine(root, "persistent"), 2);
            var kernel = await seed.Repository.LoadAsync();
            var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "in-memory"));
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var args = StopParts(seed, "park");
            var inMemoryCalls = new List<int>();
            var persistentCalls = new List<int>();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;

            string output;
            using (RecordTerminations(inMemoryCalls))
                output = CaptureConsole(() => Xunit.Assert.True(CliCommandDispatcher.ExecuteCommand(
                    args, kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
            CommandResult persistent;
            using (RecordTerminations(persistentCalls))
                persistent = RunCommand(args, seed.Repository, seed.Workspace);
            var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;

            Xunit.Assert.Null(persistent.Error);
            Xunit.Assert.True(persistent.Changed);
            Xunit.Assert.Equal([900001, 900002], inMemoryCalls);
            Xunit.Assert.Equal([900001, 900002], persistentCalls);
            Xunit.Assert.Equal(persistent.Output, output);
            Xunit.Assert.Equal(GoalStatus.Parked, kernel.GetGoal(seed.GoalId).Status);
            Xunit.Assert.Equal(GoalStatus.Parked, current!.Status);
            Xunit.Assert.Equal(GoalStatus.Parked, stored.Status);
            Xunit.Assert.All(kernel.GetGoal(seed.GoalId).Tasks, task => Xunit.Assert.True(task.LastProcess!.WasCancelled));
            Xunit.Assert.All(stored.Tasks, task => Xunit.Assert.True(task.LastProcess!.WasCancelled));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
