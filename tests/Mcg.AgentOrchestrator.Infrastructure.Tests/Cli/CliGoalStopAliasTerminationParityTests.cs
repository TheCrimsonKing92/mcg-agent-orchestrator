using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalStopAliasTerminationParityTests : CliGoalStopAliasTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("park", 0, false)]
    [Xunit.InlineData("park", 0, true)]
    [Xunit.InlineData("park", 2, false)]
    [Xunit.InlineData("park", 2, true)]
    [Xunit.InlineData("abandon", 0, false)]
    [Xunit.InlineData("abandon", 0, true)]
    [Xunit.InlineData("abandon", 2, false)]
    [Xunit.InlineData("abandon", 2, true)]
    public async Task StopOutput_MatchesLegacyForInlineAndFileReasons(string mode, int live, bool confirmed)
    {
        foreach (var textFile in new[] { false, true })
        {
            var root = CreateTempDirectory();
            try
            {
                var seed = await CreateActiveSeed(Path.Combine(root, "alias"), live);
                var legacyKernel = await seed.Repository.LoadAsync();
                var legacyWorkspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "legacy"));
                StateDbMigrations.EnsureUpToDate(legacyWorkspace.SqliteStatePath);
                var legacySeed = new ParkSeed(legacyWorkspace,
                    new SqliteOrchestratorStateRepository(legacyWorkspace.SqliteStatePath), seed.GoalId, seed.TaskIds);
                await SeedAttention(seed);
                await SeedAttention(legacySeed);
                var reasonFile = textFile ? Path.Combine(root, "reason.txt") : null;
                if (reasonFile is not null) await File.WriteAllTextAsync(reasonFile, "Operator stop");
                var args = StopParts(seed, mode, confirmed, reasonFile);
                var version = await Version(seed);
                var calls = new List<int>();
                using var seam = RecordTerminations(calls);
                var probe = new GoalTransactionProbeRepository(seed.Repository);

                var result = RunCommand(args, probe, seed.Workspace);
                // The legacy route sees only nonexistent pids from CreateActiveSeed (900001+).
                // It exercises the existing handler without ever terminating a real process.
                var legacy = RunLegacy(args, legacyKernel, legacyWorkspace);

                Xunit.Assert.Null(result.Error);
                Xunit.Assert.Equal(legacy, result.Output);
                Xunit.Assert.Equal(confirmed, result.Changed);
                Xunit.Assert.Equal(confirmed ? Enumerable.Range(900001, live) : [], calls);
                var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
                if (!confirmed)
                {
                    Xunit.Assert.Equal(version, await Version(seed));
                    Xunit.Assert.Equal(0, probe.ApplicationCount + probe.StateApplicationCount);
                    Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
                    Xunit.Assert.All(stored.Tasks.Where(task => task.LastProcess is not null), task =>
                        Xunit.Assert.Null(task.LastProcess!.CompletedAt));
                }
                else if (mode == "park")
                {
                    var expected = string.Join(Environment.NewLine,
                        $"Goal parked {seed.GoalId.Value[..8]}.", $"Cancelled running dispatches: {live}",
                        "Resolved human waits: 0", "Resolved attention items: 1", "");
                    Xunit.Assert.Equal(expected, result.Output);
                    Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
                }
                else
                {
                    Xunit.Assert.Contains("No running dispatch process records.", result.Output);
                    Xunit.Assert.True(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
                }
                if (confirmed && live > 0)
                    Xunit.Assert.All(stored.Tasks, task => Xunit.Assert.True(task.LastProcess!.WasCancelled));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [Xunit.Fact]
    public async Task Park_ResolvesSeededHumanWaitOnlyAfterCommit()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 2);
            var kernel = await seed.Repository.LoadAsync();
            kernel.RequestHumanInput(seed.GoalId, seed.TaskIds[0], "Operator decision");
            await seed.Repository.SaveAsync(kernel);
            await SeedAttention(seed);
            var calls = new List<int>();
            using var seam = RecordTerminations(calls);
            var result = RunCommand(StopParts(seed, "park"), new GoalTransactionProbeRepository(seed.Repository), seed.Workspace);

            Xunit.Assert.Null(result.Error);
            Xunit.Assert.Equal(string.Join(Environment.NewLine, $"Goal parked {seed.GoalId.Value[..8]}.",
                "Cancelled running dispatches: 2", "Resolved human waits: 1", "Resolved attention items: 1", ""), result.Output);
            Xunit.Assert.True(Xunit.Assert.Single((await LoadGoalStateExactly(seed.Repository, seed.GoalId))!.HumanInputRequests).IsCompleted);
            Xunit.Assert.Equal([900001, 900002], calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string RunLegacy(IReadOnlyList<string> args, AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(args, kernel, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current));
    }
}
