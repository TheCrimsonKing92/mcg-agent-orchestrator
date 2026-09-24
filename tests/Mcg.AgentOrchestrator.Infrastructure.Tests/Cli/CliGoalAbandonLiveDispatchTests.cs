using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalAbandonLiveDispatchTests : CliGoalParkTestSupport
{
    [Xunit.Fact]
    public async Task AbandonApplication_ReturnsTypedLiveDispatchOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);
            var hooks = WorktreeCleanupContext.Load(attentionStoreDirectory: seed.Workspace.OrchestratorDirectory).Hooks;
            var command = new CliCommandHandlers.GoalAbandonCommand(seed.GoalId.Value[..8], "Operator abandon", true);
            var outcome = CliPersistentStateRunner.ExecuteGoalAbandonApplication(
                command, seed.GoalId, seed.Repository, seed.Workspace, hooks);

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
    public async Task AbandonRecordsStateAndNamesEveryRunningDispatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, liveDispatchCount: 2);
            var probe = new GoalTransactionProbeRepository(seed.Repository);
            var result = RunCommand(
                ["abandon-goal", seed.GoalId.Value[..8], "Operator abandon", "--confirm-goal-abandon"],
                probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal("cli:abandon-goal", probe.OperationName);
            Xunit.Assert.Equal(GoalStatus.Cancelled, stored!.Status);
            Xunit.Assert.Contains(stored.Timeline, item => item.Message == "Operator abandon");
            AssertLiveDispatchOutput(seed, stored, result.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task AbandonWithoutLiveDispatch_KeepsPlanOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var result = RunCommand(
                ["abandon-goal", seed.GoalId.Value[..8], "Operator abandon", "--confirm-goal-abandon"],
                new GoalTransactionProbeRepository(seed.Repository), seed.Workspace);
            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal(GoalStatus.Cancelled, stored!.Status);
            Xunit.Assert.Contains("Dry run: False", result.Output);
            Xunit.Assert.Contains("Can apply: True", result.Output);
            Xunit.Assert.DoesNotContain("Live dispatches still running", result.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertLiveDispatchOutput(ParkSeed seed, GoalSnapshot stored, string output)
    {
        Xunit.Assert.Contains("Live dispatches still running: 2", output);
        for (var index = 0; index < 2; index++)
        {
            var task = stored.Tasks.Single(item => item.Id == seed.TaskIds[index].Value);
            Xunit.Assert.Null(task.LastProcess!.CompletedAt);
            Xunit.Assert.Contains(
                $"task {index + 1} Developer {seed.TaskIds[index].Value[..8]}: pid {900001 + index} still running",
                output);
        }

        Xunit.Assert.DoesNotContain("stopped", output, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("Cancel running dispatch", output, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.DoesNotContain("Cancelled running dispatches", output, StringComparison.OrdinalIgnoreCase);
    }
}
