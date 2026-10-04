using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalLifecycleRouteParityTests : CliGoalStopAliasTestSupport
{
    [Xunit.Fact]
    public async Task CancelGoal_InMemoryAndPersistentRoutesMatch_WithLiveDispatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(Path.Combine(root, "persistent"), 1);
            // Clone the exact seed, including ids, process records and timestamps.
            var kernel = await seed.Repository.LoadAsync();
            var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "in-memory"));
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            string[] args = ["cancel-goal", seed.GoalId.Value[..8], "Operator cancel", "--confirm-goal-stop"];

            var output = RunInMemory(args, kernel, workspace);
            var persistent = RunCommand(args, seed.Repository, seed.Workspace);
            var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;

            Xunit.Assert.Null(persistent.Error);
            Xunit.Assert.True(persistent.Changed);
            Xunit.Assert.Equal(persistent.Output, output);
            Xunit.Assert.Equal(GoalStatus.Cancelled, kernel.GetGoal(seed.GoalId).Status);
            Xunit.Assert.Equal(GoalStatus.Cancelled, stored.Status);
            Xunit.Assert.Contains("Live dispatches still running: 1", output);
            Xunit.Assert.Contains($"task 1 Developer {seed.TaskIds[0].Value[..8]}: pid 900001 still running", output);
            Xunit.Assert.True(kernel.GetTask(seed.GoalId, seed.TaskIds[0]).LastProcess!.IsRunning);
            Xunit.Assert.Null(Xunit.Assert.Single(stored.Tasks).LastProcess!.CompletedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task ParkGoal_InMemoryAndPersistentRoutesMatch_WithoutTerminatingWorkers()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(Path.Combine(root, "persistent"), 1);
            var kernel = await seed.Repository.LoadAsync();
            var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "in-memory"));
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            string[] args = ["park-goal", seed.GoalId.Value[..8], "Operator stop", "--confirm-goal-park"];
            var inMemoryCalls = new List<int>();
            var persistentCalls = new List<int>();

            string output;
            using (RecordTerminations(inMemoryCalls))
                output = RunInMemory(args, kernel, workspace);
            CommandResult persistent;
            using (RecordTerminations(persistentCalls))
                persistent = RunCommand(args, seed.Repository, seed.Workspace);
            var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;

            Xunit.Assert.Null(persistent.Error);
            Xunit.Assert.True(persistent.Changed);
            Xunit.Assert.Equal(persistent.Output, output);
            Xunit.Assert.Empty(inMemoryCalls);
            Xunit.Assert.Empty(persistentCalls);
            Xunit.Assert.Equal(GoalStatus.Parked, kernel.GetGoal(seed.GoalId).Status);
            Xunit.Assert.Equal(GoalStatus.Parked, stored.Status);
            Xunit.Assert.Contains("Live dispatches still running: 1", output);
            Xunit.Assert.True(kernel.GetTask(seed.GoalId, seed.TaskIds[0]).LastProcess!.IsRunning);
            Xunit.Assert.Null(Xunit.Assert.Single(stored.Tasks).LastProcess!.CompletedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string RunInMemory(IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => Xunit.Assert.True(CliCommandDispatcher.ExecuteCommand(
            args, kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }
}
