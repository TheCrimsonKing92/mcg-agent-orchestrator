using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its ledger, state, log and monitor mutex.
public sealed class ConductorHostHealthCacheBoundPoolTests
{
    private const string Remedy = "remedy=.orchestrator/operator-tools/Set-ForegroundLockTimeout.ps1 " +
        "(run from a focused console); leaked pool is reclaimed only by a reboot";

    [Fact]
    public void CacheOnlyRiseDoesNotAlert()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 5; index++)
            fixture.Append(2200 + index * 300, 1000 + index * 300);
        fixture.Evaluate();
        Assert.Empty(fixture.Details());
        Assert.False(File.Exists(fixture.State));
    }

    [Fact]
    public void ResidualRiseAlertsWithTotalCacheAndResidualFields()
    {
        using var fixture = new Fixture();
        foreach (var total in new[] { 3200, 3500, 3800, 4100, 4300 }) fixture.Append(total, 2000);
        fixture.Evaluate();
        Assert.Equal("HOST_HEALTH_PAGED_POOL_HIGH paged_pool_mb=4300 rise_mb=1100 records=5 " +
            "file_cache_mb=2000 residual_mb=2300 " + Remedy, Assert.Single(fixture.Details()));
    }

    [Fact]
    public void MissingCachePreservesLegacyRiseAndNormalLines()
    {
        using var fixture = new Fixture();
        foreach (var total in new[] { 3000, 3300, 3600, 3900, 4100 }) fixture.Append(total, null);
        fixture.Evaluate();
        Assert.Equal("HOST_HEALTH_PAGED_POOL_HIGH paged_pool_mb=4100 rise_mb=1100 records=5 " +
            Remedy, Assert.Single(fixture.Details()));
        fixture.Append(3500, null);
        fixture.Evaluate();
        Assert.Equal("HOST_HEALTH_PAGED_POOL_NORMAL paged_pool_mb=3500", fixture.Details()[1]);
    }

    [Theory]
    [InlineData(1808, true)]
    [InlineData(2000, false)]
    [InlineData(10000, false)]
    public void HighWaterUsesResidualOnly(double cache, bool alerts)
    {
        using var fixture = new Fixture();
        fixture.Append(10000, cache);
        fixture.Evaluate();
        if (alerts)
            Assert.Equal("HOST_HEALTH_PAGED_POOL_HIGH paged_pool_mb=10000 rise_mb=0 records=1 " +
                "file_cache_mb=1808 residual_mb=8192 " + Remedy, Assert.Single(fixture.Details()));
        else
            Assert.Empty(fixture.Details());
    }

    [Fact]
    public void RecoveryReportsCacheWithoutResidual()
    {
        using var fixture = new Fixture();
        fixture.Append(10000, 1808);
        fixture.Evaluate();
        fixture.Append(10000, 2000);
        fixture.Evaluate();
        Assert.Equal("HOST_HEALTH_PAGED_POOL_NORMAL paged_pool_mb=10000 file_cache_mb=2000", fixture.Details()[1]);
    }

    [Fact]
    public void MixedWindowFallsBackPerRecord()
    {
        using var fixture = new Fixture();
        fixture.Append(1200, null);
        fixture.Append(3500, 2000);
        fixture.Append(3800, 2000);
        fixture.Append(4100, 2000);
        fixture.Append(4300, 2000);
        fixture.Evaluate();
        Assert.Contains("rise_mb=1100 records=5 file_cache_mb=2000 residual_mb=2300", Assert.Single(fixture.Details()));
    }

    [Fact]
    public void FallingResidualBreaksMonotonicRise()
    {
        using var fixture = new Fixture();
        fixture.Append(2200, 1000);
        fixture.Append(2500, 1000);
        fixture.Append(2800, 2000);
        fixture.Append(3100, 1000);
        fixture.Append(3400, 1000);
        fixture.Evaluate();
        Assert.Empty(fixture.Details());
    }

    [Fact]
    public void SamplingSkewClampsNegativeResidual()
    {
        using var fixture = new Fixture();
        fixture.Append(1000, 2000);
        fixture.Append(1000, 2000);
        fixture.Append(1000, 2000);
        fixture.Append(1000, 2000);
        fixture.Append(1000, 500);
        fixture.Evaluate();
        Assert.Empty(fixture.Details());
    }

    private sealed class UnavailableReader : IForegroundLockReader
    {
        public ForegroundLockReading Read() => ForegroundLockReading.Unavailable("test-unavailable");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "host-health-cache", Guid.NewGuid().ToString("N"));
        private string Ledger => Path.Combine(Root, "host-health.jsonl");
        private string Log => Path.Combine(Root, "events.log");
        internal string State => Path.Combine(Root, ConductorHostHealthMonitor.StateFileName);
        internal Fixture() => Directory.CreateDirectory(Root);
        internal void Append(double total, double? cache) => GateHostHealthLedger.Append(Ledger,
            new(DateTimeOffset.UnixEpoch, Guid.NewGuid().ToString("N"), 60, total, FileCachePagedPoolMb: cache));
        internal void Evaluate() => new ConductorHostHealthMonitor(Ledger, State, new ConductEventLogWriter(Log),
            signals: new(new UnavailableReader(), new FixedClock())).Evaluate();
        internal string[] Details() => File.Exists(Log) ? File.ReadAllLines(Log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("detail").GetString()!;
        }).ToArray() : [];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
