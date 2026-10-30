// Parallel-safe: private files and a fixed clock; no processes or global state.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerConsoleGateLanePlanViewTests : IDisposable
{
    private const string Attempt = "a9983773-gate-20261030140000000-0123456789abcdef0123456789abcdef";
    private const string NextAttempt = "a9983773-gate-20261030140100000-0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 14, 5, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-plan-view-" + Guid.NewGuid().ToString("N"));
    private readonly OwnerConsoleGateManifestLane[] _manifest = [new("Console", 10), new("Build", 30)];
    private string Artifact => Path.Combine(_root, Attempt + AcceptanceGateLanePlan.ArtifactSuffix);
    private string Log => Path.Combine(_root, "conduct.jsonl");
    private string Manifest => Path.Combine(_root, "manifest.json");

    public OwnerConsoleGateLanePlanViewTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Manifest, """{"engine":{"maxConcurrentShards":1,"infrastructureTestLanes":[{"name":"Console","estimatedSerialSeconds":10},{"name":"Build","estimatedSerialSeconds":30}]}}""");
    }

    [Fact]
    public void CompletedScheduledLanesLeaveNoQueuedLaneOrRemainingWork()
    {
        WritePlan();
        WriteEvents(PlanEvent(), Progress("shard-complete", "Console", "passed"), Progress("verification-check", "Build"));
        var gate = Load();
        Assert.True(gate.LanePlanAvailable);
        var skipped = Assert.Single(gate.Lanes, row => row.Name == "Build");
        Assert.Equal(OwnerConsoleGateLaneState.Skipped, skipped.State);
        Assert.Equal("no-build-system-change", skipped.SkipReason);
        Assert.DoesNotContain(gate.Lanes, row => row.State is OwnerConsoleGateLaneState.Queued or OwnerConsoleGateLaneState.Running);
        Assert.Equal(1, gate.TotalCount);
        Assert.Equal(1, gate.DoneCount);
        Assert.Equal(0, gate.RemainingSeconds);
        Assert.Equal(1, gate.Fraction);
        var lines = OwnerConsoleGateLayout.Lines(gate, 180, false);
        Assert.Contains(lines, line => line.Contains("skipped: no-build-system-change", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(". 0 queued", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("lane plan unavailable", StringComparison.Ordinal));
        Assert.Contains("skipped: no-build-system-change", OwnerConsoleGateFormatter.LaneLine(skipped));
    }

    [Theory]
    [InlineData("missing-event")]
    [InlineData("missing-file")]
    [InlineData("malformed")]
    [InlineData("wrong-attempt")]
    [InlineData("unsupported")]
    [InlineData("stale-event")]
    [InlineData("duplicate")]
    public void UnavailablePlanPreservesLegacyRowsAndLabelsTheList(string fault)
    {
        WritePlan(fault == "wrong-attempt" ? NextAttempt : Attempt,
            fault == "unsupported" ? "future" : AcceptanceGateLanePlan.CurrentSchemaVersion,
            fault == "duplicate");
        if (fault == "missing-file") File.Delete(Artifact);
        if (fault == "malformed") File.WriteAllText(Artifact, "{");
        var events = new List<OwnerConductEvent>();
        if (fault != "missing-event") events.Add(PlanEvent());
        events.Add(Progress("shard-complete", "Console", "passed", fault == "stale-event" ? NextAttempt : Attempt));
        WriteEvents(events.ToArray());
        var gate = Load();
        var legacy = OwnerConsoleGateProgress.Build(Now, events, _manifest, []) with { Capacity = gate.Capacity };
        Assert.False(gate.LanePlanAvailable);
        Assert.Equal(legacy.Lanes, gate.Lanes);
        Assert.Equal(legacy.RemainingSeconds, gate.RemainingSeconds);
        Assert.Equal(legacy.TotalCount, gate.TotalCount);
        var expected = OwnerConsoleGateLayout.Lines(legacy, 180, false).ToArray();
        expected[2] += "  (lane plan unavailable)";
        Assert.Equal(expected, OwnerConsoleGateLayout.Lines(gate, 180, false));
    }

    [Fact]
    public void EveryLaneSkippedIsCompleteAndPlanOnlyNamesDoNotCreateRows()
    {
        File.WriteAllText(Artifact, new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, Attempt, [],
            [new("Console", "known-empty-candidate"), new("Build", "known-empty-candidate"), new("Unrelated", "known-empty-candidate")]).ToJson());
        WriteEvents(PlanEvent());
        var gate = Load();
        Assert.Equal(2, gate.Lanes.Count);
        Assert.Equal(0, gate.TotalCount);
        Assert.Equal(0, gate.RemainingSeconds);
        Assert.Equal(1, gate.Fraction);
    }

    [Fact]
    public void RefreshReadsAPlanThatBecomesAvailableAndDoesNotChangeUnlistedRows()
    {
        WriteEvents(PlanEvent(), Progress("verification-check", "Unlisted"), Progress("shard-complete", "Console", "passed"));
        var loader = new OwnerConsoleGateProgressLoader(Log, Manifest, Path.Combine(_root, "durations.jsonl"),
            new OwnerConsoleTestClock { Now = Now });
        var before = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
        Assert.False(before.LanePlanAvailable);
        WritePlan();
        var after = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
        Assert.True(after.LanePlanAvailable);
        Assert.Equal(OwnerConsoleGateLaneState.Skipped, after.Lanes.Single(row => row.Name == "Build").State);
        Assert.Equal(before.Lanes.Single(row => row.Name == "Unlisted"), after.Lanes.Single(row => row.Name == "Unlisted"));
    }

    private void WritePlan(string attempt = Attempt, string schema = AcceptanceGateLanePlan.CurrentSchemaVersion, bool duplicate = false) =>
        File.WriteAllText(Artifact, new AcceptanceGateLanePlan(schema, attempt, duplicate ? ["Build"] : ["Console"],
            [new("Build", "no-build-system-change")]).ToJson());
    private OwnerConductEvent PlanEvent() => new(Now.AddSeconds(-30), "gate-progress", "a9983773",
        $"phase=lane-plan target=plan heartbeat={JsonSerializer.Serialize(Artifact)}");
    private OwnerConductEvent Progress(string phase, string lane, string outcome = "unknown", string attempt = Attempt) =>
        new(Now.AddSeconds(-10), "gate-progress", "a9983773",
            $"phase={phase} target={lane} outcome={outcome} heartbeat={attempt}.gate-heartbeat.json");
    private void WriteEvents(params OwnerConductEvent[] events) => File.WriteAllLines(Log, events.Select(item => JsonSerializer.Serialize(new
    { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail })));
    private OwnerConsoleGateProgress Load() => Assert.IsType<OwnerConsoleGateProgress>(new OwnerConsoleGateProgressLoader(
        Log, Manifest, Path.Combine(_root, "durations.jsonl"), new OwnerConsoleTestClock { Now = Now }).Load(new Dictionary<string, string>()));
    public void Dispose() => Directory.Delete(_root, true);
}
