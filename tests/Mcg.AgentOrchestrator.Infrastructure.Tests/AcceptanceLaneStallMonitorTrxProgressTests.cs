using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns a unique temp directory and an injected clock.
public sealed class AcceptanceLaneStallMonitorTrxProgressTests
{
    private const string StallLine = "LANE_STALLED lane=\"fake lane\" silent_seconds=600 limit_seconds=600";
    private static readonly DateTime FileTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void File_growth_keeps_a_quiet_26_minute_lane_alive()
    {
        InTempDirectory(directory =>
        {
            var path = CreateStream(directory);
            var time = new ManualTimeProvider();
            var lines = new List<string>();
            using var monitor = CreateMonitor(time, lines, new TrxStreamProgressProbe(directory));
            monitor.Observe(Snapshot());
            Assert.False(monitor.Stopped);
            Assert.Empty(lines);

            for (var seconds = 5; seconds <= 26 * 60; seconds += 5)
            {
                time.Advance(5);
                if (seconds % (5 * 60) == 0)
                {
                    File.AppendAllText(path, "progress");
                    File.SetLastWriteTimeUtc(path, FileTime.AddSeconds(seconds));
                }
                monitor.Observe(Snapshot());
                Assert.False(monitor.Stopped);
                Assert.False(monitor.Token.IsCancellationRequested);
                Assert.Empty(lines);
            }
        });
    }

    [Fact]
    public void Last_write_only_change_resets_the_silence_window()
    {
        InTempDirectory(directory =>
        {
            var path = CreateStream(directory);
            var time = new ManualTimeProvider();
            var lines = new List<string>();
            using var monitor = CreateMonitor(time, lines, new TrxStreamProgressProbe(directory));
            monitor.Observe(Snapshot());
            time.Advance(300);
            File.SetLastWriteTimeUtc(path, FileTime.AddMinutes(5));
            Assert.Equal(1L, new FileInfo(path).Length);
            monitor.Observe(Snapshot());

            time.Advance(599);
            monitor.Observe(Snapshot());
            Assert.False(monitor.Stopped);
            Assert.Empty(lines);
            time.Advance(1);
            monitor.Observe(Snapshot());
            AssertStalled(monitor, lines);
        });
    }

    [Fact]
    public void An_unchanged_stream_stops_at_600_seconds_once()
    {
        InTempDirectory(directory =>
        {
            CreateStream(directory);
            var time = new ManualTimeProvider();
            var lines = new List<string>();
            using var monitor = CreateMonitor(time, lines, new TrxStreamProgressProbe(directory));
            var stops = 0;
            using var registration = monitor.Token.Register(() => stops++);
            monitor.Observe(Snapshot());
            time.Advance(599);
            monitor.Observe(Snapshot());
            Assert.False(monitor.Stopped);
            Assert.Empty(lines);
            time.Advance(1);
            monitor.Observe(Snapshot());
            AssertStalled(monitor, lines);
            Assert.Equal(1, stops);

            time.Advance(600);
            monitor.Observe(Snapshot());
            AssertStalled(monitor, lines);
            Assert.Equal(1, stops);
        });
    }

