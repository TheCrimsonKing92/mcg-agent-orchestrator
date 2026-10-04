using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalCancelStopAliasParityTests : CliGoalParkTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(0, false)]
    [Xunit.InlineData(1, false)]
    [Xunit.InlineData(0, true)]
    [Xunit.InlineData(1, true)]
    public async Task StopAliasCancel_Confirmed_MatchesDirectOutputAndState(int liveDispatchCount, bool textFile)
    {
        var root = CreateTempDirectory();
        try
        {
            var (aliasSeed, directSeed) = await CreateTwinSeeds(root, liveDispatchCount);
            var reasonFile = textFile ? Path.Combine(root, "reason.txt") : null;
            var reason = textFile ? "Operator cancel via file" : "Operator cancel";
            if (reasonFile is not null)
            {
                await File.WriteAllTextAsync(reasonFile, reason);
            }

            var aliasProbe = new GoalTransactionProbeRepository(aliasSeed.Repository);
            var directProbe = new GoalTransactionProbeRepository(directSeed.Repository);
            var alias = RunCommand(CancelParts(aliasSeed, true, reasonFile, true), aliasProbe, aliasSeed.Workspace);
            var direct = RunCommand(CancelParts(directSeed, false, reasonFile, true), directProbe, directSeed.Workspace);

            Xunit.Assert.Null(alias.Error);
            Xunit.Assert.Null(direct.Error);
            Xunit.Assert.True(alias.Changed);
            Xunit.Assert.True(direct.Changed);
            Xunit.Assert.Equal("cli:cancel-goal", aliasProbe.OperationName);
            Xunit.Assert.Equal("cli:cancel-goal", directProbe.OperationName);
            Xunit.Assert.Equal(direct.Output, alias.Output);
            var aliasStored = await aliasSeed.Repository.LoadGoalAsync(aliasSeed.GoalId);
            var directStored = await directSeed.Repository.LoadGoalAsync(directSeed.GoalId);
            Xunit.Assert.Equal(GoalStatus.Cancelled, aliasStored!.Status);
            Xunit.Assert.Equal(directStored!.Status, aliasStored.Status);
            var aliasEvent = Xunit.Assert.Single(aliasStored.Timeline, item => item.Kind == ProgressKind.GoalCancelled);
            var directEvent = Xunit.Assert.Single(directStored.Timeline, item => item.Kind == ProgressKind.GoalCancelled);
            Xunit.Assert.Equal(reason, aliasEvent.Message);
            Xunit.Assert.Equal(directEvent.Message, aliasEvent.Message);
            if (liveDispatchCount > 0)
            {
                Xunit.Assert.Contains("Live dispatches still running: 1", alias.Output);
                Xunit.Assert.Contains("pid 900001 still running", alias.Output);
                Xunit.Assert.Null(aliasStored.Tasks.Single().LastProcess!.CompletedAt);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, false)]
    [Xunit.InlineData(1, false)]
    [Xunit.InlineData(0, true)]
    [Xunit.InlineData(1, true)]
    public async Task StopAliasCancel_Unconfirmed_MatchesDirectRejection(int liveDispatchCount, bool textFile)
    {
        var root = CreateTempDirectory();
        try
        {
            var (aliasSeed, directSeed) = await CreateTwinSeeds(root, liveDispatchCount);
            var aliasVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                aliasSeed.Workspace.SqliteStatePath, aliasSeed.GoalId.Value);
            var directVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                directSeed.Workspace.SqliteStatePath, directSeed.GoalId.Value);
            Xunit.Assert.NotNull(aliasVersion);
            Xunit.Assert.NotNull(directVersion);
            var reasonFile = textFile ? Path.Combine(root, "reason.txt") : null;
            if (reasonFile is not null)
            {
                await File.WriteAllTextAsync(reasonFile, "Operator cancel via file");
            }

            var aliasProbe = new GoalTransactionProbeRepository(aliasSeed.Repository);
            var directProbe = new GoalTransactionProbeRepository(directSeed.Repository);
            // RunCommand has no selected goal: both Active targets are non-current.
            var alias = RunCommand(CancelParts(aliasSeed, true, reasonFile, false), aliasProbe, aliasSeed.Workspace);
            var direct = RunCommand(CancelParts(directSeed, false, reasonFile, false), directProbe, directSeed.Workspace);

            var aliasError = Xunit.Assert.IsType<InvalidOperationException>(alias.Error);
            var directError = Xunit.Assert.IsType<InvalidOperationException>(direct.Error);
            Xunit.Assert.Equal("cancel-goal requires --confirm-goal-stop for active or non-current goals.", aliasError.Message);
            Xunit.Assert.Equal(directError.Message, aliasError.Message);
            Xunit.Assert.Equal(direct.Output, alias.Output);
            Xunit.Assert.Equal(string.Empty, alias.Output);
            Xunit.Assert.Equal("cli:cancel-goal", aliasProbe.OperationName);
            Xunit.Assert.Equal(0, aliasProbe.ApplicationCount);
            foreach (var seed in new[] { aliasSeed, directSeed })
            {
                var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
                Xunit.Assert.Equal(GoalStatus.Active, stored!.Status);
                Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Kind == ProgressKind.GoalCancelled);
                Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
            }

            Xunit.Assert.Equal(aliasVersion, await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                aliasSeed.Workspace.SqliteStatePath, aliasSeed.GoalId.Value));
            Xunit.Assert.Equal(directVersion, await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                directSeed.Workspace.SqliteStatePath, directSeed.GoalId.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(ParkSeed Alias, ParkSeed Direct)> CreateTwinSeeds(string root, int liveDispatchCount)
    {
        var alias = await CreateActiveSeed(Path.Combine(root, "alias"), liveDispatchCount);
        // Clone the saved kernel so goal/task ids and live-dispatch paths are byte-identical.
        var kernel = await alias.Repository.LoadAsync();
        var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(root, "direct"));
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        return (alias, new ParkSeed(workspace, repository, alias.GoalId, alias.TaskIds));
    }

    private static IReadOnlyList<string> CancelParts(ParkSeed seed, bool alias, string? reasonFile, bool confirmed)
    {
        var parts = new List<string> { alias ? "stop" : "cancel-goal", seed.GoalId.Value[..8] };
        if (reasonFile is null)
        {
            parts.Add("Operator cancel");
        }
        else
        {
            parts.AddRange(["--text-file", reasonFile]);
        }

        if (alias)
        {
            parts.AddRange(["--as", "cancel"]);
        }

        if (confirmed)
        {
            parts.Add("--confirm-goal-stop");
        }

        return parts;
    }
}
