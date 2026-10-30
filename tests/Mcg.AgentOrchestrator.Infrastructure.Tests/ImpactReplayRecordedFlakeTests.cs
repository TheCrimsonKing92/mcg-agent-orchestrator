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

    [Fact]
    public void LedgerUsesExecutionRootWhenAttemptStateLivesElsewhere()
    {
        using var fixture = new ImpactReplayFixture(separateStateDirectory: true);
        fixture.AddAttempt("moved-state", failingBeta: true);
        fixture.AddFlake("moved-state");
        Assert.True(File.Exists(fixture.FlakeLedgerPath));

        var (report, _) = fixture.Run();

        var failure = Assert.Single(Assert.Single(report.Attempts).FailingClasses);
        Assert.Equal("lane-flakes", failure.FlakeEvidence);
        Assert.True(failure.IsFlakyFailureInSkippedLane);
        Assert.False(failure.IsMiss);
    }

    [Fact]
    public void UnreadableLedgerReportsDiagnosticAndKeepsMiss()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("locked-ledger", failingBeta: true);
        fixture.AddFlake("locked-ledger");
        using var ledgerLock = new FileStream(fixture.FlakeLedgerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var (report, markdown) = fixture.Run(allowSkippedInputs: true);

        var diagnostic = Assert.Single(report.SkippedInputs);
        Assert.Equal("lane-flakes.jsonl", diagnostic.Source);
        Assert.StartsWith("Flake evidence could not be read:", diagnostic.Reason);
        Assert.True(Assert.Single(Assert.Single(report.Attempts).FailingClasses).IsMiss);
        Assert.Contains("Flake evidence could not be read:", markdown);
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

    [Theory]
    [InlineData("Passed", "Completed", false, false, true)]
    [InlineData(null, "Completed", false, false, false)]
    [InlineData("NotExecuted", "Completed", false, false, false)]
    [InlineData("Passed", "Aborted", false, false, false)]
    [InlineData("Passed", null, false, false, false)]
    [InlineData("Passed", "Completed", true, false, false)]
    [InlineData("Passed", "Completed", false, true, false)]
    [InlineData(null, "Completed", false, true, false)]
    public void RerunRequiresCompletedPassingEvidenceForTheFailedMethod(
        string? failedMethodOutcome, string? summaryOutcome, bool incompleteCounters,
        bool confirmedFailure, bool expectedFlake)
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("positive-evidence", failingBeta: true, rerunBetaPass: true);
        var results = new List<(string Method, string Outcome)> { ("Unrelated", "Passed") };
        if (failedMethodOutcome is not null) results.Add(("Works", failedMethodOutcome));
        fixture.RewriteBetaRerun("positive-evidence", results.ToArray(), summaryOutcome,
            incompleteCounters ? results.Count + 1 : null);
        if (confirmedFailure)
            fixture.AddFlake("positive-evidence", outcome: AcceptanceLaneRerunEvidence.ConfirmedFailure);

        var (report, _) = fixture.Run();

        var failure = Assert.Single(Assert.Single(report.Attempts).FailingClasses);
        Assert.Equal(expectedFlake, failure.IsFlakyFailureInSkippedLane);
        Assert.Equal(!expectedFlake, failure.IsMiss);
        Assert.Equal(expectedFlake ? "rerun" : null, failure.FlakeEvidence);
    }

    [Fact]
    public void EveryOriginallyFailingMethodMustPassTheRerun()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("two-failures", failingBeta: true, rerunBetaPass: true);
        fixture.AddBetaFailure("two-failures", "AlsoFails");

        var (report, _) = fixture.Run();

        var failure = Assert.Single(Assert.Single(report.Attempts).FailingClasses);
        Assert.True(failure.IsMiss);
        Assert.Null(failure.FlakeEvidence);
    }

    [Theory]
    [InlineData("other-attempt", "Infrastructure tests: Beta lane", "BetaTests")]
    [InlineData("rerun", "Infrastructure tests: Alpha lane", "BetaTests")]
    [InlineData("rerun", "Infrastructure tests: Beta lane", "GammaTests")]
    public void UnmatchedConfirmedFailureDoesNotVetoCompletedRerun(
        string attemptId, string lane, string className)
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("rerun", failingBeta: true, rerunBetaPass: true);
        fixture.AddFlake(attemptId, lane, className, AcceptanceLaneRerunEvidence.ConfirmedFailure);

        var (report, _) = fixture.Run();

        var failure = Assert.Single(Assert.Single(report.Attempts).FailingClasses);
        Assert.False(failure.IsMiss);
        Assert.Equal("rerun", failure.FlakeEvidence);
    }
}
