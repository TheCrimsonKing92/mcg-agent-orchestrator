using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its root and all overrides are scoped to its execution context.
public sealed class GateLaneTimeLedgerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllLocalAttemptRecordsSumWithoutDependingOnLaunchProbe(bool available)
    {
        var root = Directory.CreateTempSubdirectory("gate-lane-local-").FullName;
        using var probe = GateHostHealthProbe.PushProbe(() => new(
            available ? GateLoadSample.Available(250) : GateLoadSample.Unavailable("no-launch-reading"),
            GateLoadSample.Unavailable("no-pool-reading")));
        try
        {
            var time = new RecordingTimeProvider();
            var progress = new List<AcceptanceGateProgress>();
            var accountant = AcceptanceGatePhaseAccountant.Start(time, "goal", progress.Add);
            using (accountant)
            {
                accountant.BindHostHealthLedger(root, "attempt");
                accountant.RecordLaneSample(TimeSpan.FromMilliseconds(400));
                accountant.RecordLaneSample(TimeSpan.FromMilliseconds(750));
                time.Advance(TimeSpan.FromMilliseconds(1000));
                accountant.MarkCompleted(true);
            }
            accountant.Dispose(); // The attempt must never be recorded twice.
            var record = ReadSingle(root);
            Assert.Equal(1150d, record.LocalLaneMs);
            Assert.Equal(0d, record.RemoteLaneMs);
            Assert.Equal(0, record.RemoteLaneCount);
            Assert.Equal(1000d, record.TotalMs);
            Assert.Equal("attempt", record.GateAttemptId);
            Assert.Equal("goal", record.GoalId);
            Assert.Equal("completed", record.Outcome);
            Assert.Equal(1, record.Version);
            Assert.Equal(time.GetUtcNow(), record.ObservedAt);
            Assert.Equal(TimeSpan.FromMilliseconds(750), Assert.Single(progress).PhaseBreakdown!.LongestLaneDuration);
            Assert.Equal(available, File.Exists(GateHostHealthLedger.ResolveStorePath(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LocalAndRemoteSumsRemainSeparateFromLongestLaneDurations()
    {
        var root = Directory.CreateTempSubdirectory("gate-lane-mixed-").FullName;
        using var probe = GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Unavailable("no-launch-reading"), GateLoadSample.Unavailable("no-pool-reading")));
        try
        {
            var progress = new List<AcceptanceGateProgress>();
            using (var accountant = AcceptanceGatePhaseAccountant.Start(new RecordingTimeProvider(), "goal", progress.Add))
            {
                accountant.BindHostHealthLedger(root, "attempt");
                accountant.RecordLaneSample(TimeSpan.FromMilliseconds(300));
                accountant.RecordLaneSample(TimeSpan.FromMilliseconds(900));
                accountant.RecordRemoteLaneSample(TimeSpan.FromMilliseconds(2000));
                accountant.RecordRemoteLaneSample(TimeSpan.FromMilliseconds(5000));
                accountant.MarkCompleted(true);
            }
            var record = ReadSingle(root);
            Assert.Equal(1200d, record.LocalLaneMs);
            Assert.Equal(7000d, record.RemoteLaneMs);
            Assert.Equal(2, record.RemoteLaneCount);
            var breakdown = Assert.Single(progress).PhaseBreakdown!;
            Assert.Equal(TimeSpan.FromMilliseconds(900), breakdown.LongestLaneDuration);
            Assert.Equal(TimeSpan.FromMilliseconds(5000), breakdown.LongestRemoteLaneDuration);
            Assert.Equal(2, breakdown.RemoteLaneCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnboundAttemptCreatesNoLedger()
    {
        using var probe = GateHostHealthProbe.PushProbe(() => new(
            GateLoadSample.Unavailable("no-launch-reading"), GateLoadSample.Unavailable("no-pool-reading")));
        var calls = 0;
        using var appender = GateLaneTimeLedger.PushAppender((_, _) => calls++);
        var progress = new List<AcceptanceGateProgress>();
        using (var accountant = AcceptanceGatePhaseAccountant.Start(new RecordingTimeProvider(), "goal", progress.Add))
            accountant.MarkCompleted(true);
        Assert.Single(progress);
        Assert.Equal(0, calls);
    }

    private static GateLaneTimeLedgerRecord ReadSingle(string root) =>
        JsonSerializer.Deserialize<GateLaneTimeLedgerRecord>(
            Assert.Single(File.ReadAllLines(Path.Combine(root, ".orchestrator", "gate-lane-time.jsonl"))),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
