using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every file is under an owned root; all probe overrides are AsyncLocal.
public sealed class WorkerDispatchHeartbeatCensusTests
{
    [Fact]
    public void Count_MixedHeartbeats_OnlyFreshActiveWorkersCount()
    {
        var root = Directory.CreateTempSubdirectory("heartbeat-census-").FullName;
        var now = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        try
        {
            Write(root, "running", "worker", "running", now, now);
            Write(root, "launched", "WORKER", "LAUNCHED", now, now);
            Write(root, "prep", "prep", "running", now, now);
            Write(root, "exited", "worker", "exited", now, now);
            Write(root, "old-observation", "worker", "running", now.AddMinutes(-3), now);
            Write(root, "stale-file", "worker", "running", now, now.AddMinutes(-3));
            Write(root, "boundary", "worker", "running", now.AddMinutes(-2), now.AddMinutes(-2));
            Write(root, "future", "worker", "running", now.AddMinutes(1), now.AddMinutes(1));
            Write(root, "unreadable", "worker", "running", now, now);
            foreach (var (name, content) in new[] { ("malformed", "{"), ("empty", ""), ("wrong-type", "{\"kind\":3}") })
            {
                var path = Path.Combine(root, name + ".heartbeat.json");
                File.WriteAllText(path, content);
                File.SetLastWriteTimeUtc(path, now.UtcDateTime);
            }
            File.WriteAllText(Path.Combine(root, "ignored.json"), "{}");
            var opened = new List<string>();
            var sample = WorkerDispatchHeartbeatCensus.Count(root, now, path =>
            {
                opened.Add(Path.GetFileName(path));
                if (path.EndsWith("unreadable.heartbeat.json", StringComparison.Ordinal)) throw new IOException();
                return File.ReadAllText(path);
            });
            Assert.True(sample.IsAvailable);
            Assert.Equal(4d, sample.Value);
            Assert.Equal(11, opened.Count);
            Assert.DoesNotContain("stale-file.heartbeat.json", opened);
            Assert.DoesNotContain("ignored.json", opened);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Count_MissingDirectory_ReportsUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), "missing-heartbeats-" + Guid.NewGuid().ToString("N"));
        var sample = WorkerDispatchHeartbeatCensus.Count(path, DateTimeOffset.UtcNow);
        Assert.False(sample.IsAvailable);
        Assert.Null(sample.Value);
        Assert.Equal("log-directory-missing", sample.UnavailableReason);
    }

    [Fact]
    public void Capture_BoundDirectory_ReportsCountAndHonorsOverrides()
    {
        var root = Directory.CreateTempSubdirectory("heartbeat-load-").FullName;
        using var gates = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
        using var cpu = GateLoadContextProbe.PushHostCpuProbe(() => new(10, 4, 1000));
        try
        {
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 3; index++) Write(root, index.ToString(), "worker", "running", now, now);
            using (GateLoadContextProbe.PushHostLogDirectory(root))
            {
                var context = GateLoadContextProbe.Capture(1);
                Assert.True(context.InFlightPaidWorkerDispatchCount.IsAvailable);
                Assert.Equal(3d, context.InFlightPaidWorkerDispatchCount.Value);
                Assert.Contains("load_inflight_paid_workers=3", GateLoadContextProbe.FormatProgressTokens(context));
                using var workers = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 5);
                Assert.Equal(5d, GateLoadContextProbe.Capture(1).InFlightPaidWorkerDispatchCount.Value);
            }
            using (GateLoadContextProbe.PushHostLogDirectory(null))
            {
                var sample = GateLoadContextProbe.Capture(1).InFlightPaidWorkerDispatchCount;
                Assert.False(sample.IsAvailable);
                Assert.Equal("host-log-directory-unbound", sample.UnavailableReason);
                using var workers = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 5);
                Assert.Equal(5d, GateLoadContextProbe.Capture(1).InFlightPaidWorkerDispatchCount.Value);
            }
            using (GateLoadContextProbe.PushHostLogDirectory(Path.Combine(root, "missing")))
                Assert.Equal("log-directory-missing", GateLoadContextProbe.Capture(1).InFlightPaidWorkerDispatchCount.UnavailableReason);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Write(string root, string name, string kind, string state, DateTimeOffset observed, DateTimeOffset written)
    {
        var path = Path.Combine(root, name + ".heartbeat.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { kind, state, lastObservedAt = observed }));
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
    }
}
