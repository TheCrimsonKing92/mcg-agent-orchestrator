using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel safe: pure in-memory records and fixed instants.
public sealed class LaneReuseShadowReportTests
{
    private static readonly DateTimeOffset Since = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddDays(7);

    [Fact]
    public void ThreeGatesAttributeExactCountsSecondsReasonFamiliesAndOrderedMisses()
    {
        var at = Since.AddDays(5);
        LaneReuseShadowRecord[] records =
        [
            new("goal-z", "attempt-a", at, [
                Miss("z", 2000, "red-z"),
                Miss("a", 9000, "red-a") with { Executed = false, FlakeConfirmed = true },
                MustRun("built-z", "always-affected:built-binary:Z", 3000)]),
            new("goal-a", "attempt-z", at, [
                Miss("beta", 1000, "red-beta"),
                MustRun("process-z", "always-affected:process-spawning:Z", 8000) with { Executed = false },
                MustRun("changed-z", "changed-test-class:Z", 4000)]),
            new("goal-a", "attempt-a", at.ToOffset(TimeSpan.FromHours(-5)), [
                Miss("omega", null, "red-omega"),
                MustRun("built-a", "always-affected:built-binary:A", 500),
                MustRun("process-a", "always-affected:process-spawning:A", 500),
                MustRun("changed-a", "changed-test-class:A", 1000),
                MustRun("unspecified", null, null),
                MustRun("unresolved", "lane-membership-unresolved", null)])
        ];
        var report = LaneReuseShadowReport.Build(records, Since, Until, 2, 3);

        Assert.Equal(3, report.Gates);
        Assert.Equal(12, report.LaneRows);
        Assert.Equal(4, report.WouldReuseRows);
        Assert.Equal(0.3333, report.WouldReuseShare);
        Assert.Equal(12, report.ExecutedLaneSeconds);
        Assert.Equal(3, report.SavedLaneSeconds);
        Assert.Equal(0.25, report.SavedShare);
        Assert.Equal(new[] {
            new LaneReuseShadowReasonCount("always-affected:built-binary", 2),
            new LaneReuseShadowReasonCount("always-affected:process-spawning", 2),
            new LaneReuseShadowReasonCount("changed-test-class", 2),
            new LaneReuseShadowReasonCount("lane-membership-unresolved", 1),
            new LaneReuseShadowReasonCount("unspecified", 1)
        }, report.MustRunReasons);
        Assert.Equal(new[] {
            ("goal-a", "attempt-a", "omega", "red-omega"),
            ("goal-a", "attempt-z", "beta", "red-beta"),
            ("goal-z", "attempt-a", "a", "red-a"),
            ("goal-z", "attempt-a", "z", "red-z")
        }, report.Misses.Select(m => (m.GoalId, m.AttemptId, m.Lane, m.MissReason)));
        Assert.All(report.Misses, miss =>
        {
            Assert.Equal(at, miss.RecordedAt);
            Assert.Equal(TimeSpan.Zero, miss.RecordedAt.Offset);
            Assert.Equal("unaffected", miss.Reason);
            Assert.Equal("reference-goal/reference-attempt", miss.ReferenceSource);
            Assert.Equal("verdict-is-green", miss.FailedPredicate);
            Assert.Equal(new[] { "FailingClass" }, miss.FailingClasses);
        });
        Assert.Equal(1, report.FlakeConfirmedMisses);
        Assert.True(report.Misses[2].FlakeConfirmed);
        Assert.Equal(2, report.UntimedRecords);
        Assert.Equal(3, report.UnreadableRecords);

        // Removing rows that were never executed cannot change either seconds total.
        var onlyExecuted = records.Select(r => r with { Lanes = r.Lanes.Where(l => l.Executed).ToArray() });
        var executedReport = LaneReuseShadowReport.Build(onlyExecuted, Since, Until);
        Assert.Equal(report.ExecutedLaneSeconds, executedReport.ExecutedLaneSeconds);
        Assert.Equal(report.SavedLaneSeconds, executedReport.SavedLaneSeconds);
    }

    [Fact]
    public void WindowIsHalfOpenAndMissTimeOrdersBeforeGateAndLane()
    {
        var report = LaneReuseShadowReport.Build([
            new("a", "a", Until, [Miss("end", 1, "excluded")]),
            new("a", "a", Since.AddTicks(-1), [Miss("before", 1, "excluded")]),
            new("a", "a", Since.AddDays(1), [Miss("later", 1000, "later")]),
            new("z", "z", Since, [Miss("earlier", 1000, "earlier")])
        ], Since, Until);
        Assert.Equal(2, report.Gates);
        Assert.Equal(2, report.LaneRows);
        Assert.Equal(1, report.WouldReuseShare);
        Assert.Equal(2, report.ExecutedLaneSeconds);
        Assert.Equal(2, report.SavedLaneSeconds);
        Assert.Equal(1, report.SavedShare);
        Assert.Equal(new[] { "earlier", "later" }, report.Misses.Select(m => m.Lane));
    }

    [Fact]
    public void EmptyRowsAndUnexecutedRowsHaveZeroSharesAndSeconds()
    {
        var empty = LaneReuseShadowReport.Build([], Since, Until);
        Assert.Equal(0, empty.Gates);
        Assert.Equal(0, empty.LaneRows);
        Assert.Equal(0, empty.WouldReuseRows);
        Assert.Equal(0, empty.WouldReuseShare);
        Assert.Equal(0, empty.ExecutedLaneSeconds);
        Assert.Equal(0, empty.SavedLaneSeconds);
        Assert.Equal(0, empty.SavedShare);
        Assert.Empty(empty.MustRunReasons);
        Assert.Empty(empty.Misses);
        Assert.Equal(0, empty.FlakeConfirmedMisses);
        var unexecuted = LaneReuseShadowReport.Build([
            new("g", "a", Since, [new("l", "would-reuse", "unaffected", false, 5000)])
        ], Since, Until);
        Assert.Equal(0, unexecuted.ExecutedLaneSeconds);
        Assert.Equal(0, unexecuted.SavedLaneSeconds);
        Assert.Equal(0, unexecuted.SavedShare);
        Assert.Throws<ArgumentException>(() => LaneReuseShadowReport.Build([], Until, Since));
    }

    private static LaneReuseShadowLaneRow MustRun(string lane, string? reason, long? duration) =>
        new(lane, "must-run", reason, true, duration);

    private static LaneReuseShadowLaneRow Miss(string lane, long? duration, string reason) =>
        new(lane, "would-reuse", "unaffected", true, duration, true, reason,
            "reference-goal/reference-attempt", false, "verdict-is-green", ["FailingClass"]);
}
