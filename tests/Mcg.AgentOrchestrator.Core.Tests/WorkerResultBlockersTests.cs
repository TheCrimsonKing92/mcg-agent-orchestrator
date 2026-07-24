using Mcg.AgentOrchestrator.Core;

public sealed class WorkerResultBlockersTests
{
    [Xunit.Fact(DisplayName = "TryFindReviewFindingRound_parses_structured_findings_and_touched_anchors")]
    public void TryFindReviewFindingRoundParsesStructuredFindingsAndTouchedAnchors()
    {
        var authoritativeAnchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            """
            WORKER_RESULT:
            findings: [{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard."}]
            touched_anchors: [{"file":"src/A.cs","region":"A.Run","hunk":"guard"}]
            verdict: needs-work
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow,
            ReviewFindingTouchedAnchors: [authoritativeAnchor]);

        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var diagnostic), diagnostic);
        var finding = Assert.Single(round.Findings);
        Assert.Equal("F-1", finding.StableId);
        Assert.Equal(ReviewFindingState.Open, finding.State);
        Assert.Equal("A.Run", Assert.Single(round.TouchedAnchors).Region);
    }

    [Xunit.Fact(DisplayName = "TryFindReviewFindingRound_ignores_reviewer_authored_touched_anchors")]
    public void TryFindReviewFindingRoundIgnoresReviewerAuthoredTouchedAnchors()
    {
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            """
            WORKER_RESULT:
            findings: [{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard."}]
            touched_anchors: [{"file":"src/A.cs","region":"A.Run","hunk":"guard"}]
            verdict: needs-work
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow);

        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var diagnostic), diagnostic);
        Assert.Empty(round.TouchedAnchors);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_rejects_untouched_resolved_reopen")]
    public void ReviewFindingConvergenceRejectsUntouchedResolvedReopen()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Resolved, anchor, "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-1", ReviewFindingState.Open, anchor, "Missing guard.")],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.UntouchedReopenViolationCode, error.Code);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_rejects_open_set_growth")]
    public void ReviewFindingConvergenceRejectsOpenSetGrowth()
    {
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run"), "A.")
        };
        var next = new ReviewFindingRound(
            [
                previous[0],
                new ReviewFinding("F-2", ReviewFindingState.Open, new ReviewFindingLocation("src/B.cs", "B.Run"), "B.")
            ],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.MonotonicityViolationCode, error.Code);
        Assert.Equal(1, error.PreviousOpenCount);
        Assert.Equal(2, error.NextOpenCount);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_allows_exact_anchor_regression_reopen")]
    public void ReviewFindingConvergenceAllowsExactAnchorRegressionReopen()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Resolved, anchor, "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding("F-1", ReviewFindingState.Open, anchor, "Missing guard.")
            ],
            [anchor]);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(1, ReviewFindingConvergence.CountOpen(state));
        Assert.Equal(ReviewFindingState.Open, state.Single(finding => finding.StableId == "F-1").State);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_allows_new_identity_when_total_open_does_not_grow")]
    public void ReviewFindingConvergenceAllowsNewIdentityWhenTotalOpenDoesNotGrow()
    {
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run"), "Old issue.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding("F-1", ReviewFindingState.Resolved, new ReviewFindingLocation("src/A.cs", "A.Run"), "Old issue."),
                new ReviewFinding("F-NEW", ReviewFindingState.Open, new ReviewFindingLocation("src/New.cs", "New.Run"), "New-code issue.")
            ],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(1, ReviewFindingConvergence.CountOpen(state));
        Assert.Equal(ReviewFindingState.Open, state.Single(finding => finding.StableId == "F-NEW").State);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_rejects_recycled_stable_id_at_existing_anchor")]
    public void ReviewFindingConvergenceRejectsRecycledStableIdAtExistingAnchor()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Resolved, anchor, "Original issue.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-RECYCLED", ReviewFindingState.Open, anchor, "Same anchor, new identity.")],
            [anchor]);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.RecycledAnchorIdentityViolationCode, error.Code);
    }

    [Xunit.Fact(DisplayName = "TryFindNeedsWorkVerdict_uses_open_structured_findings_when_blockers_is_none")]
    public void TryFindNeedsWorkVerdictUsesOpenStructuredFindingsWhenBlockersIsNone()
    {
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            0,
            """
            WORKER_RESULT:
            blockers: none
            findings: [{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run","hunk":"guard"},"description":"Missing guard."}]
            touched_anchors: []
            verdict: needs-work
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.True(WorkerResultBlockers.TryFindNeedsWorkVerdict(verification, out var blocker));
        Assert.Equal("Missing guard.", blocker);
    }

    [Xunit.Fact(DisplayName = "TryFindEvidenceRequest_reads_latest_worker_result_field")]
    public void TryFindEvidenceRequestReadsLatestWorkerResultField()
    {
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            """
            WORKER_RESULT:
            blockers: stale blocker
            evidence-request: Core.Tests: OldTests
            verdict: needs-work
            END_WORKER_RESULT
            WORKER_RESULT:
            blockers: missing focused evidence
            evidence-request: Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests
            verdict: needs-work
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.True(WorkerResultBlockers.TryFindEvidenceRequest(verification, out var request));
        Assert.Equal("Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests", request);
    }
}
