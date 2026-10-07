using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceGateShardTimingBreakdownTestsRemoteLanes
{
    [Fact]
    public void RemoteSamples_AreCountedSeparatelyFromLocalTiming()
    {
        var emitted = new List<AcceptanceGateProgress>();
        using (var accountant = AcceptanceGatePhaseAccountant.Start(TimeProvider.System, "timing-goal", emitted.Add))
        {
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(750));
            accountant.RecordLaneExecution(TimeSpan.FromMilliseconds(900));
            accountant.RecordRemoteLaneSample(TimeSpan.FromSeconds(187.5));
            accountant.RecordRemoteLaneSample(TimeSpan.FromSeconds(42));
            accountant.MarkCompleted(passed: true);
        }

        var progress = Assert.Single(emitted);
        var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(progress.PhaseBreakdown);
        Assert.Equal(2, breakdown.RemoteLaneCount);
        Assert.Equal(TimeSpan.FromSeconds(187.5), breakdown.LongestRemoteLaneDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(750), breakdown.LongestLaneDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(900), breakdown.LaneExecutionDuration);
        Assert.Contains("remote_lanes=2;longest_remote_lane_ms=187500", progress.CurrentTarget, StringComparison.Ordinal);
        Assert.Contains("longest_lane_ms=750", progress.CurrentTarget, StringComparison.Ordinal);
        var fields = progress.CurrentTarget!.Split(';');
        var unattributedIndex = Array.FindIndex(fields, field => field.StartsWith("unattributed_ms=", StringComparison.Ordinal));
        Assert.True(unattributedIndex >= 0);
        Assert.Equal("remote_lanes=2", fields[unattributedIndex + 1]);
        Assert.Equal("longest_remote_lane_ms=187500", fields[unattributedIndex + 2]);
    }

    [Fact]
    public void LocalSamples_LeaveRemoteFieldsAbsent()
    {
        var emitted = new List<AcceptanceGateProgress>();
        using (var accountant = AcceptanceGatePhaseAccountant.Start(TimeProvider.System, "timing-goal", emitted.Add))
        {
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(400));
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(750));
            accountant.MarkCompleted(passed: true);
        }

        var progress = Assert.Single(emitted);
        var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(progress.PhaseBreakdown);
        Assert.Null(breakdown.RemoteLaneCount);
        Assert.Null(breakdown.LongestRemoteLaneDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(750), breakdown.LongestLaneDuration);
        Assert.DoesNotContain("remote_lanes=", progress.CurrentTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("longest_remote_lane_ms=", progress.CurrentTarget, StringComparison.Ordinal);
    }
}
