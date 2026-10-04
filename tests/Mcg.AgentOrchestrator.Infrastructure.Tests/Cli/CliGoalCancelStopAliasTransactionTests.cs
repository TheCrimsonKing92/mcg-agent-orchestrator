using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalCancelStopAliasTransactionTests : CliGoalParkTestSupport
{
    [Xunit.Fact]
    public async Task StopAliasCancel_AfterApplicationThrow_LeavesGoalActiveAndUnversioned()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var version = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath, seed.GoalId.Value);
            Xunit.Assert.NotNull(version);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                AfterApplication = (_, _, _) => throw new IOException("cancel pre-commit failure")
            };

            var result = RunCommand(
                ["stop", seed.GoalId.Value[..8], "Operator cancel", "--as", "cancel", "--confirm-goal-stop"],
                probe, seed.Workspace);

            var error = Xunit.Assert.IsType<IOException>(result.Error);
            Xunit.Assert.Equal("cancel pre-commit failure", error.Message);
            Xunit.Assert.Equal("cli:cancel-goal", probe.OperationName);
            Xunit.Assert.Equal(1, probe.ApplicationCount);
            Xunit.Assert.Equal(0, probe.WholeKernelLoadCount);
            Xunit.Assert.Equal(0, probe.WholeKernelSaveCount);
            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Equal(GoalStatus.Active, stored!.Status);
            Xunit.Assert.Equal(version, await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
                seed.Workspace.SqliteStatePath, seed.GoalId.Value));
            Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Message == "Operator cancel");
            var eventPath = EventPath(seed.Workspace, seed.GoalId);
            if (File.Exists(eventPath))
            {
                Xunit.Assert.DoesNotContain(File.ReadAllLines(eventPath), line => line.Contains("GoalCancelled"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task StopAliasCancel_ConcurrentWriterCannotBeOverwritten()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                AfterApplication = (attempt, snapshot, token) => writer.SaveGoalSnapshotsAsync(
                    [snapshot with { Objective = $"Concurrent writer {attempt}" }], token)
            };

            var result = RunCommand(
                ["stop", seed.GoalId.Value[..8], "Operator cancel", "--as", "cancel", "--confirm-goal-stop"],
                probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(
                ["stop", seed.GoalId.Value[..8], "Operator cancel", "--as", "cancel"]));
            Xunit.Assert.Equal("cli:cancel-goal", probe.OperationName);
            Xunit.Assert.True(probe.ApplicationCount > 0);
            Xunit.Assert.Equal(0, probe.WholeKernelLoadCount);
            Xunit.Assert.Equal(0, probe.WholeKernelSaveCount);
            Xunit.Assert.Equal($"Concurrent writer {probe.ApplicationCount}", stored!.Objective);
            if (result.Error is null)
            {
                Xunit.Assert.True(result.Changed);
                Xunit.Assert.Equal(GoalStatus.Cancelled, stored.Status);
                Xunit.Assert.Contains(stored.Timeline, item => item.Message == "Operator cancel");
            }
            else
            {
                Xunit.Assert.Contains("concurrent updates exhausted the retry budget", result.Error.Message);
                Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
                Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Message == "Operator cancel");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
