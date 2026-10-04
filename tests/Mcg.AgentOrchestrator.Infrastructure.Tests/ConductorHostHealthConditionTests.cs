using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns unique ledger, state, event-log and mutex paths.
public sealed class ConductorHostHealthConditionTests
{
    [Fact]
    public void ArmedForegroundLockPublishesOnceAndDisarmsOnce()
    {
        using var fixture = new Fixture();
        fixture.Reader.Reading = ForegroundLockReading.Available(2147483647, 26200);
        fixture.Evaluate(3);
        var armed = Assert.Single(fixture.Events());
        Assert.Equal("HOST_HEALTH_FOREGROUND_LOCK_ARMED timeout_ms=2147483647 build=26200 " +
            "remedy=.orchestrator/operator-tools/Set-ForegroundLockTimeout.ps1 (run from a focused console)", Detail(armed));
        Assert.Equal("decision", armed.GetProperty("operator").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow(), armed.GetProperty("timestamp").GetDateTimeOffset());
        fixture.Reader.Reading = ForegroundLockReading.Available(0, 26200);
        fixture.Evaluate(3);
        var events = fixture.Events();
        Assert.Equal(2, events.Length);
        Assert.Equal("HOST_HEALTH_FOREGROUND_LOCK_DISARMED timeout_ms=0 build=26200", Detail(events[1]));
        Assert.Equal("outcome", events[1].GetProperty("operator").GetString());
        Assert.Equal(6, fixture.Reader.Calls.Count);
        Assert.All(fixture.Reader.Calls, call => Assert.Equal("Read", call));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnsupportedOrUnavailableReadKeepsState(bool unavailable, bool seedState)
    {
        using var fixture = new Fixture();
        const string original = "{\"isDegraded\":true,\"pending\":null,\"foregroundLockArmed\":true,\"pagedPoolHigh\":true}";
        if (seedState) File.WriteAllText(fixture.State, original);
        fixture.Reader.Reading = unavailable
            ? ForegroundLockReading.Unavailable("injected-error")
            : ForegroundLockReading.Available(2147483647, 22631);
        fixture.Evaluate(3);
        Assert.Empty(fixture.Events());
        Assert.Equal(3, fixture.Reader.Calls.Count);
        if (seedState) Assert.Equal(original, File.ReadAllText(fixture.State));
        else Assert.False(File.Exists(fixture.State));
    }

    [Theory]
    [InlineData(3000, 3300, 3600, 3900, 4100, 1100)]
    [InlineData(8192, 8192, 8192, 8192, 8192, 0)]
    public void RiseOrHighWaterPublishesOnce(double a, double b, double c, double d, double e, double rise)
    {
        using var fixture = new Fixture();
        fixture.Append(a, b, c, d, e);
        fixture.Evaluate(3);
        var high = Assert.Single(fixture.Events());
        Assert.Equal($"HOST_HEALTH_PAGED_POOL_HIGH paged_pool_mb={e} rise_mb={rise} records=5 " +
            "remedy=.orchestrator/operator-tools/Set-ForegroundLockTimeout.ps1 (run from a focused console); " +
            "leaked pool is reclaimed only by a reboot", Detail(high));
        Assert.Equal("decision", high.GetProperty("operator").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow(), high.GetProperty("timestamp").GetDateTimeOffset());
        fixture.Append(3500);
        fixture.Evaluate(3);
        var events = fixture.Events();
        Assert.Equal(2, events.Length);
        Assert.Equal("HOST_HEALTH_PAGED_POOL_NORMAL paged_pool_mb=3500", Detail(events[1]));
        Assert.Equal("outcome", events[1].GetProperty("operator").GetString());
    }

    [Theory]
    [InlineData(3000d, 3500d, 3400d, 3900d, 4100d)] // Net rise alone is insufficient.
    [InlineData(3000d, 3200d, 3400d, 3600d, 4023d)] // Just below the rise threshold.
    [InlineData(3000d, null, 3600d, 3900d, 4100d)] // Incomplete evidence.
    [InlineData(8191d, 8191d, 8191d, 8191d, 8191d)]
    public void PoolBelowBothConditionsPublishesNothing(double a, double? b, double c, double d, double e)
    {
        using var fixture = new Fixture();
        fixture.Append(a, b, c, d, e);
        fixture.Evaluate(2);
        Assert.Empty(fixture.Events());
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public void SingleHighWaterRecordPublishesButShortRiseDoesNot()
    {
        using var fixture = new Fixture();
        fixture.Append(3000, 4100);
        fixture.Evaluate(2);
        Assert.Empty(fixture.Events());
        fixture.Append(8192);
        fixture.Evaluate(2);
        var high = Assert.Single(fixture.Events());
        Assert.Contains("paged_pool_mb=8192 rise_mb=5192 records=3", Detail(high));
        fixture.Append((double?)null);
        fixture.Evaluate(2);
        Assert.Single(fixture.Events());
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.State));
        Assert.True(state.RootElement.GetProperty("pagedPoolHigh").GetBoolean());
    }

