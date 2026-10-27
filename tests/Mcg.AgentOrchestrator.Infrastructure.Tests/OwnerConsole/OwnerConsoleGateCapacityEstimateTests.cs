// Parallel-safe: each load owns a unique temporary directory and uses an injected clock.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateCapacityEstimateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoBreakdownEventUsesConfiguredCapacity()
    {
        var gate = Load(5, 5);

        Assert.Equal(5, gate.Capacity!.LocalBudget);
        Assert.All(gate.Lanes, lane => Assert.Equal(OwnerConsoleGateLaneState.Queued, lane.State));
        Assert.Equal(120, gate.RemainingSeconds);
    }

    [Fact]
    public void BreakdownConcurrencyOverridesConfiguredCapacity()
    {
        var gate = Load(5, 5, Breakdown(-3600, "2"));

        Assert.Equal(5, gate.Capacity!.LocalBudget);
        Assert.Equal(300, gate.RemainingSeconds);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 1)]
    public void SerialCapacityKeepsSerialSum(int maxConcurrentShards, int coreBudget)
    {
        var gate = Load(maxConcurrentShards, coreBudget);

        Assert.Equal(1, gate.Capacity!.LocalBudget);
        Assert.Equal(600, gate.RemainingSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unavailable")]
    [InlineData("0")]
    [InlineData("-2")]
    public void NewestUnusableBreakdownFallsBackToConfiguredCapacity(string concurrency)
    {
        var gate = Load(5, 5, Breakdown(-3600, "2"), Breakdown(-1800, concurrency));

        Assert.Equal(120, gate.RemainingSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningManifestLanesRaiseConcurrencyFloor(bool hasBreakdown)
    {
        var events = new List<string> { Running("A"), Running("B") };
        if (hasBreakdown) events.Add(Breakdown(-3600, "1"));
        var gate = Load(1, 5, events.ToArray());

        Assert.Equal(1, gate.Capacity!.LocalBudget);
        Assert.Equal(2, gate.Lanes.Count(lane => lane.State == OwnerConsoleGateLaneState.Running));
        Assert.Equal(300, gate.RemainingSeconds);
    }

    private static OwnerConsoleGateProgress Load(int maxConcurrentShards, int coreBudget, params string[] events)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gate-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "conduct-events.log");
            var manifest = Path.Combine(directory, "acceptance-manifest.json");
            File.WriteAllLines(log, events.Append(Event(-60, "acceptance-cohort",
                "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb")));
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                engine = new
                {
                    maxConcurrentShards,
                    infrastructureTestLanes = Enumerable.Range(0, 10).Select(index => new
                    {
                        name = ((char)('A' + index)).ToString(), estimatedSerialSeconds = 60
                    }).ToArray()
                }
            }));
            var loader = new OwnerConsoleGateProgressLoader(log, manifest,
                Path.Combine(directory, "missing-durations.jsonl"), new OwnerConsoleTestClock { Now = Now },
                coreBudget: coreBudget);

            var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
            Assert.True(gate.Live);
            Assert.Equal(10, gate.Lanes.Count);
            Assert.All(gate.Lanes, lane => Assert.Equal(60d, lane.MedianSeconds));
            return gate;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Breakdown(int age, string concurrency) => Event(age, "gate-progress",
        "PHASE_PROGRESS goal=99999999 phase=gate-phase-breakdown elapsed_ms=0 " +
        "target=scope=verifier-run;outcome=failed;total_ms=1;lane_ms=1" +
        (concurrency.Length == 0 ? "" : ";shard_concurrency_effective=" + concurrency), "99999999");

    private static string Running(string lane) => Event(0, "gate-progress",
        $"PHASE_PROGRESS goal=776a132e members=776a132e,eade84cb phase=verification-check target=\"infrastructure tests: {lane}\" elapsed_ms=0");

    private static string Event(int age, string kind, string detail, string goal = "776a132e") =>
        JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = kind, goalId = goal, detail });
}
