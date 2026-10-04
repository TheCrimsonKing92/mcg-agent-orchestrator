using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalStopAliasTerminationConcurrentWriterTests : CliGoalStopAliasTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("park", false)]
    [Xunit.InlineData("abandon", false)]
    [Xunit.InlineData("park", true)]
    [Xunit.InlineData("abandon", true)]
    public async Task ConcurrentWriter_RetriesWithoutRepeatingTermination(string mode, bool exhaust)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 2);
            var store = await SeedAttention(seed);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            Task Write(int attempt, GoalSnapshot goal, CancellationToken token) =>
                attempt == 1 || exhaust
                    ? writer.SaveGoalSnapshotsAsync([goal with { Objective = $"Concurrent writer {attempt}" }], token)
                    : Task.CompletedTask;
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                StateAfterApplication = (attempt, state, token) => Write(attempt, state.Goal, token),
                AfterApplication = Write
            };
            var calls = new List<int>();
            using var seam = RecordTerminations(calls);

            var result = RunCommand(StopParts(seed, mode), probe, seed.Workspace);

            var count = mode == "park" ? probe.StateApplicationCount : probe.ApplicationCount;
            Xunit.Assert.True(count >= 2);
            Xunit.Assert.Equal([900001, 900002], calls);
            var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
            Xunit.Assert.Equal($"Concurrent writer {(exhaust ? count : 1)}", stored.Objective);
            if (exhaust)
            {
                var error = Xunit.Assert.IsType<InvalidOperationException>(result.Error);
                Xunit.Assert.Contains($"stop --as {mode} did not commit", error.Message);
                foreach (var id in seed.TaskIds) Xunit.Assert.Contains(id.Value[..8], error.Message);
                foreach (var pid in calls) Xunit.Assert.Contains(pid.ToString(), error.Message);
                Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
                Xunit.Assert.All(stored.Tasks, task => Xunit.Assert.False(task.LastProcess!.WasCancelled));
                Xunit.Assert.Equal(CollaborationItemStatus.Raised, Xunit.Assert.Single(await store.ListAsync(seed.GoalId.Value)).Status);
            }
            else
            {
                Xunit.Assert.Null(result.Error);
                Xunit.Assert.True(result.Changed);
                Xunit.Assert.Equal(mode == "park" ? GoalStatus.Parked : GoalStatus.Cancelled, stored.Status);
                Xunit.Assert.All(stored.Tasks, task => Xunit.Assert.True(task.LastProcess!.WasCancelled));
                Xunit.Assert.Equal(2, stored.Timeline.Count(item => item.Kind == ProgressKind.TaskCancelled));
                Xunit.Assert.Equal(2, stored.Timeline.Count(item => item.Message == "RESOURCE recording seam"));
                Xunit.Assert.Equal(2, stored.Timeline.Count(item => item.Message.StartsWith("CANCELLATION_CANDIDATE_EVIDENCE")));
                Xunit.Assert.Equal(mode == "park" ? CollaborationItemStatus.Resolved : CollaborationItemStatus.Raised,
                    Xunit.Assert.Single(await store.ListAsync(seed.GoalId.Value)).Status);
            }
            Xunit.Assert.Equal(0, probe.WholeKernelLoadCount + probe.WholeKernelSaveCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
