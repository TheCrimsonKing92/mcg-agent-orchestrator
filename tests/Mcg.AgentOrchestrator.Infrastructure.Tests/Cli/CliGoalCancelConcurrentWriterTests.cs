using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalCancelConcurrentWriterTests : CliGoalParkTestSupport
{
    [Xunit.Fact]
    public async Task ConcurrentWriter_CannotBeOverwrittenByCancel()
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
                ["cancel-goal", seed.GoalId.Value[..8], "Operator cancel", "--confirm-goal-stop"],
                probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.True(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["cancel-goal"]));
            Xunit.Assert.False(CliPersistentStateRunner.IsGoalLifecycleDispositionCommand(["supersede-goal"]));
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
