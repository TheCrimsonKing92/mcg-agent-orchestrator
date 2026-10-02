using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Each test owns its journal directory; no shared store or process state is mutated.
public sealed class RunEventMaintenanceCadenceRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-cadence-runner-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_root, "conduct-events.log");

    [Xunit.Fact]
    public async Task BlockedCadenceReturnsFromTickStaysSingleFlightAndCanRunAgain()
    {
        var entered = NewSignal();
        var enteredAgain = NewSignal();
        var release = NewSignal();
        var calls = 0;
        var runner = new RunEventMaintenanceCadenceRunner(LogPath, () =>
        {
            if (Interlocked.Increment(ref calls) == 1) entered.TrySetResult();
            else enteredAgain.TrySetResult();
            TestHangGuard.WaitAsync(release.Task, "cadence release").GetAwaiter().GetResult();
        });
        try
        {
            Assert.True(await TickAsync(runner));
            await TestHangGuard.WaitAsync(entered.Task, "first cadence entry");
            var firstRun = runner.CurrentRun!;
            Assert.False(firstRun.IsCompleted);
            Assert.False(release.Task.IsCompleted);
            Assert.False(await TickAsync(runner));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Same(firstRun, runner.CurrentRun);

            release.TrySetResult();
            await TestHangGuard.WaitAsync(firstRun, "first cadence completion");
            Assert.True(await TickAsync(runner));
            await TestHangGuard.WaitAsync(enteredAgain.Task, "second cadence entry");
            await TestHangGuard.WaitAsync(runner.CurrentRun!, "second cadence completion");
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            release.TrySetResult();
            if (runner.CurrentRun is { } run)
                await TestHangGuard.WaitAsync(run, "cadence cleanup");
        }
    }

    [Xunit.Fact]
    public async Task ThrownCadenceIsJournaledOnceAndTheNextTickCanRetry()
    {
        var calls = 0;
        var retryEntered = NewSignal();
        var releaseRetry = NewSignal();
        var runner = new RunEventMaintenanceCadenceRunner(LogPath, () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("injected cadence failure");
            retryEntered.TrySetResult();
            TestHangGuard.WaitAsync(releaseRetry.Task, "retry cadence release").GetAwaiter().GetResult();
        });
        try
        {
            Assert.True(await TickAsync(runner));
            await TestHangGuard.WaitAsync(runner.CurrentRun!, "throwing cadence completion");
            Assert.True(runner.CurrentRun!.IsCompletedSuccessfully);
            var failed = Assert.Single(ReadEvents("run-events-maintenance-failed"));
            Assert.Contains("exception=InvalidOperationException", failed.GetProperty("detail").GetString());

            // A normal return also proves observing the finished run did not throw into the tick.
            Assert.True(await TickAsync(runner));
            await TestHangGuard.WaitAsync(retryEntered.Task, "retry cadence entry");
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Single(ReadEvents("run-events-maintenance-failed"));
        }
        finally
        {
            releaseRetry.TrySetResult();
            if (runner.CurrentRun is { } run)
                await TestHangGuard.WaitAsync(run, "retry cadence cleanup");
        }
    }

    [Xunit.Fact]
    public async Task StartEventPrecedesOperationAndInFlightTickAddsNone()
    {
        var entered = NewSignal();
        var release = NewSignal();
        JsonElement[]? eventsAtEntry = null;
        var runner = new RunEventMaintenanceCadenceRunner(LogPath, () =>
        {
            eventsAtEntry = ReadEvents("storage-retention-sweep-started");
            entered.TrySetResult();
            TestHangGuard.WaitAsync(release.Task, "journal test cadence release").GetAwaiter().GetResult();
        });
        try
        {
            Assert.True(await TickAsync(runner));
            await TestHangGuard.WaitAsync(entered.Task, "journal test cadence entry");
            Assert.Single(eventsAtEntry!);
            Assert.False(runner.CurrentRun!.IsCompleted);
            Assert.False(await TickAsync(runner));
            Assert.Single(ReadEvents("storage-retention-sweep-started"));
        }
        finally
        {
            release.TrySetResult();
            if (runner.CurrentRun is { } run)
                await TestHangGuard.WaitAsync(run, "journal test cadence cleanup");
        }
        Assert.Single(ReadEvents("storage-retention-sweep-started"));
    }

    [Xunit.Fact]
    public async Task FreshGateSkipsLaunchAndJournalUntilDue()
    {
        var due = false;
        var calls = 0;
        var runner = new RunEventMaintenanceCadenceRunner(LogPath,
            () => Interlocked.Increment(ref calls), () => due);
        Assert.False(await TickAsync(runner));
        Assert.Null(runner.CurrentRun);
        Assert.Equal(0, calls);
        Assert.False(File.Exists(LogPath));

        due = true;
        Assert.True(await TickAsync(runner));
        await TestHangGuard.WaitAsync(runner.CurrentRun!, "due cadence completion");
        due = false;
        Assert.False(await TickAsync(runner));
        Assert.Equal(1, calls);
        Assert.Single(ReadEvents("storage-retention-sweep-started"));
    }

    [Xunit.Fact]
    public async Task CommandExitWaitJoinsBlockedCadenceUntilReleased()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var waitReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? waitTask = null;
        Task? invokeWait = null;
        var runner = new RunEventMaintenanceCadenceRunner(LogPath, () =>
        {
            entered.TrySetResult();
            TestHangGuard.WaitAsync(release.Task, "command exit cadence release").GetAwaiter().GetResult();
        });
        try
        {
            Assert.True(await TickAsync(runner));
            await TestHangGuard.WaitAsync(entered.Task, "command exit cadence entry");
            invokeWait = Task.Run(() =>
            {
                // Publish the actual join task after invoking the method, so a no-op join
                // fails deterministically rather than depending on thread-pool scheduling.
                waitReturned.TrySetResult(runner.WaitForCurrentRunAsync());
            });
            waitTask = await TestHangGuard.WaitAsync(waitReturned.Task, "command exit wait task returned");
            Assert.False(release.Task.IsCompleted);
            Assert.False(runner.CurrentRun!.IsCompleted);
            Assert.False(waitTask.IsCompleted);

            release.TrySetResult();
            await TestHangGuard.WaitAsync(waitTask, "command exit wait completion");
            Assert.True(runner.CurrentRun!.IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            if (runner.CurrentRun is { } run)
                await TestHangGuard.WaitAsync(run, "command exit cadence cleanup");
            if (waitTask is not null)
                await TestHangGuard.WaitAsync(waitTask, "command exit wait cleanup");
            if (invokeWait is not null)
                await TestHangGuard.WaitAsync(invokeWait, "command exit wait invocation cleanup");
        }
    }

    private JsonElement[] ReadEvents(string kind) => File.ReadAllLines(LogPath)
        .Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        })
        .Where(evt => evt.GetProperty("eventKind").GetString() == kind).ToArray();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<bool> TickAsync(RunEventMaintenanceCadenceRunner runner) =>
        TestHangGuard.WaitAsync(Task.Run(runner.OnTick), "per-tick cadence entry returned");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
