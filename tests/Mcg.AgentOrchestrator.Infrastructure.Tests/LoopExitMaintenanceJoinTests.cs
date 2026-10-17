using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: task signals and a unique temporary journal only.
public sealed class LoopExitMaintenanceJoinTests
{
    [Fact]
    public async Task Deadline_BlockedWait_ReportsOnlyUnfinishedRunner()
    {
        var blocked = NewSignal();
        var reports = new List<string>();
        var calls = new int[3];
        var deadlines = 0;
        await TestHangGuard.WaitAsync(LoopExitMaintenanceJoin.JoinAsync(
            [("first", () => { calls[0]++; return Task.CompletedTask; }),
             ("blocked", () => { calls[1]++; return blocked.Task; }),
             ("last", () => { calls[2]++; return Task.CompletedTask; })],
            LoopExitMaintenanceJoin.Grace, grace =>
            {
                deadlines++;
                Assert.Equal(TimeSpan.FromSeconds(30), grace);
                return Task.CompletedTask;
            }, reports.Add), "bounded exit join");

        Assert.Equal([1, 1, 1], calls);
        Assert.Equal(1, deadlines);
        Assert.Equal("LOOP_EXIT_MAINTENANCE_STILL_RUNNING runner=blocked graceSeconds=30",
            Assert.Single(reports));
    }

    [Fact]
    public async Task AllWaits_ReleasedAfterJoin_ReturnsWithoutDeadlineOrReports()
    {
        var waits = new[] { NewSignal(), NewSignal(), NewSignal() };
        var deadline = NewSignal();
        var reports = new List<string>();
        var join = LoopExitMaintenanceJoin.JoinAsync(
            waits.Select((signal, i) => ($"runner-{i}", (Func<Task>)(() => signal.Task))).ToArray(),
            LoopExitMaintenanceJoin.Grace, _ => deadline.Task, reports.Add);

        // Only this test can release the waits or deadline, so this is event-gated.
        Assert.False(join.IsCompleted);
        foreach (var wait in waits) wait.SetResult();
        await TestHangGuard.WaitAsync(join, "all exit waits released");
        Assert.Empty(reports);
    }

    [Fact]
    public async Task WaitFaults_AsyncAndSynchronous_AreFinishedWithoutReports()
    {
        var fault = NewSignal();
        var reports = new List<string>();
        var calls = 0;
        var join = LoopExitMaintenanceJoin.JoinAsync(
            [("async-fault", () => fault.Task),
             ("sync-fault", () => throw new InvalidOperationException("sync")),
             ("finished", () => { calls++; return Task.CompletedTask; })],
            LoopExitMaintenanceJoin.Grace, _ => NewSignal().Task, reports.Add);
        Assert.False(join.IsCompleted);
        fault.SetException(new InvalidOperationException("async"));
        await TestHangGuard.WaitAsync(join, "faulted exit wait observed");
        Assert.Equal(1, calls);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task RealCadence_BlockedOperation_JoinAbandonsBackgroundRun()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-exit-join-{Guid.NewGuid():N}");
        var entered = NewSignal();
        var release = NewSignal();
        var runner = new RunEventMaintenanceCadenceRunner(Path.Combine(root, "conduct.log"), () =>
        {
            entered.SetResult();
            TestHangGuard.WaitAsync(release.Task, "cadence release").GetAwaiter().GetResult();
        });
        try
        {
            Assert.True(runner.OnTick());
            await TestHangGuard.WaitAsync(entered.Task, "cadence entry");
            var reports = new List<string>();
            await TestHangGuard.WaitAsync(LoopExitMaintenanceJoin.JoinAsync(
                [("maintenance-cadence", () => runner.WaitForCurrentRunAsync())],
                LoopExitMaintenanceJoin.Grace, _ => Task.CompletedTask, reports.Add), "abandoned cadence join");
            Assert.False(runner.CurrentRun!.IsCompleted);
            Assert.Equal("LOOP_EXIT_MAINTENANCE_STILL_RUNNING runner=maintenance-cadence graceSeconds=30",
                Assert.Single(reports));
        }
        finally
        {
            release.TrySetResult();
            if (runner.CurrentRun is { } run)
                await TestHangGuard.WaitAsync(run, "cadence cleanup");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
