using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all time and child progress are values; no process or shared state is used.
public sealed class AcceptanceLaneStallPolicyTests
{
    [Fact]
    public void Limit_preserves_measured_healthy_silence_headroom()
    {
        Assert.True(AcceptanceLaneStallPolicy.StallLimitSeconds == 600,
            "Measured basis: longest healthy silence 366 s in 1,083 lane/check invocations.");
    }

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, true)]
    [InlineData(601, true)]
    [InlineData(-30, false)]
    public void Policy_trips_at_limit_and_rejects_backwards_time(int silentSeconds, bool expected)
    {
        var time = new ManualTimeProvider();
        var lastProgress = time.GetUtcNow();
        time.Advance(silentSeconds);
        Assert.Equal(expected, AcceptanceLaneStallPolicy.IsStalled(
            lastProgress, time.GetUtcNow(), AcceptanceLaneStallPolicy.StallLimit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1234)]
    public void Unchanged_output_stops_child_once_with_stall_reason(long outputBytes)
    {
        var time = new ManualTimeProvider();
        var lines = new List<string>();
        using var monitor = CreateMonitor(time, lines.Add);
        using var clockTimeout = new CancellationTokenSource();
        using var childWait = CancellationTokenSource.CreateLinkedTokenSource(clockTimeout.Token, monitor.Token);
        var childStops = 0;
        using var registration = childWait.Token.Register(() => childStops++);
        monitor.Observe(Snapshot(outputBytes));

        time.Advance(599);
        monitor.Observe(Snapshot(outputBytes));
        Assert.False(childWait.IsCancellationRequested);
        time.Advance(1);
        monitor.Observe(Snapshot(outputBytes));

        Assert.True(monitor.Stopped);
        Assert.True(childWait.IsCancellationRequested);
        Assert.False(clockTimeout.IsCancellationRequested);
        Assert.Equal(AcceptanceLaneStallPolicy.LaneStalledReason, monitor.StopReason);
        Assert.Equal(AcceptanceLaneStallPolicy.StallLimit, monitor.Silent);
        Assert.Equal(1, childStops);
        Assert.Equal("LANE_STALLED lane=\"fake lane\" silent_seconds=600 limit_seconds=600", Assert.Single(lines));

        for (var tick = 0; tick < 20; tick++)
        {
            time.Advance(5);
            monitor.Observe(Snapshot(outputBytes));
        }
        Assert.Equal(1, childStops);
        Assert.Single(lines);
    }

    [Fact]
    public void Output_changes_keep_a_26_minute_child_alive()
    {
        var time = new ManualTimeProvider();
        var lines = new List<string>();
        using var monitor = CreateMonitor(time, lines.Add);
        monitor.Observe(Snapshot(0));
        long outputBytes = 0;
        for (var seconds = 5; seconds <= 26 * 60; seconds += 5)
        {
            time.Advance(5);
            if (seconds % (9 * 60) == 0)
                outputBytes++;
            monitor.Observe(Snapshot(outputBytes));
        }

        Assert.False(monitor.Stopped);
        Assert.False(monitor.Token.IsCancellationRequested);
        Assert.Null(monitor.StopReason);
        Assert.Empty(lines);
    }

    [Theory]
    [InlineData(366)]
    [InlineData(599)]
    public void Quiet_window_followed_by_output_resets_silence(int quietSeconds)
    {
        var time = new ManualTimeProvider();
        using var monitor = CreateMonitor(time);
        monitor.Observe(Snapshot(0));
        time.Advance(quietSeconds);
        monitor.Observe(Snapshot(0));
        Assert.False(monitor.Stopped);
        monitor.Observe(Snapshot(1));

        time.Advance(599);
        monitor.Observe(Snapshot(1));
        Assert.False(monitor.Token.IsCancellationRequested);
        time.Advance(1);
        monitor.Observe(Snapshot(1));
        Assert.True(monitor.Stopped);
        Assert.Equal(AcceptanceLaneStallPolicy.StallLimit, monitor.Silent);
    }

    [Fact]
    public void Completed_child_is_untouched_by_later_heartbeat_ticks()
    {
        var time = new ManualTimeProvider();
        var lines = new List<string>();
        using var monitor = CreateMonitor(time, lines.Add);
        monitor.Observe(Snapshot(0));
        time.Advance(599);
        monitor.Observe(Snapshot(0));
        monitor.Complete();
        time.Advance(AcceptanceLaneStallPolicy.StallLimitSeconds);
        monitor.Observe(Snapshot(0));

        Assert.False(monitor.Stopped);
        Assert.False(monitor.Token.IsCancellationRequested);
        Assert.Empty(lines);
    }

    [Fact]
    public void Factory_requires_a_verification_heartbeat_context()
    {
        var time = new ManualTimeProvider();
        Assert.Null(AcceptanceLaneStallMonitor.ForHeartbeat(null, time));
        var context = new GateHeartbeatContext(null, "discovery", "fake lane", null, "unused", null, "fake", null);
        Assert.Null(AcceptanceLaneStallMonitor.ForHeartbeat(context, time));
        using var monitor = AcceptanceLaneStallMonitor.ForHeartbeat(context with { Phase = "verification-check" }, time);
        Assert.NotNull(monitor);
        Assert.Equal(AcceptanceLaneStallPolicy.StallLimit, monitor.Limit);
    }

    [Fact]
    public void Diagnostic_failure_does_not_prevent_stop()
    {
        var time = new ManualTimeProvider();
        using var monitor = CreateMonitor(time, _ => throw new IOException("fake log failure"));
        monitor.Observe(Snapshot(0));
        time.Advance(AcceptanceLaneStallPolicy.StallLimitSeconds);
        monitor.Observe(Snapshot(0));
        Assert.True(monitor.Token.IsCancellationRequested);
    }

    private static AcceptanceLaneStallMonitor CreateMonitor(ManualTimeProvider time, Action<string>? emit = null) =>
        new("fake lane", time, AcceptanceLaneStallPolicy.StallLimit, emit ?? (_ => { }));

    // Deliberately unrelated timestamps: only bytes and the injected clock govern the monitor.
    private static GateHeartbeatSnapshot Snapshot(long bytes) =>
        new(null, "verification-check", "fake lane", null, null, null, "running",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, bytes, 0, bytes);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
}
