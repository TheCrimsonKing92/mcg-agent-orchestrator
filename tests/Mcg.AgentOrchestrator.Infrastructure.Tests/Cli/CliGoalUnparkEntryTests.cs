using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliGoalUnparkEntryTests : CliGoalUnparkTestSupport
{
    [Xunit.Fact]
    public async Task ConfirmedUnpark_UsesGoalCasAndRendersAfterCommit()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var probe = new GoalTransactionProbeRepository(seed.Repository);

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Resume", "--confirm-goal-unpark"],
                probe,
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(result.Changed);
            Xunit.Assert.Equal(0, probe.WholeKernelLoadCount);
            Xunit.Assert.Equal(0, probe.WholeKernelSaveCount);
            Xunit.Assert.Equal(GoalStatus.Active, stored!.Status);
            Xunit.Assert.Equal("Goal unparked: Resume", stored.Timeline[^1].Message);
            Xunit.Assert.Equal(
                $"Goal unparked {seed.GoalId.Value[..8]}.{Environment.NewLine}Status change: Parked -> Active{Environment.NewLine}",
                result.Output);
            Xunit.Assert.Single(File.ReadAllLines(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task DryRun_ReadsOneGoalAndLeavesVersionUnchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var probe = new GoalTransactionProbeRepository(seed.Repository);
            var before = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Inspect only"],
                probe,
                seed.Workspace);

            var after = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);
            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.False(result.Changed);
            Xunit.Assert.Equal(before, after);
            Xunit.Assert.Equal(GoalStatus.Parked, stored!.Status);
            Xunit.Assert.Contains("Goal unpark dry run", result.Output, StringComparison.Ordinal);
            Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ProjectionFailure_ReportsCommittedStateAndDiagnostic()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var beforeVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);
            Directory.CreateDirectory(EventPath(seed.Workspace, seed.GoalId));

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Commit despite projection", "--confirm-goal-unpark"],
                new GoalTransactionProbeRepository(seed.Repository),
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            var afterVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);
            Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(result.Error);
            Xunit.Assert.Contains(
                "is committed Active, but lifecycle event projection failed",
                result.Error.Message,
                StringComparison.Ordinal);
            Xunit.Assert.Equal(
                $"Goal unparked {seed.GoalId.Value[..8]}.{Environment.NewLine}Status change: Parked -> Active{Environment.NewLine}",
                result.Output);
            Xunit.Assert.Equal(GoalStatus.Active, stored!.Status);
            Xunit.Assert.Equal(beforeVersion + 1, afterVersion);
            Xunit.Assert.Equal(1, stored.Timeline.Count(item => item.Message == "Goal unparked: Commit despite projection"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task OpenHumanWait_UnparkPreservesTaskAndRequest()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedOpenHumanWaitSeed(root);
            var before = await LoadGoalStateExactly(seed.Repository, seed.GoalId);
            var beforeRequest = before!.HumanInputRequests.Single(item => item.Id == seed.HumanInputRequestId.Value);

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Preserve wait", "--confirm-goal-unpark"],
                new GoalTransactionProbeRepository(seed.Repository),
                seed.Workspace);

            var after = await LoadGoalStateExactly(seed.Repository, seed.GoalId);
            var stored = after!.Goal;
            var storedTask = stored.Tasks.Single(item => item.Id == seed.TaskId.Value);
            var storedRequest = after.HumanInputRequests.Single(item => item.Id == seed.HumanInputRequestId.Value);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
            Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, storedTask.Status);
            Xunit.Assert.Equal(JsonSerializer.Serialize(beforeRequest), JsonSerializer.Serialize(storedRequest));
            Xunit.Assert.False(storedRequest.IsCompleted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ParkGoal_RemainsOnWholeKernelPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var probe = new GoalTransactionProbeRepository(seed.Repository);

            var result = RunCommand(
                ["park-goal", seed.GoalId.Value[..8], "Stay parked", "--confirm-goal-park"],
                probe,
                seed.Workspace);

            Xunit.Assert.Equal("whole-kernel load sentinel", result.Error?.Message);
            Xunit.Assert.Equal(1, probe.WholeKernelLoadCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void NonParkedGoal_ReturnsTypedRejectedOutcome()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Active goal");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var command = new CliCommandHandlers.GoalUnparkCommand(goal.Id.Value[..8], "No-op", true);

        var outcome = CliCommandHandlers.ApplyGoalUnparkWithoutRendering(command, kernel, goal.Id);

        Xunit.Assert.Equal(CliCommandHandlers.GoalLifecycleTransitionDisposition.Rejected, outcome.Disposition);
        Xunit.Assert.Equal(GoalStatus.Active, outcome.ObservedStatus);
        Xunit.Assert.Contains("only applies to Parked goals", outcome.RejectionReason, StringComparison.Ordinal);
    }
}
