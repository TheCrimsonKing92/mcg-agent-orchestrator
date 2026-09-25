using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LaunchLockSummaryFormatterTests
{
    [Xunit.Fact]
    public void FormatterEmitsAllFieldsWithKnownValues()
    {
        var frequency = Stopwatch.Frequency;
        var snapshot = new LaunchLockSnapshot(
            10, frequency * 4, frequency, frequency * 2, frequency * 4,
            frequency * 8, frequency * 2, frequency * 4, frequency * 6,
            frequency, [1, 2, 3, 4]);

        var line = LaunchLockTelemetry.FormatSummary(snapshot, 4242);

        Xunit.Assert.Equal(
            "LAUNCH_LOCK_SUMMARY pid=4242 launches=10 " +
            "wait_ms_total=4000.000 wait_p50_ms=1000.000 wait_p95_ms=2000.000 wait_max_ms=4000.000 " +
            "hold_ms_total=8000.000 hold_p50_ms=2000.000 hold_p95_ms=4000.000 hold_max_ms=6000.000 " +
            "create_ms_total=1000.000 by_entry=AcquireConsoleForChildSpawn:1,AcquireSuppressedChildSpawn:2," +
            "AcquireErrorModeForChildSpawn:3,Start:4",
            line);
    }

    [Xunit.Fact]
    public void HistogramPercentilesAreOrderedAndBoundedByMaximum()
    {
        var counters = new LaunchLockCounters();
        var frequency = Stopwatch.Frequency;
        counters.Record(LaunchLockEntryPoint.Start, frequency, frequency * 2, 0);
        counters.Record(LaunchLockEntryPoint.Start, frequency * 2, frequency * 4, 0);
        counters.Record(LaunchLockEntryPoint.Start, frequency * 4, frequency * 8, 0);

        var snapshot = counters.Snapshot();
        Xunit.Assert.Equal(3, snapshot.Launches);
        Xunit.Assert.Equal(3, snapshot.ByEntry[(int)LaunchLockEntryPoint.Start]);
        Xunit.Assert.Equal(frequency * 7, snapshot.WaitTicksTotal);
        Xunit.Assert.Equal(frequency * 14, snapshot.HoldTicksTotal);
        Xunit.Assert.Equal((long)Math.Ceiling(2_097_152 * frequency / 1_000_000.0), snapshot.WaitP50Ticks);
        Xunit.Assert.Equal((long)Math.Ceiling(4_194_304 * frequency / 1_000_000.0), snapshot.HoldP50Ticks);
        Xunit.Assert.Equal(frequency * 4, snapshot.WaitP95Ticks);
        Xunit.Assert.Equal(frequency * 8, snapshot.HoldP95Ticks);
        Xunit.Assert.Equal(frequency * 4, snapshot.WaitMaxTicks);
        Xunit.Assert.Equal(frequency * 8, snapshot.HoldMaxTicks);
    }

    [Xunit.Fact]
    public async Task AssemblyFixtureWritesSummaryAtTeardown()
    {
        using var diagnostics = new StringWriter();
        var fixture = new AssemblyTempRootCleanupFixture(() => null, diagnostics);

        await fixture.DisposeAsync();

        var line = diagnostics.ToString().TrimEnd('\r', '\n');
        Xunit.Assert.StartsWith($"LAUNCH_LOCK_SUMMARY pid={Environment.ProcessId} ", line);
        Xunit.Assert.Single(diagnostics.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
