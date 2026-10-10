using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every clock, delay, body and emitted line belongs to the test.
public sealed class ConductorStartupHeartbeatTests
{
    [Xunit.Fact]
    public async Task SlowPhase_ScriptedIntervals_EmitInjectedElapsedMilliseconds()
    {
        var script = new Script();
        var finishBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(() => script.Heartbeat.Run(ConductorStartupHeartbeat.Phases.TerminalSweep,
            () => finishBody.Task.GetAwaiter().GetResult()));
        try
        {
            Assert.Equal("STARTUP_PROGRESS phase=terminal-sweep elapsed_ms=0", await script.NextLine());
            for (var tick = 1; tick <= 3; tick++)
            {
                var delay = await script.NextDelay();
                script.SetElapsed(TimeSpan.FromSeconds(15 * tick));
                delay.TrySetResult();
                Assert.Equal($"STARTUP_PROGRESS phase=terminal-sweep elapsed_ms={15000 * tick}", await script.NextLine());
            }
            var pending = await script.NextDelay();
            finishBody.TrySetResult();
            await WaitFor(run, "slow phase return");
            pending.TrySetResult();
            Assert.Equal(4, script.Lines.Count);
            Assert.Equal(new long[] { 0, 15000, 30000, 45000 }, script.Lines.Select(line =>
                long.Parse(line[(line.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            finishBody.TrySetResult();
            await WaitFor(run, "slow phase cleanup");
        }
    }

    [Xunit.Fact]
    public async Task DisposedPhase_PendingDelayCompletes_NoFurtherEmission()
    {
        var script = new Script(ignoreCancellation: true);
        using var phase = script.Heartbeat.Begin(ConductorStartupHeartbeat.Phases.TerminalSweep);
        Assert.Equal("STARTUP_PROGRESS phase=terminal-sweep elapsed_ms=0", await script.NextLine());
        var pending = await script.NextDelay();
        phase.Dispose();
        script.SetElapsed(TimeSpan.FromSeconds(15));
        pending.TrySetResult(); // This delay deliberately ignores cancellation.
        await WaitFor(phase.Completion, "stopped ticker completion");
        Assert.Single(script.Lines);
        phase.Dispose(); // Idempotent even after ticker resource cleanup.
    }

    [Xunit.Fact]
    public void FastPhase_NoReleasedDelay_EmitsOnlyStartAndReturnsValue()
    {
        var script = new Script();
        Assert.Equal(42, script.Heartbeat.Run(ConductorStartupHeartbeat.Phases.TerminalSweep, () => 42));
        Assert.Equal(new[] { "STARTUP_PROGRESS phase=terminal-sweep elapsed_ms=0" }, script.Lines);
    }

    [Xunit.Fact]
    public void ThrowingPhase_EmitsStart_RethrowsSameException()
    {
        var script = new Script();
        var failure = new InvalidOperationException("phase failure");
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            script.Heartbeat.Run(ConductorStartupHeartbeat.Phases.TerminalSweep, () => { throw failure; }));
        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "STARTUP_PROGRESS phase=terminal-sweep elapsed_ms=0" }, script.Lines);
    }

    [Xunit.Fact]
    public void Constructor_IntervalBounds_RejectUnsafeCadence()
    {
        var script = new Script();
        Assert.Equal(TimeSpan.FromSeconds(15), script.Heartbeat.Interval);
        Assert.True(script.Heartbeat.Interval <= TimeSpan.FromMinutes(2) / 2);
        Assert.Equal(TimeSpan.FromMinutes(2) / 2, ConductorStartupHeartbeat.MaxInterval);
        foreach (var interval in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(60.001) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConductorStartupHeartbeat(
                _ => { }, () => DateTimeOffset.UnixEpoch, (_, _) => Task.CompletedTask, interval));
        Assert.Equal(ConductorStartupHeartbeat.MaxInterval, new ConductorStartupHeartbeat(
            _ => { }, () => DateTimeOffset.UnixEpoch, (_, _) => Task.CompletedTask,
            ConductorStartupHeartbeat.MaxInterval).Interval);
    }

    [Xunit.Fact]
    public async Task DelayFault_PhaseContinues_BodyOutcomeIsPreserved()
    {
        var lines = new ConcurrentQueue<string>();
        var heartbeat = new ConductorStartupHeartbeat(lines.Enqueue, () => DateTimeOffset.UnixEpoch,
            (_, _) => Task.FromException(new InvalidOperationException("ticker failure")));
        using var phase = heartbeat.Begin(ConductorStartupHeartbeat.Phases.TerminalSweep);
        await WaitFor(phase.Completion, "faulted delay observation");
        Assert.Single(lines);
        phase.Dispose();
    }

    private static async Task WaitFor(Task task, string eventName)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException exception) { throw new TimeoutException($"Timed out waiting for {eventName}", exception); }
    }

    private sealed class Script
    {
        private readonly Channel<TaskCompletionSource> _delays = Channel.CreateUnbounded<TaskCompletionSource>();
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private long _elapsedTicks;
        internal ConcurrentQueue<string> Lines { get; } = new();
        internal ConductorStartupHeartbeat Heartbeat { get; }

        internal Script(bool ignoreCancellation = false)
        {
            Heartbeat = new(line => { Lines.Enqueue(line); Assert.True(_lines.Writer.TryWrite(line)); },
                () => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref _elapsedTicks)),
                (interval, cancellationToken) =>
                {
                    Assert.Equal(TimeSpan.FromSeconds(15), interval);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Assert.True(_delays.Writer.TryWrite(release));
                    return ignoreCancellation ? release.Task : release.Task.WaitAsync(cancellationToken);
                });
        }

        internal void SetElapsed(TimeSpan elapsed) => Interlocked.Exchange(ref _elapsedTicks, elapsed.Ticks);
        internal async Task<TaskCompletionSource> NextDelay()
        {
            var next = _delays.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            await WaitFor(next, "heartbeat delay request");
            return await next;
        }
        internal async Task<string> NextLine()
        {
            var next = _lines.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            await WaitFor(next, "heartbeat emitted line");
            return await next;
        }
    }
}
