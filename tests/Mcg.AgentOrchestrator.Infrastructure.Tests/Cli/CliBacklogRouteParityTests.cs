using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: both routes read the same immutable seed in a case-owned workspace.
public sealed class CliBacklogRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task ReadForms_PreserveWriterOutputAndHandlerErrors()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
            var repository = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            foreach (var args in CliBacklogReadOnlyRouteTests.ExplicitForms(seed.Prerequisite.Id[..8])
                .Concat(CliBacklogReadOnlyRouteTests.FlaggedForms(seed.Prerequisite.Id[..8])))
            {
                // This assertion makes parity fail on the pre-change writer-only route.
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var expected = Execute(args, repository, seed.Workspace, skipReadOnlyRoute: true);
                var actual = Execute(args, repository, seed.Workspace, skipReadOnlyRoute: false);
                Xunit.Assert.NotEmpty(expected);
                Xunit.Assert.Equal(expected, actual);
            }

            AssertErrorParity(["backlog-show", "missing"], repository, seed.Workspace);
            AssertErrorParity(["backlog-similar", "--id", "missing"], repository, seed.Workspace);
            AssertErrorParity(["backlog-similar", "--id"], repository, seed.Workspace);
            AssertErrorParity(["backlog-similar", "text", "--id", seed.Prerequisite.Id[..8]], repository, seed.Workspace);
            AssertErrorParity(["backlog-show"], repository, seed.Workspace);
            foreach (var verb in new[] { "backlog-list", "backlog-show", "backlog-similar" })
                AssertErrorParity([verb, "--unknown"], repository, seed.Workspace);

            // With 17 hexadecimal ids a shared first character is guaranteed, independent of time.
            var store = new BacklogStore(seed.Workspace.BacklogStorePath);
            var ids = new List<string> { seed.Prerequisite.Id, seed.Dependent.Id };
            for (var count = ids.Count; count < 17; count++)
                ids.Add((await store.AddAsync($"Shared prefix case {count}")).Id);
            var sharedPrefix = ids.GroupBy(id => id[..1]).First(group => group.Count() > 1).Key;
            Xunit.Assert.True(ids.Count(id => id.StartsWith(sharedPrefix, StringComparison.Ordinal)) >= 2);
            AssertErrorParity(["backlog-show", sharedPrefix], repository, seed.Workspace);
            AssertErrorParity(["backlog-similar", "--id", sharedPrefix], repository, seed.Workspace);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ListedGoalOmitted_PreservesUnreadableSnapshotWriterOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
            var linkedId = CliBacklogReadOnlyRouteTests.LinkedGoalId;
            var repository = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = seed.Workspace.SqliteStatePath, Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "UPDATE goals SET snapshot_json = 'unreadable snapshot' WHERE id = $id";
                corrupt.Parameters.AddWithValue("$id", linkedId);
                Xunit.Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
            }
            var writerKernel = await repository.LoadAsync();
            Xunit.Assert.DoesNotContain(writerKernel.Goals, goal => goal.Id.Value == linkedId);
            Xunit.Assert.Single(writerKernel.Goals);

            foreach (var args in CliBacklogReadOnlyRouteTests.ExplicitForms(seed.Prerequisite.Id[..8]))
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var probe = new ProbeStateRepository(seed.Kernel) { ThrowOnOutbox = true };
                probe.UnavailableGoalIds.Add(linkedId);
                var expected = Execute(args, repository, seed.Workspace, skipReadOnlyRoute: true);
                var actual = Execute(args, probe, seed.Workspace, skipReadOnlyRoute: false);
                Xunit.Assert.NotEmpty(expected);
                Xunit.Assert.Equal(expected, actual);
                Xunit.Assert.Equal(1, probe.ListGoalMetadataCount);
                Xunit.Assert.Equal(1, probe.LoadGoalsCount);
                Xunit.Assert.Equal([CliBacklogReadOnlyRouteTests.CompletedGoalId], probe.LoadedGoalIds);
                Xunit.Assert.Equal(0, probe.FullLoadAttempts);
                if (args.SequenceEqual(new[] { "backlog-show", seed.Prerequisite.Id[..8] }))
                    Xunit.Assert.DoesNotContain("Linked goals:", actual);
                if (args.SequenceEqual(new[] { "backlog-list" }))
                    Xunit.Assert.Contains($"{seed.Prerequisite.Id} | status=open | goal=-", actual);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertErrorParity(
        string[] args, ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
        Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var expected = Xunit.Record.Exception(() => Execute(args, repository, workspace, true));
        var actual = Xunit.Record.Exception(() => Execute(args, repository, workspace, false));
        Xunit.Assert.NotNull(expected);
        Xunit.Assert.NotNull(actual);
        Xunit.Assert.Equal(expected.GetType(), actual.GetType());
        Xunit.Assert.Equal(expected.Message, actual.Message);
    }

    private static string Execute(
        string[] args, ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }
}
