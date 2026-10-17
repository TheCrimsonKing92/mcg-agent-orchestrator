using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: each test owns its files and uses an injected clock.
public sealed class ConductorGateLaneProgressScannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T05:16:00Z");
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void LatestProgress_OperatorAndGoalFolders_ReturnsLargestBodyValue()
    {
        using var fixture = new Fixture();
        Write(fixture.Root, "operator/operator-gate-check.gate-heartbeat.json", Now.AddSeconds(-90), Now);
        Write(fixture.Root, fixture.Goals[0].Id.Value + "/train-check.gate-heartbeat.json", Now.AddSeconds(-20), Now);
        Assert.Equal(Now.AddSeconds(-20), ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    [Fact]
    public void LatestProgress_FreshWriteWithOlderBody_UsesBodyTimestamp()
    {
        using var fixture = new Fixture();
        Write(fixture.Root, "operator/check.gate-heartbeat.json", Now.AddMinutes(-7), Now);
        Assert.Equal(Now.AddMinutes(-7), ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    [Theory]
    [InlineData(-11, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void LatestProgress_StaleOrFutureEvidence_IsIgnored(int writeMinutes, int progressMinutes)
    {
        using var fixture = new Fixture();
        Write(fixture.Root, "check.gate-heartbeat.json", Now.AddMinutes(progressMinutes), Now.AddMinutes(writeMinutes));
        Assert.Null(ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    public void LatestProgress_WriteTimeAtWindowBoundary_IsIncluded(int writeMinutes)
    {
        using var fixture = new Fixture();
        Write(fixture.Root, "check.gate-heartbeat.json", Now.AddMinutes(-7), Now.AddMinutes(writeMinutes));
        Assert.Equal(Now.AddMinutes(-7), ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"lastProgressAt\":42}")]
    [InlineData("{\"lastProgressAt\":null}")]
    [InlineData("{\"lastProgressAt\":\"invalid\"}")]
    public void LatestProgress_MalformedBody_IsIgnored(string body)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "check.gate-heartbeat.json");
        File.WriteAllText(path, body);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        Assert.Null(ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    [Fact]
    public void LatestProgress_MissingRoot_ReturnsNull()
    {
        using var fixture = new Fixture();
        Assert.Null(ConductorGateLaneProgressScanner.LatestProgress(Path.Combine(fixture.Root, "missing"), Now, Window));
    }

    [Fact]
    public void LatestProgress_LockedFile_DoesNotHideReadableProgress()
    {
        using var fixture = new Fixture();
        var locked = Write(fixture.Root, "locked.gate-heartbeat.json", Now, Now);
        using var stream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
        Write(fixture.Root, "readable.gate-heartbeat.json", Now.AddMinutes(-1), Now);
        Assert.Equal(Now.AddMinutes(-1), ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    [Theory]
    [InlineData("2026-10-17T05:15:00")]
    [InlineData("2026-10-17T07:15:00+02:00")]
    public void LatestProgress_CaseAndOffsetVariants_ParseAsUtc(string timestamp)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "check.gate-heartbeat.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { LastProgressAt = timestamp, state = "finished" }));
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        Assert.Equal(Now.AddMinutes(-1), ConductorGateLaneProgressScanner.LatestProgress(fixture.Root, Now, Window));
    }

    private static string Write(string root, string name, DateTimeOffset progress, DateTimeOffset writeTime)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { lastProgressAt = progress.ToString("O", CultureInfo.InvariantCulture) }));
        File.SetLastWriteTimeUtc(path, writeTime.UtcDateTime);
        return path;
    }
}
