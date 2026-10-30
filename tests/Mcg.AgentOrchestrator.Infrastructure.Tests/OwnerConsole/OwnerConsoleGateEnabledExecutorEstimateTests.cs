// Parallel-safe: pure snapshots, an injected clock, and a unique temporary directory per loader.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateEnabledExecutorEstimateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 1, 0, 0, TimeSpan.Zero);
    private static readonly OwnerConsoleGateManifestLane[] Manifest = Enumerable.Range(0, 5)
        .Select(index => new OwnerConsoleGateManifestLane(((char)('A' + index)).ToString(), 60)).ToArray();

    [Fact]
    public void Build_DisabledExecutor_ExtendsEstimateAndMatchesOnlyEnabled()
    {
        var enabled = Build([new("a", 2), new("b", 2)]);
        var disabled = Build([new("a", 2), new("b", 2, false)]);
        var onlyEnabled = Build([new("a", 2)]);

        Assert.Equal(60, enabled.RemainingSeconds);
        Assert.Equal(120, disabled.RemainingSeconds);
        Assert.True(disabled.RemainingSeconds > enabled.RemainingSeconds);
        Assert.Equal(onlyEnabled.RemainingSeconds, disabled.RemainingSeconds);
        Assert.Equal(onlyEnabled.Fraction, disabled.Fraction);
        Assert.Equal(enabled.Lanes, disabled.Lanes);
        Assert.Equal(1, disabled.Lanes.Count(lane => lane.State == OwnerConsoleGateLaneState.Running));
        Assert.Equal(4, disabled.Lanes.Count(lane => lane.State == OwnerConsoleGateLaneState.Queued));
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(2, 180)]
    public void Build_NoEnabledExecutors_PreservesLocalEstimate(int capacity, int expected)
    {
        var omitted = OwnerConsoleGateProgress.Build(Now, Events(), Manifest, [], capacity: capacity);
        var empty = Build([], capacity);
        var disabled = Build([new("a", 2, false), new("b", 2, false)], capacity);

        Assert.Equal(expected, omitted.RemainingSeconds);
        Assert.Equal(expected, empty.RemainingSeconds);
        Assert.Equal(expected, disabled.RemainingSeconds);
        Assert.Equal(omitted.Fraction, empty.Fraction);
        Assert.Equal(omitted.Fraction, disabled.Fraction);
    }

    [Fact]
    public void Build_BreakdownConcurrency_AddsEnabledSlotsToLocalOverride()
    {
        var events = Events().Append(Parse(Event(-120, "gate-progress",
            "PHASE_PROGRESS goal=99999999 phase=gate-phase-breakdown elapsed_ms=0 " +
            "target=scope=verifier-run;outcome=failed;shard_concurrency_effective=1", "99999999"))).ToArray();
        var enabled = OwnerConsoleGateProgress.Build(Now, events, Manifest, [], capacity: 5,
            executors: [new("a", 2), new("b", 2)]);
        var disabled = OwnerConsoleGateProgress.Build(Now, events, Manifest, [], capacity: 5,
            executors: [new("a", 2), new("b", 2, false)]);

        Assert.Equal(60, enabled.RemainingSeconds);
        Assert.Equal(120, disabled.RemainingSeconds);
    }

    [Fact]
    public void Build_RunningLanesOnDisabledExecutor_PreservesConcurrencyFloor()
    {
        var events = new[] { Entry() }.Concat(Manifest.Select(lane => Event(0, "gate-progress",
            $"PHASE_PROGRESS goal=776a132e members=776a132e,eade84cb phase=remote-shard-started " +
            $"target=\"infrastructure tests: {lane.Name}\" executor=b elapsed_ms=0"))).Select(Parse).ToArray();
        var gate = OwnerConsoleGateProgress.Build(Now, events, Manifest, [], capacity: 1,
            executors: [new("a", 2), new("b", 2, false)]);

        Assert.Equal(5, gate.Lanes.Count(lane => lane.State == OwnerConsoleGateLaneState.Running));
        Assert.All(gate.Lanes, lane => Assert.Equal("b", lane.Location));
        Assert.Equal(60, gate.RemainingSeconds);
    }

    [Fact]
    public void Load_AdmissionChanges_RefreshEnabledCapacityAndEstimate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gate-enabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Path.Combine(directory, "conduct-events.log");
            var manifest = Path.Combine(directory, "acceptance-manifest.json");
            var config = Path.Combine(directory, "remote-executors.json");
            File.WriteAllLines(log, EventLines());
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                engine = new
                {
                    maxConcurrentShards = 5,
                    infrastructureTestLanes = Manifest.Select(lane => new
                    { name = lane.Name, estimatedSerialSeconds = lane.EstimatedSerialSeconds }).ToArray()
                }
            }));
            var loader = new OwnerConsoleGateProgressLoader(log, manifest,
                Path.Combine(directory, "missing-durations.jsonl"), new OwnerConsoleTestClock { Now = Now },
                executorConfigPath: config, coreBudget: 1);

            File.WriteAllText(config,
                """{"executors":[{"id":"a","slots":2},{"id":"b","slots":2}],"lanes":["*"]}""");
            var enabled = Load();
            Assert.All(enabled.Capacity!.Executors, executor => Assert.True(executor.Enabled));

            File.WriteAllText(config,
                """{"executors":[{"id":"a","slots":2},{"id":"b","slots":2,"enabled":false}],"lanes":["*"]}""");
            var disabled = Load();
            Assert.True(Assert.Single(disabled.Capacity!.Executors, executor => executor.Id == "a").Enabled);
            Assert.False(Assert.Single(disabled.Capacity.Executors, executor => executor.Id == "b").Enabled);

            File.WriteAllText(config, """{"executors":[{"id":"a","slots":2}],"lanes":["*"]}""");
            var onlyEnabled = Load();
            Assert.Single(onlyEnabled.Capacity!.Executors);
            Assert.Equal(60, enabled.RemainingSeconds);
            Assert.Equal(120, disabled.RemainingSeconds);
            Assert.True(disabled.RemainingSeconds > enabled.RemainingSeconds);
            Assert.Equal(onlyEnabled.RemainingSeconds, disabled.RemainingSeconds);
            Assert.Equal(onlyEnabled.Fraction, disabled.Fraction);

            OwnerConsoleGateProgress Load()
            {
                var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
                Assert.True(gate.Live);
                Assert.Equal(1, gate.Capacity!.LocalBudget);
                Assert.Equal(5, gate.Lanes.Count);
                return gate;
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static OwnerConsoleGateProgress Build(IReadOnlyList<OwnerConsoleGateExecutorSlots> executors, int capacity = 1) =>
        OwnerConsoleGateProgress.Build(Now, Events(), Manifest, [], capacity: capacity, executors: executors);

    private static OwnerConductEvent[] Events() => EventLines().Select(Parse).ToArray();
    private static OwnerConductEvent Parse(string line) => Assert.IsType<OwnerConductEvent>(OwnerConsoleGateProgress.ParseEvent(line));
    private static string[] EventLines() => [Entry(), Event(0, "gate-progress",
        "PHASE_PROGRESS goal=776a132e members=776a132e,eade84cb phase=verification-check target=\"infrastructure tests: A\" elapsed_ms=0")];
    private static string Entry() => Event(-60, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=776a132e,eade84cb");
    private static string Event(int age, string kind, string detail, string goal = "776a132e") =>
        JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = kind, goalId = goal, detail });
}
