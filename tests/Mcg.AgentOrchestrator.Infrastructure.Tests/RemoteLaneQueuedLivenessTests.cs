using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: fixed inputs, with no external state or processes.
public sealed class RemoteLaneQueuedLivenessTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void Observe_UnreadableStaleAndUnlisted_RecordsDistinctReasons()
    {
        var tracker = new RemoteLaneQueuedLiveness("job", Start);
        Assert.Equal("not-listed", tracker.Summary().LastReason);
        Assert.Equal(0, tracker.Summary().Probes);
        Assert.Null(tracker.Observe(null, Start));
        Assert.Equal("unreadable", tracker.Summary().LastReason);
        Assert.Null(tracker.Observe(new(Start.AddSeconds(-121), 3, ["job"], []), Start));
        Assert.Equal("stale-state", tracker.Summary().LastReason);
        Assert.Null(tracker.Observe(new(Start.AddSeconds(121), 3, [], ["job"]), Start));
        Assert.Equal("stale-state", tracker.Summary().LastReason);
        Assert.Null(tracker.Observe(new(Start, 3, ["other"], []), Start));
        Assert.Equal("not-listed", tracker.Summary().LastReason);
        Assert.Equal(4, tracker.Summary().Probes);
        Assert.Equal(0, tracker.Summary().ListedProbes);
    }

    [Xunit.Fact]
    public void Observe_ListedJobs_CountsSeparatelyAndPrefersRunning()
    {
        var tracker = new RemoteLaneQueuedLiveness("job", Start);
        Assert.Equal(Start, tracker.Observe(new(Start, 3, [], ["JOB.JSON"]), Start));
        Assert.Equal("listed-queued", tracker.Summary().LastReason);
        Assert.Equal("queued", tracker.Summary().LastListedAs);
        var later = Start.AddSeconds(20);
        Assert.Equal(later, tracker.Observe(new(later, 3, ["job"], ["job"]), later));
        Assert.Equal("listed-running", tracker.Summary().LastReason);
        Assert.Equal("running", tracker.Summary().LastListedAs);
        Assert.Null(tracker.Observe(new(later, 3, [], []), later));
        var summary = tracker.Summary();
        Assert.Equal(3, summary.Probes);
        Assert.Equal(2, summary.ListedProbes);
        Assert.Equal(later, summary.LastListedAt);
        Assert.Equal(Start, summary.StartedAt);
        Assert.Equal(300, summary.CeilingSeconds);
    }

    [Xunit.Fact]
    public void ShouldProbe_CeilingInclusive_RecordsCeilingAfterward()
    {
        var tracker = new RemoteLaneQueuedLiveness("job", Start);
        Assert.Equal(TimeSpan.FromSeconds(300), RemoteLaneQueuedLiveness.Ceiling);
        Assert.Equal(TimeSpan.FromSeconds(120), RemoteLaneQueuedLiveness.StateFreshness);
        Assert.True(tracker.ShouldProbe(Start.AddSeconds(300)));
        Assert.False(tracker.ShouldProbe(Start.AddSeconds(301)));
        Assert.Equal("ceiling-reached", tracker.Summary().LastReason);
        Assert.Equal(0, tracker.Summary().Probes);
    }

    [Xunit.Fact]
    public void OwnHeartbeatSeen_FirstTimestampWins_StopsProbing()
    {
        var tracker = new RemoteLaneQueuedLiveness("job", Start);
        tracker.OwnHeartbeatSeen(Start.AddSeconds(20));
        tracker.OwnHeartbeatSeen(Start.AddSeconds(40));
        Assert.False(tracker.ShouldProbe(Start.AddSeconds(40)));
        Assert.False(tracker.ShouldProbe(Start.AddSeconds(301)));
        Assert.Equal("own-heartbeat", tracker.Summary().LastReason);
        Assert.Equal(Start.AddSeconds(20), tracker.Summary().OwnHeartbeatAt);
    }
}
