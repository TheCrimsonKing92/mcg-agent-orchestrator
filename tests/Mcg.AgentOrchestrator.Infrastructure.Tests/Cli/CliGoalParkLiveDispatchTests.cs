using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalParkLiveDispatchTests : CliGoalParkTestSupport
{
    [Xunit.Fact]
    public async Task ParkApplication_ReturnsTypedLiveDispatchOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);
            var command = new CliCommandHandlers.GoalParkCommand(seed.GoalId.Value[..8], "Hold the goal", true);

            var outcome = CliPersistentStateRunner.ExecuteGoalParkApplication(command, seed.GoalId, seed.Repository);

            Xunit.Assert.Equal(CliCommandHandlers.GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches, outcome.Disposition);
            Xunit.Assert.True(outcome.ShouldSave);
            Xunit.Assert.Equal([1, 2], outcome.LiveDispatches!.Select(dispatch => dispatch.TaskNumber));
            Xunit.Assert.Equal([900001, 900002], outcome.LiveDispatches.Select(dispatch => dispatch.ProcessId));
            Xunit.Assert.All(outcome.LiveDispatches, dispatch => Xunit.Assert.Equal(AgentRole.Developer, dispatch.Role));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task LiveDryRun_NamesDispatchesWithoutPromisingCancellation()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);

            var result = RunCommand(
                ["park-goal", seed.GoalId.Value[..8], "Inspect only"],
                new GoalTransactionProbeRepository(seed.Repository), seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.False(result.Changed);
            Xunit.Assert.Equal(GoalStatus.Active, stored!.Status);
            Xunit.Assert.Contains("live dispatches still running after park: 2", result.Output);
            for (var index = 0; index < 2; index++)
            {
                Xunit.Assert.Contains($"task {index + 1} Developer {seed.TaskIds[index].Value[..8]}: pid {900001 + index} still running", result.Output);
            }

            Xunit.Assert.DoesNotContain("cancel", result.Output, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("stopped", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ParkRecordsStateAndNamesEveryRunningDispatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);
            var probe = new GoalTransactionProbeRepository(seed.Repository);

            var result = RunCommand(
                ["park-goal", seed.GoalId.Value[..8], "Hold the goal", "--confirm-goal-park"],
                probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal("cli:park-goal", probe.StateOperationName);
            Xunit.Assert.Equal(GoalStatus.Parked, stored!.Status);
            Xunit.Assert.Contains(stored.Timeline, item => item.Message == "Goal parked: Hold the goal");
            Xunit.Assert.Contains("Live dispatches still running: 2", result.Output);
            for (var index = 0; index < 2; index++)
            {
                var task = stored.Tasks.Single(item => item.Id == seed.TaskIds[index].Value);
                Xunit.Assert.True(task.LastProcess!.CompletedAt is null);
                Xunit.Assert.Contains(
                    $"task {index + 1} Developer {seed.TaskIds[index].Value[..8]}: pid {900001 + index} still running",
                    result.Output);
            }

            Xunit.Assert.DoesNotContain("cancelled", result.Output, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("stopped", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
