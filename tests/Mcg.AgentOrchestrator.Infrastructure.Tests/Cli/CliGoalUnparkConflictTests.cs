using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliGoalUnparkConflictTests : CliGoalUnparkTestSupport
{
    [Xunit.Fact]
    public async Task VersionConflict_RecomputesAndPreservesConcurrentTaskData()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var beforeRequests = JsonSerializer.Serialize(
                (await seed.Repository.LoadAsync()).ExportSnapshot().HumanInputRequests
                    .Where(item => item.GoalId == seed.GoalId.Value));
            var callbackSawNoEventFile = false;
            var verification = new TaskVerificationSnapshot(
                "concurrent-check", root, 0, "preserved", string.Empty, DateTimeOffset.UtcNow);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                AfterApplication = async (attempt, snapshot, token) =>
                {
                    callbackSawNoEventFile = !File.Exists(EventPath(seed.Workspace, seed.GoalId));
                    if (attempt != 1)
                    {
                        return;
                    }

                    var tasks = snapshot.Tasks.Select(task => task.Id == seed.TaskId.Value
                        ? task with
                        {
                            Status = WorkTaskStatus.Failed,
                            LastVerification = verification,
                            VerificationHistory = [verification],
                            CriterionRetryCount = 3
                        }
                        : task).ToArray();
                    await writer.SaveGoalSnapshotsAsync(
                        [snapshot with { Objective = "Concurrent writer survived", Tasks = tasks }],
                        token);
                }
            };

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Resume after conflict", "--confirm-goal-unpark"],
                probe,
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            var afterRequests = JsonSerializer.Serialize(
                (await seed.Repository.LoadAsync()).ExportSnapshot().HumanInputRequests
                    .Where(item => item.GoalId == seed.GoalId.Value));
            var storedTask = stored!.Tasks.Single(task => task.Id == seed.TaskId.Value);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.True(probe.ApplicationCount >= 2);
            Xunit.Assert.True(callbackSawNoEventFile);
            Xunit.Assert.Equal("Concurrent writer survived", stored.Objective);
            Xunit.Assert.Equal(WorkTaskStatus.Failed, storedTask.Status);
            Xunit.Assert.Equal(3, storedTask.CriterionRetryCount);
            Xunit.Assert.Equal("preserved", storedTask.LastVerification?.StandardOutput);
            Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
            Xunit.Assert.Equal(beforeRequests, afterRequests);
            Xunit.Assert.Equal(1, stored.Timeline.Count(item => item.Message.StartsWith("Goal unparked:", StringComparison.Ordinal)));
            Xunit.Assert.Single(File.ReadAllLines(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task CancellationBeforeCommit_LeavesParkedStateUnchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var before = await seed.Repository.LoadGoalAsync(seed.GoalId);
            var version = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);
            var probe = new GoalTransactionProbeRepository(seed.Repository) { CancelBeforeCommit = true };

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Cancelled", "--confirm-goal-unpark"],
                probe,
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            var storedVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);
            Xunit.Assert.IsAssignableFrom<OperationCanceledException>(result.Error);
            Xunit.Assert.Equal(string.Empty, result.Output);
            Xunit.Assert.Equal(version, storedVersion);
            Xunit.Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(stored));
            Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ExceptionBeforeCommit_LeavesTransitionAndTimelineAbsent()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                ThrowBeforeCommit = new IOException("pre-commit failure")
            };

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Must not commit", "--confirm-goal-unpark"],
                probe,
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Equal("pre-commit failure", result.Error?.Message);
            Xunit.Assert.Equal(GoalStatus.Parked, stored!.Status);
            Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
            Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task LostResponseAfterCommit_LeavesCommittedTransitionDurable()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                ThrowAfterCommit = new IOException("response lost")
            };

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Commit then lose response", "--confirm-goal-unpark"],
                probe,
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Equal("response lost", result.Error?.Message);
            Xunit.Assert.Equal(string.Empty, result.Output);
            Xunit.Assert.Equal(GoalStatus.Active, stored!.Status);
            Xunit.Assert.Contains(stored.Timeline, item => item.Message == "Goal unparked: Commit then lose response");
            Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task RepeatUnpark_RefusesWithoutSecondTransition()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var probe = new GoalTransactionProbeRepository(seed.Repository);
            var args = new[] { "unpark-goal", seed.GoalId.Value[..8], "Once", "--confirm-goal-unpark" };
            var first = RunCommand(args, probe, seed.Workspace);
            var version = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);

            var second = RunCommand(args, probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            var storedVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                seed.GoalId.Value);
            Xunit.Assert.Null(first.Error);
            Xunit.Assert.Contains("only applies to Parked goals", second.Error?.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(string.Empty, second.Output);
            Xunit.Assert.Equal(version, storedVersion);
            Xunit.Assert.Equal(1, stored!.Timeline.Count(item => item.Message == "Goal unparked: Once"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
