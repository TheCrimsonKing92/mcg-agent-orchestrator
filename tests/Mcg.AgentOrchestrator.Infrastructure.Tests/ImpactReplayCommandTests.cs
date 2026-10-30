// Parallel-safe integration tests: Git, receipts, reports and detached checkouts belong to each fixture.
public sealed class ImpactReplayCommandTests
{
    [Fact]
    public void OneSourceChangeSelectsDependentAndFloorLanesAndReportsSavings()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("one-source");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("one-source", attempt.AttemptId);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "src/Sample/Alpha.cs" }, attempt.ChangedFiles);
        Assert.Equal(new[] { "AlphaTests" }, attempt.SelectedTestClasses);
        Assert.Equal(new[] { "Alpha lane", "Beta lane", "Chaos gate" }, attempt.RanLanes);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(23.5, attempt.EstimatedSerialSecondsSaved);
        Assert.Equal(1.0 / 3, attempt.ShareOfLanesSkipped);
        Assert.Equal("default", report.FloorSource);
        Assert.Equal(new[] { "Chaos gate" }, report.FloorLanes);
        Assert.All(attempt.LaneOutcomes, lane => Assert.Equal("Passed", lane.Outcome));
        Assert.Empty(attempt.FailingClasses);
        Assert.True(attempt.MissCheckComplete);
        Assert.Contains("Attempts replayed: 1", markdown);
        Assert.Contains("Total estimated lane-seconds saved: 23.5", markdown);
        Assert.Contains("Median share of lanes skipped: 33.3", markdown);
        Assert.Contains("P90 share of lanes skipped: 33.3", markdown);
    }

    [Fact]
    public void FailureInSkippedLaneIsReportedAsMissWithAttemptClassAndLane()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("miss-attempt", failingBeta: true);

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        var failure = Assert.Single(attempt.FailingClasses);
        Assert.Equal("SampleTests.BetaTests", failure.ClassName);
        Assert.Equal("Beta lane", failure.Lane);
        Assert.Equal(false, failure.WouldRun);
        Assert.True(failure.IsMiss);
        Assert.True(attempt.MissCheckComplete);
        Assert.Equal("Failed", Assert.Single(attempt.LaneOutcomes, lane => lane.Lane == "Beta lane").Outcome);
        Assert.Contains("| miss-attempt | SampleTests.BetaTests | Beta lane |", markdown);
    }

    [Fact]
    public void SixSourceChangesAreCountedAsAbandonedWithoutSkippingAnyLanes()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 6);
        fixture.AddAttempt("over-cap");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(6, attempt.ChangedFiles.Count);
        Assert.Equal("Abandoned", attempt.SelectionOutcome);
        Assert.Equal("ChangedSourceFileCount", attempt.DegradationKind);
        Assert.Empty(attempt.SkippedLanes);
        Assert.Equal(attempt.RanLanes.ToArray(), attempt.WouldRunLanes.ToArray());
        Assert.Equal(0, attempt.EstimatedSerialSecondsSaved);
        Assert.Equal(0, attempt.ShareOfLanesSkipped);
        Assert.Contains("- ChangedSourceFileCount: 1", markdown);
        Assert.Contains("Total estimated lane-seconds saved: 0", markdown);
    }

    [Fact]
    public void CommandOnlyWritesRequestedReportsAndRemovesDetachedWorktrees()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("read-only");
        fixture.Write(".orchestrator/state.db", "opaque state sentinel");
        fixture.Write(".orchestrator/nested/keep.txt", "unchanged receipt sentinel");
        var before = fixture.StateSnapshot();
        var worktreesBefore = fixture.Git("worktree", "list", "--porcelain");
        Assert.False(Directory.Exists(fixture.CheckoutParent));

        var (report, _) = fixture.Run();

        Assert.Single(report.Attempts);
        Assert.Equal(before, fixture.StateSnapshot());
        Assert.Equal(new[] { "impact-replay.json", "impact-replay.md" },
            Directory.GetFiles(fixture.OutputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(worktreesBefore, fixture.Git("worktree", "list", "--porcelain"));
        Assert.True(Directory.Exists(fixture.CheckoutParent));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.CheckoutParent));
        Assert.Empty(report.CleanupErrors);
    }
}
