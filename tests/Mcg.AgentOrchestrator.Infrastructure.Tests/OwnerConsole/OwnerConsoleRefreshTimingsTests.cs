using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its timestamp clock and collector.
public sealed class OwnerConsoleRefreshTimingsTests
{
    [Fact]
    public async Task Detail_MultipleLoaders_ReportsCumulativeTimeInFixedOrder()
    {
        var clock = new TimestampClock();
        var timings = new OwnerConsoleRefreshTimings(clock);
        for (var index = 0; index < 2; index++)
            Assert.Equal(5, timings.Time("gate-progress", () => { clock.Advance(5); return 5; }));
        Assert.Equal(2, await timings.TimeAsync("latest-tick", () =>
        { clock.Advance(2); return Task.FromResult(2); }));

        Assert.Equal("board=0/0ms gate-progress=2/10ms gate-motion=0/0ms latest-tick=1/2ms decisions=0/0ms lifecycle-tail=0/0ms landed-today=0/0ms activity=0/0ms render=0/0ms",
            timings.Detail());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Time_ThrowingAction_RecordsElapsedAndRethrowsSameException(bool asynchronous)
    {
        var clock = new TimestampClock();
        var timings = new OwnerConsoleRefreshTimings(clock);
        var failure = new InvalidOperationException("loader failed");
        int Fail() { clock.Advance(7); throw failure; }

        var observed = asynchronous
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => timings.TimeAsync("board", () =>
                { clock.Advance(7); return Task.FromException<int>(failure); }))
            : Assert.Throws<InvalidOperationException>(() => timings.Time("board", Fail));

        Assert.Same(failure, observed);
        Assert.Equal("board=1/7ms gate-progress=0/0ms gate-motion=0/0ms latest-tick=0/0ms decisions=0/0ms lifecycle-tail=0/0ms landed-today=0/0ms activity=0/0ms render=0/0ms",
            timings.Detail());
    }

    [Fact]
    public async Task TimeAsync_OverlappingCalls_AccumulatesBothDurations()
    {
        var clock = new TimestampClock();
        var timings = new OwnerConsoleRefreshTimings(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = timings.TimeAsync("board", async () => { await release.Task; return 1; });
        var second = timings.TimeAsync("board", async () => { await release.Task; return 2; });
        clock.Advance(5);
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard: both timed actions completed.
        Assert.StartsWith("board=2/10ms ", timings.Detail());
    }

    private sealed class TimestampClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(long milliseconds) => Interlocked.Add(ref _ticks, milliseconds);
    }
}
