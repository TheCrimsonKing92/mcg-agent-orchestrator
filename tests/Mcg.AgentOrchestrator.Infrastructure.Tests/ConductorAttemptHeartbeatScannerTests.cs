using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its heartbeat tree and fixes both observation and file times.
public sealed class ConductorAttemptHeartbeatScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("attempt-heartbeats-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = ConductorInvariantThresholds.GateIdleWhileReady;

    [Fact]
    public void Scan_RunningAttempts_SelectsNewestAndPreservesAllFiles()
    {
        Write("goal-a/first.heartbeat.json", "running", Now.AddMinutes(-2), Now);
        var second = Path.Combine(_root, "goal-b", "second.heartbeat.json");
        GateHeartbeatArtifacts.Write(second, new(null, "test", "remote", null, null, null,
            "RUNNING", Now, Now, Now.AddMinutes(-1), 0, 0, 0));
        File.SetLastWriteTimeUtc(second, Now.UtcDateTime);
        Write("goal-a/ignored.gate-heartbeat.json", "running", Now, Now);
        Write("goal-b/completed.heartbeat.json", "completed", Now, Now);
        Write("goal-b/stale.heartbeat.json", "running", Now, Now.AddMinutes(-11));
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(
            path => path, path => (Body: File.ReadAllText(path), WrittenAt: File.GetLastWriteTimeUtc(path)));

        Assert.Equal(Now.AddMinutes(-1), ConductorAttemptHeartbeatScanner.LatestProgress(_root, Now, Window));
        Assert.Equal(2, ConductorAttemptHeartbeatScanner.CountLive(_root, Now, Window));
        Assert.Equal(before.Count, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, expected) in before)
        {
            Assert.True(File.Exists(path));
            Assert.Equal(expected.Body, File.ReadAllText(path));
            Assert.Equal(expected.WrittenAt, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void Scan_MissingRoot_ReturnsEmptyWithoutCreatingIt()
    {
        var missing = Path.Combine(_root, "missing");
        Assert.Null(ConductorAttemptHeartbeatScanner.LatestProgress(missing, Now, Window));
        Assert.Equal(0, ConductorAttemptHeartbeatScanner.CountLive(missing, Now, Window));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Scan_WindowBoundaryAndMixedCase_UsesRecordedProgress()
    {
        var path = Path.Combine(_root, "boundary.heartbeat.json");
        var progress = Now.AddMinutes(-20);
        File.WriteAllText(path, JsonSerializer.Serialize(new { STATE = "Running", LASTPROGRESSAT = progress }));
        File.SetLastWriteTimeUtc(path, (Now - Window).UtcDateTime);
        Assert.Equal(progress, ConductorAttemptHeartbeatScanner.LatestProgress(_root, Now, Window));
        Assert.Equal(1, ConductorAttemptHeartbeatScanner.CountLive(_root, Now, Window));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"state\":\"running\"}")]
    [InlineData("{\"state\":\"running\",\"lastProgressAt\":null}")]
    [InlineData("{\"state\":\"running\",\"lastProgressAt\":\"invalid\"}")]
    [InlineData("{\"state\":\"running\",\"lastProgressAt\":\"2026-10-17T12:00:01Z\"}")]
    [InlineData("{\"state\":7,\"lastProgressAt\":\"2026-10-17T12:00:00Z\"}")]
    public void Scan_InvalidOrFutureSnapshot_DoesNotCount(string body)
    {
        var path = Path.Combine(_root, "invalid.heartbeat.json");
        File.WriteAllText(path, body);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        Assert.Null(ConductorAttemptHeartbeatScanner.LatestProgress(_root, Now, Window));
        Assert.Equal(0, ConductorAttemptHeartbeatScanner.CountLive(_root, Now, Window));
        Assert.Equal(body, File.ReadAllText(path));
    }

    [Fact]
    public void Scan_LockedFile_SkipsItAndKeepsOtherProgress()
    {
        var locked = Write("locked.heartbeat.json", "running", Now, Now);
        Write("available.heartbeat.json", "running", Now.AddMinutes(-1), Now);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal(Now.AddMinutes(-1), ConductorAttemptHeartbeatScanner.LatestProgress(_root, Now, Window));
        Assert.Equal(1, ConductorAttemptHeartbeatScanner.CountLive(_root, Now, Window));
        Assert.True(File.Exists(locked));
    }

    private string Write(string relative, string state, DateTimeOffset progress, DateTimeOffset written)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { kind = "solo", pid = 123,
            state, lastProgressAt = progress }));
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
