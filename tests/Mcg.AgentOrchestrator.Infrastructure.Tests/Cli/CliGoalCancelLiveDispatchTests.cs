using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalCancelLiveDispatchTests : CliGoalParkTestSupport
{
    [Xunit.Fact]
    public async Task CancelApplication_ReturnsTypedLiveDispatchOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);
            var command = CliCommandHandlers.PrepareGoalCancelCommand(
                ["cancel-goal", seed.GoalId.Value[..8], "Operator cancel", "--confirm-goal-stop"]);
            var outcome = CliPersistentStateRunner.ExecuteGoalCancelApplication(
                command, seed.GoalId, null, seed.Repository);

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
    public async Task CancelRecordsStateAndNamesEveryRunningDispatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);
            var probe = new GoalTransactionProbeRepository(seed.Repository);
            var result = RunCommand(
                ["cancel-goal", seed.GoalId.Value[..8], "Operator cancel", "--confirm-goal-stop"],
                probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal("cli:cancel-goal", probe.OperationName);
            Xunit.Assert.Equal(GoalStatus.Cancelled, stored!.Status);
            Xunit.Assert.Contains(stored.Timeline, item => item.Message == "Operator cancel");
            Xunit.Assert.Contains("Live dispatches still running: 2", result.Output);
            for (var index = 0; index < 2; index++)
            {
                var task = stored.Tasks.Single(item => item.Id == seed.TaskIds[index].Value);
                Xunit.Assert.Null(task.LastProcess!.CompletedAt);
                Xunit.Assert.Contains(
                    $"task {index + 1} Developer {seed.TaskIds[index].Value[..8]}: pid {900001 + index} still running",
                    result.Output);
            }

            Xunit.Assert.DoesNotContain("stopped", result.Output, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("Cancel running dispatch", result.Output, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.DoesNotContain("Cancelled running dispatches", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task CancelWithoutLiveDispatch_KeepsGoalOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var result = RunCommand(
                ["cancel-goal", seed.GoalId.Value[..8], "Operator cancel", "--confirm-goal-stop"],
                new GoalTransactionProbeRepository(seed.Repository), seed.Workspace);
            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal(GoalStatus.Cancelled, stored!.Status);
            Xunit.Assert.DoesNotContain("Live dispatches still running", result.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
