// Parallel-safe: immutable fixtures and an injected clock; no process or shared files.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateRemainingEstimateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void QueuedLanesUseLongestFirstSchedule()
    {
        var gate = Build([300, 250, 200, 150, 100, 50], [Breakdown(-3600, "3")]);

        Assert.All(gate.Lanes, lane => Assert.Equal(OwnerConsoleGateLaneState.Queued, lane.State));
        Assert.Equal(350, gate.RemainingSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unavailable")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-2")]
    public void MissingOrSerialConcurrencyKeepsSerialSum(string? concurrency)
    {
        var gate = Build([300, 250, 200, 150, 100, 50],
            concurrency is null ? [] : [Breakdown(-3600, concurrency)]);

        Assert.Equal(1050, gate.RemainingSeconds);
    }

    [Theory]
    [InlineData("2", 300)]
    [InlineData("1", 500)]
    public void RunningLaneReservesItsRemainingWorkFirst(string concurrency, int expected)
    {
        var gate = Build([300, 150, 150], [Breakdown(-3600, concurrency), Running("A", 100000)]);

        Assert.Equal(OwnerConsoleGateLaneState.Running, gate.Lanes[0].State);
        Assert.Equal(TimeSpan.FromSeconds(100), gate.Lanes[0].Elapsed);
        Assert.Equal(expected, gate.RemainingSeconds);
    }

    [Fact]
    public void NewestTimestampWinsRegardlessOfInputOrder()
    {
        var gate = Build([300, 250, 200, 150, 100, 50],
            [Breakdown(-1800, "5"), Breakdown(-3600, "2")]);

        Assert.Equal(300, gate.RemainingSeconds);
    }

    [Fact]
    public void LaterInputWinsWhenBreakdownTimestampsTie()
    {
        var gate = Build([300, 250, 200, 150, 100, 50],
            [Breakdown(-1800, "2"), Breakdown(-1800, "5")]);

        Assert.Equal(300, gate.RemainingSeconds);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("")]
    public void NewestMissingMeasurementDoesNotReuseOlderConcurrency(string concurrency)
    {
        var gate = Build([300, 250, 200, 150, 100, 50],
            [Breakdown(-3600, "5"), Breakdown(-1800, concurrency)]);

        Assert.Equal(1050, gate.RemainingSeconds);
    }

    [Fact]
    public void ChangingConcurrencyReprojectsProgressWithoutChangingLaneTruth()
    {
        var running = Running("A", 20000);
        var serial = Build([100, 100, 100], [running, Breakdown(-3600, "1")]);
        var parallel = Build([100, 100, 100], [running, Breakdown(-3600, "1"), Breakdown(-1800, "3")]);
        var fallback = Build([100, 100, 100],
            [running, Breakdown(-3600, "3"), Breakdown(-1800, "unavailable")]);

        Assert.Equal(280, serial.RemainingSeconds);
        Assert.Equal(3d / 17, serial.Fraction, 10); // 60 / (60 + 280).
        Assert.Equal(100, parallel.RemainingSeconds);
        Assert.Equal(3d / 8, parallel.Fraction, 10); // 60 / (60 + 100).
        Assert.Equal(serial.Fraction, fallback.Fraction);
        Assert.True(fallback.Fraction < parallel.Fraction); // Estimates need not be monotonic.
        Assert.Equal(serial.Lanes, parallel.Lanes);
        Assert.Equal(serial.Lanes, fallback.Lanes);
        foreach (var gate in new[] { serial, parallel, fallback })
        {
            Assert.Equal(TimeSpan.FromSeconds(60), gate.Elapsed);
            Assert.True(double.IsFinite(gate.Fraction));
            Assert.InRange(gate.Fraction, 0, 0.99);
            Assert.Equal(0, gate.DoneCount);
            Assert.Equal(3, gate.TotalCount);
            Assert.False(gate.Failing);
        }
    }

    [Fact]
    public void RunningManifestLanesRaiseConcurrencyFloor()
    {
        var gate = Build([300, 250, 200, 150, 100, 50], [Running("A", 0), Running("B", 0)]);

        Assert.Equal(2, gate.Lanes.Count(lane => lane.State == OwnerConsoleGateLaneState.Running));
        Assert.Equal(550, gate.RemainingSeconds);
    }

    private static OwnerConsoleGateProgress Build(double[] medians, OwnerConductEvent[] observations)
    {
        var manifest = medians.Select((_, index) => new OwnerConsoleGateManifestLane(((char)('A' + index)).ToString(), 999)).ToArray();
        var history = manifest.Select((lane, index) => new OwnerConsoleGateDuration("infrastructure tests: " + lane.Name, medians[index], Now));
        var entry = Event(-60, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb");
        var gate = OwnerConsoleGateProgress.Build(Now, observations.Append(entry), manifest, history);

        Assert.True(gate.Live);
        Assert.Equal("cohort", gate.Kind);
        Assert.Equal(["776a132e", "eade84cb"], gate.Members.Select(member => member.Id));
        Assert.Equal(medians, gate.Lanes.Select(lane => lane.MedianSeconds!.Value));
        return gate;
    }

    private static OwnerConductEvent Breakdown(int age, string concurrency) => Event(age, "gate-progress",
        "PHASE_PROGRESS goal=99999999 phase=gate-phase-breakdown elapsed_ms=0 " +
        "target=scope=verifier-run;outcome=failed;total_ms=1;lane_ms=1" +
        (concurrency.Length == 0 ? "" : ";shard_concurrency_effective=" + concurrency), "99999999");

    private static OwnerConductEvent Running(string lane, int elapsed) => Event(0, "gate-progress",
        $"PHASE_PROGRESS goal=776a132e members=776a132e,eade84cb phase=verification-check target=\"infrastructure tests: {lane}\" elapsed_ms={elapsed}");

    // Round-trip the real JSONL envelope, as the loader does.
    private static OwnerConductEvent Event(int age, string kind, string detail, string goal = "776a132e") =>
        OwnerConsoleGateProgress.ParseEvent(JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = kind, goalId = goal, detail }))!;
}
