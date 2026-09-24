using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalParkConcurrentWriterTests : CliGoalParkTestSupport
{
    [Xunit.Fact]
    public async Task ConcurrentWriter_CannotBeOverwrittenByPark()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                StateAfterApplication = (attempt, state, token) => writer.SaveGoalSnapshotsAsync(
                    [state.Goal with { Objective = $"Concurrent writer {attempt}" }], token)
            };

            var result = RunCommand(
                ["park-goal", seed.GoalId.Value[..8], "Operator park", "--confirm-goal-park"],
                probe, seed.Workspace);

            var stored = await seed.Repository.LoadGoalAsync(seed.GoalId);
            Xunit.Assert.Equal("cli:park-goal", probe.StateOperationName);
            Xunit.Assert.True(probe.StateApplicationCount > 0);
            Xunit.Assert.Equal(0, probe.WholeKernelLoadCount);
            Xunit.Assert.Equal(0, probe.WholeKernelSaveCount);
            Xunit.Assert.Equal($"Concurrent writer {probe.StateApplicationCount}", stored!.Objective);
            if (result.Error is null)
            {
                Xunit.Assert.True(result.Changed);
                Xunit.Assert.Equal(GoalStatus.Parked, stored.Status);
                Xunit.Assert.Contains(stored.Timeline, item => item.Message == "Goal parked: Operator park");
            }
            else
            {
                Xunit.Assert.Contains("concurrent updates exhausted the retry budget", result.Error.Message);
                Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
                Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Message == "Goal parked: Operator park");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
