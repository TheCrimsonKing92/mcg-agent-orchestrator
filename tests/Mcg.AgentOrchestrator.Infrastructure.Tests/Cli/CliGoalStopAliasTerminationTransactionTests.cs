using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalStopAliasTerminationTransactionTests : CliGoalStopAliasTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("park", false)]
    [Xunit.InlineData("abandon", false)]
    [Xunit.InlineData("park", true)]
    [Xunit.InlineData("abandon", true)]
    public async Task CommitFault_ReportsTerminatedWorkersAndLeavesStateUntouched(string mode, bool cancel)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 2);
            var store = await SeedAttention(seed);
            var version = await Version(seed);
            var calls = new List<int>();
            using var seam = RecordTerminations(calls);
            Exception fault = cancel ? new OperationCanceledException("injected cancellation") : new IOException("injected commit failure");
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                StateAfterApplication = (_, _, _) => throw fault,
                AfterApplication = (_, _, _) => throw fault
            };

            var result = RunCommand(StopParts(seed, mode), probe, seed.Workspace);

            var error = Xunit.Assert.IsType<InvalidOperationException>(result.Error);
            Xunit.Assert.Same(fault, error.InnerException);
            Xunit.Assert.Contains($"stop --as {mode} did not commit", error.Message);
            for (var index = 0; index < 2; index++)
            {
                Xunit.Assert.Contains(seed.TaskIds[index].Value[..8], error.Message);
                Xunit.Assert.Contains((900001 + index).ToString(), error.Message);
            }
            Xunit.Assert.Equal([900001, 900002], calls);
            Xunit.Assert.Equal($"cli:{mode}-goal", mode == "park" ? probe.StateOperationName : probe.OperationName);
            Xunit.Assert.Equal(0, probe.WholeKernelLoadCount);
            Xunit.Assert.Equal(0, probe.WholeKernelSaveCount);
            var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
            Xunit.Assert.Equal(GoalStatus.Active, stored.Status);
            Xunit.Assert.Equal(version, await Version(seed));
            Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Message == "Goal parked: Operator stop");
            Xunit.Assert.DoesNotContain(stored.Timeline, item => item.Kind == ProgressKind.GoalCancelled);
            Xunit.Assert.All(stored.Tasks, task =>
            {
                Xunit.Assert.False(task.LastProcess!.WasCancelled);
                Xunit.Assert.Null(task.LastProcess.CompletedAt);
            });
            var attention = Xunit.Assert.Single(await store.ListAsync(seed.GoalId.Value));
            Xunit.Assert.Equal(CollaborationItemStatus.Raised, attention.Status);
            Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, seed.GoalId)));
            Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, seed.GoalId)));
            Xunit.Assert.Equal(string.Empty, result.Output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("park")]
    [Xunit.InlineData("abandon")]
    public async Task CompletedGoal_RejectsBeforeTerminating(string mode)
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root, 2);
            var snapshot = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
            await seed.Repository.SaveGoalSnapshotsAsync([snapshot with { Status = GoalStatus.Completed }]);
            var version = await Version(seed);
            var calls = new List<int>();
            using var seam = RecordTerminations(calls);
            var probe = new GoalTransactionProbeRepository(seed.Repository);

            var result = RunCommand(StopParts(seed, mode), probe, seed.Workspace);

            var error = Xunit.Assert.IsType<InvalidOperationException>(result.Error);
            Xunit.Assert.Contains(mode == "abandon"
                ? "abandon-goal could not apply because one or more steps are blocked."
                : "Completed and cannot be parked.", error.Message);
            Xunit.Assert.Empty(calls);
            Xunit.Assert.Equal(0, probe.ApplicationCount + probe.StateApplicationCount);
            Xunit.Assert.Equal(version, await Version(seed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
