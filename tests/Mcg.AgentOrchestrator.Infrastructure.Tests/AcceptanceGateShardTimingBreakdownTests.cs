using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceGateShardTimingBreakdownTests
{
    [Fact]
    public void FakeLaneSamples_AreCarriedByBreakdownAndCompactAuditLine()
    {
        var emitted = new List<AcceptanceGateProgress>();
        using (var accountant = AcceptanceGatePhaseAccountant.Start(
                   TimeProvider.System,
                   "timing-goal",
                   emitted.Add))
        {
            accountant.RecordSlotWait(TimeSpan.FromMilliseconds(125));
            accountant.RecordLaneScheduling(effectiveConcurrency: 3, peakConcurrency: 2);
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(400));
            accountant.RecordLaneSample(TimeSpan.FromMilliseconds(750));
            accountant.RecordLaneExecution(TimeSpan.FromMilliseconds(900));
            accountant.MarkCompleted(passed: true);
        }

        var progress = Assert.Single(emitted);
        var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(progress.PhaseBreakdown);
        Assert.Equal(3, breakdown.EffectiveShardConcurrency);
        Assert.Equal(2, breakdown.PeakShardConcurrency);
        Assert.Equal(TimeSpan.FromMilliseconds(750), breakdown.LongestLaneDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(125), breakdown.SlotWaitDuration);
        Assert.Contains("shard_concurrency_effective=3", progress.CurrentTarget, StringComparison.Ordinal);
        Assert.Contains("longest_lane_ms=750", progress.CurrentTarget, StringComparison.Ordinal);
        Assert.Contains("slot_wait_ms=125", progress.CurrentTarget, StringComparison.Ordinal);
    }
}