    [Fact]
    public void One_growth_at_300_seconds_moves_the_stop_to_900_seconds()
    {
        InTempDirectory(directory =>
        {
            var path = CreateStream(directory);
            var time = new ManualTimeProvider();
            var lines = new List<string>();
            using var monitor = CreateMonitor(time, lines, new TrxStreamProgressProbe(directory));
            monitor.Observe(Snapshot());
            time.Advance(300);
            File.AppendAllText(path, "progress");
            File.SetLastWriteTimeUtc(path, FileTime);
            monitor.Observe(Snapshot());

            time.Advance(300);
            monitor.Observe(Snapshot());
            Assert.False(monitor.Stopped);
            time.Advance(299);
            monitor.Observe(Snapshot());
            Assert.False(monitor.Stopped);
            Assert.Empty(lines);
            time.Advance(1);
            monitor.Observe(Snapshot());
            AssertStalled(monitor, lines);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_absent_progress_signal_preserves_the_600_second_stop(bool hasDirectory)
    {
        InTempDirectory(directory =>
        {
            File.WriteAllBytes(Path.Combine(directory, "other.bin"), [1]);
            File.SetLastWriteTimeUtc(Path.Combine(directory, "other.bin"), FileTime);
            var probe = AcceptanceLaneResultsDirectory.ProbeFor(hasDirectory
                ? ["dotnet", "test", "--results-directory", directory]
                : ["dotnet", "test"]);
            if (hasDirectory)
                Assert.Null(Assert.IsType<TrxStreamProgressProbe>(probe).Sample());
            else
                Assert.Null(probe);

            var time = new ManualTimeProvider();
            var lines = new List<string>();
            using var monitor = CreateMonitor(time, lines, probe);
            monitor.Observe(Snapshot());
            time.Advance(599);
            monitor.Observe(Snapshot());
            Assert.False(monitor.Stopped);
            Assert.Empty(lines);
            time.Advance(1);
            monitor.Observe(Snapshot());
            AssertStalled(monitor, lines);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_probe_is_not_sampled_after_stop_or_completion(bool complete)
    {
        InTempDirectory(directory =>
        {
            CreateStream(directory);
            var time = new ManualTimeProvider();
            var lines = new List<string>();
            var probe = new CountingProbe(new TrxStreamProgressProbe(directory));
            using var monitor = CreateMonitor(time, lines, probe);
            monitor.Observe(Snapshot());
            Assert.Equal(1, probe.Samples);
            if (complete)
                monitor.Complete();
            else
            {
                time.Advance(600);
                monitor.Observe(Snapshot());
                AssertStalled(monitor, lines);
                Assert.Equal(2, probe.Samples);
            }

            var samples = probe.Samples;
            time.Advance(600);
            monitor.Observe(Snapshot());
            Assert.Equal(samples, probe.Samples);
            if (complete)
            {
                Assert.False(monitor.Stopped);
                Assert.False(monitor.Token.IsCancellationRequested);
                Assert.Empty(lines);
            }
        });
    }

    [Fact]
    public void Later_output_changes_do_not_sample_the_probe()
    {
        InTempDirectory(directory =>
        {
            CreateStream(directory);
            var time = new ManualTimeProvider();
            var lines = new List<string>();
            var probe = new CountingProbe(new TrxStreamProgressProbe(directory));
            using var monitor = CreateMonitor(time, lines, probe);
            monitor.Observe(Snapshot());
            Assert.Equal(1, probe.Samples);
            time.Advance(300);
            monitor.Observe(Snapshot() with { OutputBytes = 1 });
            Assert.Equal(1, probe.Samples);
            time.Advance(599);
            monitor.Observe(Snapshot() with { OutputBytes = 1 });
            Assert.Equal(2, probe.Samples);
            Assert.False(monitor.Stopped);
            Assert.Empty(lines);
            time.Advance(1);
            monitor.Observe(Snapshot() with { OutputBytes = 1 });
            AssertStalled(monitor, lines);
        });
    }

    private static AcceptanceLaneStallMonitor CreateMonitor(ManualTimeProvider time, List<string> lines,
        IAcceptanceLaneProgressProbe? probe) =>
        new("fake lane", time, AcceptanceLaneStallPolicy.StallLimit, lines.Add, probe);

    private static GateHeartbeatSnapshot Snapshot() =>
        new(null, "verification-check", "fake lane", null, null, null, "running",
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, 0, 0, 0);

    private static void AssertStalled(AcceptanceLaneStallMonitor monitor, List<string> lines)
    {
        Assert.True(monitor.Stopped);
        Assert.True(monitor.Token.IsCancellationRequested);
        Assert.Equal(AcceptanceLaneStallPolicy.StallLimit, monitor.Silent);
        Assert.Equal(StallLine, Assert.Single(lines));
    }

    private static string CreateStream(string directory)
    {
        var path = Path.Combine(directory, "trx-stream-1.bin");
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTimeUtc(path, FileTime);
        return path;
    }

    private static void InTempDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lane-trx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class CountingProbe(IAcceptanceLaneProgressProbe inner) : IAcceptanceLaneProgressProbe
    {
        internal int Samples { get; private set; }
        public string? Sample()
        {
            Samples++;
            return inner.Sample();
        }
    }
}
