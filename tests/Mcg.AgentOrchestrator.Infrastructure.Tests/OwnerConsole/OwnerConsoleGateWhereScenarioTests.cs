// Parallel-safe: a per-test temporary directory and a fixed clock; no process.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateWhereScenarioTests : IDisposable
{
    private const string FullWhere = "WHERE   mcg-exec-1 3/3 [##########]   mcg-exec-2 2/2 [##########]   local 1/5 [##--------]";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-where-" + Guid.NewGuid().ToString("N"));
    private readonly OwnerConsoleTestClock _clock = new() { Now = Now };
    private string LogPath => Path.Combine(_root, "conduct-events.log");
    private string ManifestPath => Path.Combine(_root, "acceptance-manifest.json");
    private string HistoryPath => Path.Combine(_root, "lane-durations.jsonl");
    private string ConfigPath => Path.Combine(_root, "executors.json");

    public OwnerConsoleGateWhereScenarioTests()
    {
        Directory.CreateDirectory(_root);
        WriteManifest("5");
        File.WriteAllText(ConfigPath,
            """{"executors":[{"id":"mcg-exec-1","slots":3},{"id":"mcg-exec-2","slots":2}],"lanes":["*"]}""");
        File.WriteAllLines(HistoryPath, Enumerable.Range(1, 9).Select(i => JsonSerializer.Serialize(new
        {
            laneName = $"Lane {i}", durationSeconds = 90, recordedAt = Now.AddDays(-1)
        })));
        var events = new List<string>
        {
            Event(-120, "acceptance-cohort", "ACCEPTANCE_COHORT_ENTRY members=ca798e7c train=owner")
        };
        for (var i = 1; i <= 5; i++)
            events.Add(Progress(-90, "remote-shard-started", i,
                " heartbeat=activity-0.heartbeat.json executor=" + (i <= 3 ? "mcg-exec-1" : "mcg-exec-2")));
        events.Add(Progress(-80, "verification-check", 6, " heartbeat=activity-0.heartbeat.json"));
        events.Add(Progress(-60, "shard-complete", 7, " outcome=passed"));
        events.Add(Progress(-60, "shard-complete", 8, " outcome=passed"));
        events.Add(Progress(-75, "remote-shard-started", 9, " heartbeat=activity-0.heartbeat.json executor=mcg-exec-2"));
        events.Add(Progress(-30, "remote-shard-complete", 9, " outcome=passed"));
        File.WriteAllLines(LogPath, events);
    }

    [Fact]
    public void LiveLogs_ConfiguredSlots_RenderOccupancyWithoutChangingRows()
    {
        var gate = Load(CreateLoader(ConfigPath));
        Assert.True(gate.Live);
        Assert.Equal(9, gate.Lanes.Count);
        Assert.Equal(FullWhere, Where(gate));
        var capacity = Assert.IsType<OwnerConsoleGateCapacity>(gate.Capacity);
        Assert.Equal(5, capacity.LocalBudget);
        Assert.Equal(new[] { ("mcg-exec-1", 3), ("mcg-exec-2", 2) }, capacity.Executors.Select(e => (e.Id, e.Slots)));
        Assert.Equal(6, gate.Lanes.Count(row => row.State == OwnerConsoleGateLaneState.Running));
        var completedRemote = Assert.Single(gate.Lanes, row => row.Name == "Lane 9");
        Assert.Equal(OwnerConsoleGateLaneState.Done, completedRemote.State);
        Assert.Equal("mcg-exec-2", completedRemote.Location);
        var withoutConfig = Load(CreateLoader(null));
        Assert.Equal(withoutConfig.Lanes.Select(RowValues), gate.Lanes.Select(RowValues));
    }

    [Fact]
    public void AppendedCompletions_WithoutExecutor_ClearOnlyCompletedOccupancy()
    {
        var loader = CreateLoader(ConfigPath);
        Assert.Equal(FullWhere, Where(Load(loader)));
        File.AppendAllLines(LogPath, Enumerable.Range(1, 3)
            .Select(i => Progress(-10, "remote-shard-complete", i, " outcome=passed")));
        var completed = Load(loader);
        Assert.Equal(FullWhere.Replace("mcg-exec-1 3/3 [##########]", "mcg-exec-1 0/3 [----------]"), Where(completed));
        Assert.All(completed.Lanes.Take(3), row =>
        {
            Assert.Equal(OwnerConsoleGateLaneState.Done, row.State);
            Assert.Equal("mcg-exec-1", row.Location);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrInvalidConfig_PreservesGateAndShowsUnknownRunning(bool invalid)
    {
        var path = Path.Combine(_root, "missing-or-invalid.json");
        if (invalid) File.WriteAllText(path, "{ invalid json");
        var gate = Load(CreateLoader(path));
        Assert.True(gate.Live);
        Assert.Empty(Assert.IsType<OwnerConsoleGateCapacity>(gate.Capacity).Executors);
        Assert.Equal("WHERE   mcg-exec-1 3 running   mcg-exec-2 2 running   local 1/5 [##--------]", Where(gate));
    }

    [Fact]
    public void LowerCoreBudget_CapsLocalCapacity()
    {
        var gate = Load(CreateLoader(ConfigPath, coreBudget: 2));
        Assert.Equal(FullWhere.Replace("local 1/5 [##--------]", "local 1/2 [#####-----]"), Where(gate));
        Assert.Equal(2, Assert.IsType<OwnerConsoleGateCapacity>(gate.Capacity).LocalBudget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"bad\"")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("null")]
    public void MissingOrMalformedShardBudget_DefaultsToOne(string? shards)
    {
        WriteManifest(shards);
        var gate = Load(CreateLoader(ConfigPath));
        Assert.Equal(FullWhere.Replace("local 1/5 [##--------]", "local 1/1 [##########]"), Where(gate));
        Assert.Equal(1, Assert.IsType<OwnerConsoleGateCapacity>(gate.Capacity).LocalBudget);
    }

    [Fact]
    public void ConfigEditedBetweenLoads_RefreshesSlotCapacity()
    {
        var loader = CreateLoader(ConfigPath);
        Assert.Equal(FullWhere, Where(Load(loader)));
        File.WriteAllText(ConfigPath,
            """{"executors":[{"id":"mcg-exec-1","slots":6},{"id":"mcg-exec-2","slots":2}],"lanes":["*"]}""");
        Assert.Equal(FullWhere.Replace("mcg-exec-1 3/3 [##########]", "mcg-exec-1 3/6 [#####-----]"), Where(Load(loader)));
    }

    private OwnerConsoleGateProgressLoader CreateLoader(string? executorConfigPath, int coreBudget = 8) =>
        new(LogPath, ManifestPath, HistoryPath, _clock, executorConfigPath, coreBudget);
    private static OwnerConsoleGateProgress Load(OwnerConsoleGateProgressLoader loader) =>
        Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
    private static string Where(OwnerConsoleGateProgress gate) =>
        Assert.Single(OwnerConsoleGateFormatter.Lines(gate), line => line.StartsWith("WHERE   ", StringComparison.Ordinal));
    private static (string, OwnerConsoleGateLaneState, string, string, TimeSpan, double?) RowValues(OwnerConsoleGateLane row) =>
        (row.Name, row.State, row.Outcome, row.Location, row.Elapsed, row.MedianSeconds);

    private void WriteManifest(string? shards)
    {
        var engine = new Dictionary<string, object>
        {
            ["infrastructureTestLanes"] = Enumerable.Range(1, 9).Select(i => new { name = $"Lane {i}", estimatedSerialSeconds = 100 }).ToArray()
        };
        if (shards is not null) engine["maxConcurrentShards"] = JsonSerializer.Deserialize<JsonElement>(shards);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new { engine }));
    }

    private static string Progress(int seconds, string phase, int lane, string extra) => Event(seconds, "gate-progress",
        $"PHASE_PROGRESS goal=ca798e7c phase={phase} target=\"infrastructure tests: Lane {lane}\" elapsed_ms=10000" + extra);
    private static string Event(int seconds, string kind, string detail) => JsonSerializer.Serialize(new
    {
        timestamp = Now.AddSeconds(seconds), eventKind = kind, goalId = "ca798e7c", detail
    });
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
