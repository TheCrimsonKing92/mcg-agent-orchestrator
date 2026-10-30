using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns all Git and filesystem state through its fixture.
public sealed class ImpactReplayRecordedFlakeTests
{
    [Fact]
    public void RecordedFlakeOnlyReclassifiesMatchingSkippedFailure()
    {
        using var fixture = new ImpactReplayFixture(extraBetaClass: true);
        fixture.AddAttempt("recorded-flake", failingBeta: true);
        fixture.AddFlake("recorded-flake");
        fixture.AddFlake("recorded-flake");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(new[] { "AlphaTests" }, attempt.SelectedTestClasses);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        var flake = Assert.Single(attempt.FailingClasses, failure => failure.IsFlakyFailureInSkippedLane);
        Assert.Equal("SampleTests.BetaTests", flake.ClassName);
        Assert.Equal("Beta lane", flake.Lane);
        Assert.Equal(false, flake.WouldRun);
        Assert.Equal("lane-flakes", flake.FlakeEvidence);
        Assert.False(flake.IsMiss);
        var miss = Assert.Single(attempt.FailingClasses, failure => failure.IsMiss);
        Assert.Equal("SampleTests.GammaTests", miss.ClassName);
        Assert.Null(miss.FlakeEvidence);
        Assert.True(attempt.MissCheckComplete);
        var misses = markdown[markdown.IndexOf("## Misses", StringComparison.Ordinal)..
            markdown.IndexOf("## Flaky failures in skipped lanes", StringComparison.Ordinal)];
        Assert.DoesNotContain("SampleTests.BetaTests", misses);
        Assert.Contains("| recorded-flake | SampleTests.GammaTests | Beta lane |", misses);
        Assert.Contains("| recorded-flake | SampleTests.BetaTests | Beta lane | lane-flakes |", markdown);
        Assert.Equal(2, markdown.Split("| recorded-flake | SampleTests.BetaTests |", StringSplitOptions.None).Length);
    }

    [Theory]
    [InlineData("other-attempt", "Infrastructure tests: Beta lane", "BetaTests", AcceptanceLaneRerunEvidence.Flake)]
    [InlineData("unmatched", "Infrastructure tests: Alpha lane", "BetaTests", AcceptanceLaneRerunEvidence.Flake)]
    [InlineData("unmatched", "Infrastructure tests: Beta lane", "GammaTests", AcceptanceLaneRerunEvidence.Flake)]
    [InlineData("unmatched", "Infrastructure tests: Beta lane", "BetaTests", AcceptanceLaneRerunEvidence.ConfirmedFailure)]
    public void UnmatchedOrConfirmedFailureRowsKeepTheMiss(
        string attemptId, string lane, string className, string outcome)
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("unmatched", failingBeta: true);
        fixture.AddFlake(attemptId, lane, className, outcome);

        var (report, markdown) = fixture.Run();

        var failure = Assert.Single(Assert.Single(report.Attempts).FailingClasses);
        Assert.True(failure.IsMiss);
        Assert.False(failure.IsFlakyFailureInSkippedLane);
        Assert.Null(failure.FlakeEvidence);
        Assert.DoesNotContain("## Flaky failures in skipped lanes", markdown);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void RerunMustPassAfterFailureWithinTheSameLane(
        bool earlierPass, bool rerunFailure, bool otherLanePass, bool expectedFlake)
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("rerun", failingBeta: true,
            rerunBetaPass: !earlierPass && !rerunFailure && !otherLanePass,
            earlierBetaPass: earlierPass, rerunBetaFailure: rerunFailure, rerunOtherLanePass: otherLanePass);

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        var failure = Assert.Single(attempt.FailingClasses);
        Assert.Equal("SampleTests.BetaTests", failure.ClassName);
        Assert.Equal("Beta lane", failure.Lane);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(expectedFlake, failure.IsFlakyFailureInSkippedLane);
        Assert.Equal(!expectedFlake, failure.IsMiss);
        Assert.Equal(expectedFlake ? "rerun" : null, failure.FlakeEvidence);
        Assert.Equal(expectedFlake, markdown.Contains("## Flaky failures in skipped lanes", StringComparison.Ordinal));
    }
}
