using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its root and scopes the AsyncLocal host-health probe.
public sealed class GateLaneTimeLedgerTestsExperimentArms
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void GateAndTickWritersRecordOnThenOffAsVersionTwo()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var experiment = fixture.Add(ExperimentArmStampTests.AlternatingSpec());
        foreach (var flag in new[] { true, false })
        {
            File.WriteAllText(fixture.PolicyPath, ExperimentFlagTestFixture.PolicyJson(flag));
            CompleteGate(fixture.Root, flag ? "on-attempt" : "off-attempt");
            TickCpuLedger.TryAppend(fixture.Root, flag ? 1 : 2, 100, 30, 20, 50, true);
        }
        var ledgers = ExperimentHostLedgers.Read(fixture.Root);
        Assert.Equal(2, ledgers.GateLaneTimes.Count);
        Assert.Equal(2, ledgers.TickCpu.Count);
        Assert.Collection(ledgers.GateLaneTimes,
            row => AssertStamped(row.Version, row.ExperimentArms, experiment.Id, "on"),
            row => AssertStamped(row.Version, row.ExperimentArms, experiment.Id, "off"));
        Assert.Collection(ledgers.TickCpu,
            row => AssertStamped(row.Version, row.ExperimentArms, experiment.Id, "on"),
            row => AssertStamped(row.Version, row.ExperimentArms, experiment.Id, "off"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnstampedAndFailedStampWritesKeepVersionOneJson(bool failedStamp)
    {
        using var fixture = new ExperimentFlagTestFixture();
        if (failedStamp)
        {
            fixture.Add(ExperimentArmStampTests.AlternatingSpec());
            File.WriteAllText(fixture.PolicyPath, "not-json");
        }
        CompleteGate(fixture.Root, "unstamped");
        TickCpuLedger.TryAppend(fixture.Root, 1, 100, 30, 20, 50, false);
        var ledgers = ExperimentHostLedgers.Read(fixture.Root);
        var gate = Assert.Single(ledgers.GateLaneTimes);
        var tick = Assert.Single(ledgers.TickCpu);
        Assert.Equal(1, gate.Version);
        Assert.Null(gate.ExperimentArms);
        Assert.Equal("completed", gate.Outcome);
        Assert.Equal(1000d, gate.TotalMs);
        Assert.Equal(1, tick.Version);
        Assert.Null(tick.ExperimentArms);
        Assert.Equal(100, tick.CpuMs);
        foreach (var path in new[] { GateLaneTimeLedger.ResolveStorePath(fixture.Root), TickCpuLedger.ResolveStorePath(fixture.Root) })
        {
            using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
            Assert.False(json.RootElement.TryGetProperty("experimentArms", out _));
        }
    }

    [Fact]
    public void LegacyJsonAndMixedLedgersRetainBothVersions()
    {
        using var fixture = new ExperimentFlagTestFixture();
        const string gateJson = """
            {"observedAt":"2026-10-02T00:00:00Z","gateAttemptId":"legacy","goalId":null,"outcome":"completed","totalMs":1000,"localLaneMs":1000,"remoteLaneMs":0,"remoteLaneCount":0,"version":1}
            """;
        const string tickJson = """
            {"observedAt":"2026-10-02T00:00:00Z","tick":1,"cpuMs":100,"cpuSweepMs":30,"cpuPrewalkMs":20,"cpuWalkMs":50,"hadGoalChanges":false,"version":1}
            """;
        var gate = JsonSerializer.Deserialize<GateLaneTimeLedgerRecord>(gateJson, JsonOptions)!;
        var tick = JsonSerializer.Deserialize<TickCpuLedgerRecord>(tickJson, JsonOptions)!;
        Assert.Equal(1, gate.Version);
        Assert.Null(gate.ExperimentArms);
        Assert.Equal(1, tick.Version);
        Assert.Null(tick.ExperimentArms);
        var arms = new Dictionary<string, string> { ["experiment"] = "on" };
        File.WriteAllLines(GateLaneTimeLedger.ResolveStorePath(fixture.Root),
            [gateJson, JsonSerializer.Serialize(gate with { Version = 2, ExperimentArms = arms }, JsonOptions)]);
        File.WriteAllLines(TickCpuLedger.ResolveStorePath(fixture.Root),
            [tickJson, JsonSerializer.Serialize(tick with { Version = 2, ExperimentArms = arms }, JsonOptions)]);
        var ledgers = ExperimentHostLedgers.Read(fixture.Root);
        Assert.Collection(ledgers.GateLaneTimes,
            row => { Assert.Equal(1, row.Version); Assert.Null(row.ExperimentArms); },
            row => AssertStamped(row.Version, row.ExperimentArms, "experiment", "on"));
        Assert.Collection(ledgers.TickCpu,
            row => { Assert.Equal(1, row.Version); Assert.Null(row.ExperimentArms); },
            row => AssertStamped(row.Version, row.ExperimentArms, "experiment", "on"));
    }

    private static void CompleteGate(string root, string attempt)
    {
        using var probe = GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Unavailable("fixture"), GateLoadSample.Unavailable("fixture")));
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        using (var accountant = AcceptanceGatePhaseAccountant.Start(time, "goal", progress.Add))
        {
            accountant.BindHostHealthLedger(root, attempt);
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(1000));
            time.Advance(TimeSpan.FromMilliseconds(1000));
            accountant.MarkCompleted(true);
        }
        Assert.Equal("completed", Assert.Single(progress).PhaseBreakdown!.Outcome);
    }

    private static void AssertStamped(int version, IReadOnlyDictionary<string, string>? arms, string id, string arm)
    {
        Assert.Equal(2, version);
        var stamp = Assert.Single(arms!);
        Assert.Equal(id, stamp.Key);
        Assert.Equal(arm, stamp.Value);
    }
}
