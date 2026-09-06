using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliGoalUnparkConcurrentWriterTests : CliGoalUnparkTestSupport
{
    [Xunit.Fact]
    public async Task CrossGoalState_RemainsBitForBitUnchanged()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root, "Target goal");
            var kernel = await seed.Repository.LoadAsync();
            var otherTask = new TaskSpec(TaskId.New(), "Other task", AgentRole.Tester);
            var other = kernel.CreateGoal("Other goal", [otherTask]);
            kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
            _ = kernel.RequestHumanInput(other.Id, otherTask.Id, "Other request");
            var seededSnapshot = kernel.ExportSnapshot();
            var otherVerification = new TaskVerificationSnapshot(
                "other-check", root, 0, "other evidence", string.Empty, DateTimeOffset.UtcNow);
            var goals = seededSnapshot.Goals.Select(goal => goal.Id == other.Id.Value
                ? goal with
                {
                    Tasks = goal.Tasks.Select(task => task.Id == otherTask.Id.Value
                        ? task with
                        {
                            Status = WorkTaskStatus.Failed,
                            LastVerification = otherVerification,
                            VerificationHistory = [otherVerification],
                            CriterionRetryCount = 2,
                            EmptyOutputRetryCount = 1
                        }
                        : task).ToArray()
                }
                : goal).ToArray();
            kernel = AgentOrchestratorKernel.FromSnapshot(seededSnapshot with { Goals = goals });
            await seed.Repository.SaveAsync(kernel);
            var before = await seed.Repository.LoadAsync();
            var beforeSnapshot = before.ExportSnapshot();
            var beforeOther = JsonSerializer.Serialize(beforeSnapshot.Goals.Single(goal => goal.Id == other.Id.Value));
            var beforeRequests = JsonSerializer.Serialize(beforeSnapshot.HumanInputRequests.Where(item => item.GoalId == other.Id.Value));
            var beforeVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                other.Id.Value);

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Target only", "--confirm-goal-unpark"],
                new GoalTransactionProbeRepository(seed.Repository),
                seed.Workspace);

            var after = await seed.Repository.LoadAsync();
            var afterSnapshot = after.ExportSnapshot();
            var afterOther = JsonSerializer.Serialize(afterSnapshot.Goals.Single(goal => goal.Id == other.Id.Value));
            var afterRequests = JsonSerializer.Serialize(afterSnapshot.HumanInputRequests.Where(item => item.GoalId == other.Id.Value));
            var afterVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath,
                other.Id.Value);
            Xunit.Assert.Null(result.Error);
            Xunit.Assert.Equal(beforeVersion, afterVersion);
            Xunit.Assert.Equal(beforeOther, afterOther);
            Xunit.Assert.Equal(beforeRequests, afterRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task RetryExhaustion_RefusesAndPreservesLatestWriterData()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateParkedSeed(root);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                AfterApplication = (attempt, snapshot, token) => writer.SaveGoalSnapshotsAsync(
                    [snapshot with { Objective = $"Concurrent writer {attempt}" }],
                    token)
            };

            var result = RunCommand(
                ["unpark-goal", seed.GoalId.Value[..8], "Never wins", "--confirm-goal-unpark"],
                probe,
                seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Equal(6, probe.ApplicationCount);
            Xunit.Assert.Contains("concurrent updates exhausted the retry budget", result.Error?.Message, StringComparison.Ordinal);
            Xunit.Assert.Equal(string.Empty, result.Output);
            Xunit.Assert.Equal("Concurrent writer 6", stored!.Objective);
            Xunit.Assert.Equal(GoalStatus.Parked, stored.Status);
            Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Message.StartsWith("Goal unparked:", StringComparison.Ordinal));
            Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