    [Fact]
    public void WindowsReaderSourceContainsOnlyGetActionAndNoWrites()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.Infrastructure", "Workspaces", "WindowsForegroundLockReader.cs"));
        Assert.Contains("0x2000", source);
        Assert.Contains("SystemParametersInfo(SpiGetForegroundLockTimeout, 0, ref timeoutMs, 0)", source);
        foreach (var forbidden in new[] { "0x2001", "SPI_SET", "SpiSet", "Microsoft.Win32", "Registry", "RegSetValue", "SetValue" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWrittenStateLoadsWithNewConditionsFalse()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.State, "{\"isDegraded\":true,\"pending\":null}");
        fixture.Reader.Reading = ForegroundLockReading.Available(2147483647, 26200);
        fixture.Evaluate(2);
        Assert.StartsWith("HOST_HEALTH_FOREGROUND_LOCK_ARMED ", Detail(Assert.Single(fixture.Events())));
        using (var state = JsonDocument.Parse(File.ReadAllText(fixture.State)))
        {
            Assert.True(state.RootElement.GetProperty("isDegraded").GetBoolean());
            Assert.True(state.RootElement.GetProperty("foregroundLockArmed").GetBoolean());
            Assert.False(state.RootElement.GetProperty("pagedPoolHigh").GetBoolean());
        }
        using var poolFixture = new Fixture();
        File.WriteAllText(poolFixture.State, "{\"isDegraded\":false,\"pending\":null}");
        poolFixture.Append(8192);
        poolFixture.Evaluate(2);
        Assert.StartsWith("HOST_HEALTH_PAGED_POOL_HIGH ", Detail(Assert.Single(poolFixture.Events())));
        using var poolState = JsonDocument.Parse(File.ReadAllText(poolFixture.State));
        Assert.False(poolState.RootElement.GetProperty("foregroundLockArmed").GetBoolean());
        Assert.True(poolState.RootElement.GetProperty("pagedPoolHigh").GetBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedPublishReplaysEachConditionOnce(bool pool, bool afterAppend)
    {
        using var fixture = new Fixture();
        if (pool) fixture.Append(8192);
        else fixture.Reader.Reading = ForegroundLockReading.Available(2147483647, 26200);
        var fail = true;
        var writer = new ConductEventLogWriter(fixture.Log, beforeRequiredEventDrain: () =>
        {
            if (fail && !afterAppend) throw new IOException("injected event write failure");
        });
        fixture.Monitor(writer, () =>
        {
            if (fail && afterAppend) throw new IOException("injected failure before state commit");
        }).Evaluate();
        if (afterAppend) Assert.Single(fixture.Events());
        else Assert.Empty(fixture.Events());
        using var pending = JsonDocument.Parse(File.ReadAllText(fixture.State));
        var transition = pending.RootElement.GetProperty("pending");
        Assert.Equal(pool ? "paged-pool" : "foreground-lock", transition.GetProperty("condition").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow(), transition.GetProperty("observedAt").GetDateTimeOffset());
        var eventId = "host-health:" + transition.GetProperty("id").GetString();
        fail = false;
        fixture.Evaluate(3);
        var emitted = Assert.Single(fixture.Events());
        Assert.Equal(eventId, emitted.GetProperty("event_id").GetString());
        using var committed = JsonDocument.Parse(File.ReadAllText(fixture.State));
        Assert.Equal(JsonValueKind.Null, committed.RootElement.GetProperty("pending").ValueKind);
        Assert.True(committed.RootElement.GetProperty(pool ? "pagedPoolHigh" : "foregroundLockArmed").GetBoolean());
    }

    [Fact]
    public void AllConditionsCommitIndependentlyAndPreserveLatencyText()
    {
        using var fixture = new Fixture();
        foreach (var launch in new[] { 60, 55, 70, 58, 62, 200, 210, 230 })
            fixture.AppendRecord(launch, 11800);
        fixture.Reader.Reading = ForegroundLockReading.Available(2147483647, 26200);
        fixture.Evaluate(3);
        var events = fixture.Events();
        Assert.Equal(3, events.Length);
        Assert.Equal("HOST_HEALTH_DEGRADED launch_ms=230 baseline_ms=55 ratio=4.182 paged_pool_mb=11800 consecutive=3", Detail(events[0]));
        Assert.StartsWith("HOST_HEALTH_PAGED_POOL_HIGH ", Detail(events[1]));
        Assert.StartsWith("HOST_HEALTH_FOREGROUND_LOCK_ARMED ", Detail(events[2]));
        fixture.AppendRecord(60, 3500);
        fixture.Reader.Reading = ForegroundLockReading.Available(0, 26200);
        fixture.Evaluate(3);
        events = fixture.Events();
        Assert.Equal(6, events.Length);
        Assert.StartsWith("HOST_HEALTH_RECOVERED ", Detail(events[3]));
        Assert.StartsWith("HOST_HEALTH_PAGED_POOL_NORMAL ", Detail(events[4]));
        Assert.StartsWith("HOST_HEALTH_FOREGROUND_LOCK_DISARMED ", Detail(events[5]));
    }

    private static string Detail(JsonElement record) => record.GetProperty("detail").GetString()!;

    private sealed class FakeReader : IForegroundLockReader
    {
        internal ForegroundLockReading Reading { get; set; } = ForegroundLockReading.Unavailable("not-configured");
        internal List<string> Calls { get; } = [];
        public ForegroundLockReading Read()
        {
            Calls.Add(nameof(Read));
            return Reading;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(123);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "host-health-conditions", Guid.NewGuid().ToString("N"));
        internal string Ledger => Path.Combine(Root, "host-health.jsonl");
        internal string State => Path.Combine(Root, ConductorHostHealthMonitor.StateFileName);
        internal string Log => Path.Combine(Root, "events.log");
        internal FakeReader Reader { get; } = new();
        internal FixedClock Clock { get; } = new();
        internal Fixture() => Directory.CreateDirectory(Root);
        internal void Append(params double?[] pools)
        {
            foreach (var pool in pools) AppendRecord(60, pool);
        }
        internal void AppendRecord(double launch, double? pool) => GateHostHealthLedger.Append(Ledger,
            new(Clock.GetUtcNow(), Guid.NewGuid().ToString("N"), launch, pool));
        internal ConductorHostHealthMonitor Monitor(ConductEventLogWriter? writer = null, Action? afterAppend = null) =>
            new(Ledger, State, writer ?? new ConductEventLogWriter(Log), afterAppend, new(Reader, Clock));
        internal void Evaluate(int count)
        {
            for (var index = 0; index < count; index++) Monitor().Evaluate();
        }
        internal JsonElement[] Events() => File.Exists(Log)
            ? File.ReadAllLines(Log).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray()
            : [];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
