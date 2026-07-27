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

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_allows_open_set_growth_from_a_new_identity_at_a_new_anchor")]
    public void ReviewFindingConvergenceAllowsOpenSetGrowthFromNewIdentityAtNewAnchor()
    {
        // A reviewer discovering a genuinely new defect late must be able to report it, even when the
        // open set grows. Rejecting this trapped reviewers on 2026-07-25: structured reports failed the
        // old open-set-increase check, prose-only needs-work fails the no-open-findings rule, and pass
        // would be dishonest. Re-litigation is still blocked by the anchor-identity guards.
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

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(2, ReviewFindingConvergence.CountOpen(state));
        Assert.Contains(state, finding => finding.StableId == "F-2" &&
            finding.State == ReviewFindingState.Open);
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

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_rejects_recycled_stable_id_at_open_anchor")]
    public void ReviewFindingConvergenceRejectsRecycledStableIdAtOpenAnchor()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, anchor, "Original issue.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding("F-1", ReviewFindingState.Open, anchor, "Original issue."),
                new ReviewFinding("F-RECYCLED", ReviewFindingState.Open, anchor, "Same anchor, new identity.")
            ],
            [anchor]);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.RecycledAnchorIdentityViolationCode, error.Code);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_allows_new_stable_id_at_resolved_anchor")]
    public void ReviewFindingConvergenceAllowsNewStableIdAtResolvedAnchor()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Resolved, anchor, "Original issue, fixed.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-NEW", ReviewFindingState.Open, anchor, "Genuinely new defect at the fixed site.")],
            [anchor]);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(1, ReviewFindingConvergence.CountOpen(state));
        Assert.Equal(ReviewFindingState.Open, state.Single(finding => finding.StableId == "F-NEW").State);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_tolerates_hunk_drift_on_open_finding_and_refreshes_location")]
    public void ReviewFindingConvergenceToleratesHunkDriftOnOpenFindingAndRefreshesLocation()
    {
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run", "1392-1412"), "Missing guard.")
        };
        var drifted = new ReviewFindingLocation("src/A.cs", "A.Run", "1403-1412");
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-1", ReviewFindingState.Open, drifted, "Missing guard.")],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(1, ReviewFindingConvergence.CountOpen(state));
        Assert.Equal(drifted, state.Single(finding => finding.StableId == "F-1").Location);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_allows_new_stable_id_in_same_region_at_different_hunk")]
    public void ReviewFindingConvergenceAllowsNewStableIdInSameRegionAtDifferentHunk()
    {
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run", "guard"), "First defect.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run", "guard"), "First defect."),
                new ReviewFinding("F-2", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run", "dispose"), "Second, distinct defect in the same region.")
            ],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(2, ReviewFindingConvergence.CountOpen(state));
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_allows_resolved_finding_reported_at_moved_anchor")]
    public void ReviewFindingConvergenceAllowsResolvedFindingReportedAtMovedAnchor()
    {
        var openedAt = new ReviewFindingLocation("tests/DashboardRenderingTests.cs", "line-1621 assertion");
        var fixedAt = new ReviewFindingLocation("tests/DashboardRenderingTests.cs", "operator-controls catalog assertions", "CanEmitOperatorControls");
        var previous = new[]
        {
            new ReviewFinding("REV-001", ReviewFindingState.Open, openedAt, "Stale assertion.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("REV-001", ReviewFindingState.Resolved, fixedAt, "Assertion replaced; focused tests pass.")],
            [fixedAt]);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(0, ReviewFindingConvergence.CountOpen(state));
        Assert.Equal(openedAt, state.Single(finding => finding.StableId == "REV-001").Location);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_rejects_still_open_finding_reported_at_moved_anchor")]
    public void ReviewFindingConvergenceRejectsStillOpenFindingReportedAtMovedAnchor()
    {
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/A.cs", "A.Run"), "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-1", ReviewFindingState.Open, new ReviewFindingLocation("src/B.cs", "B.Run"), "Missing guard.")],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, error.Code);
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
