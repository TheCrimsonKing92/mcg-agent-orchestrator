using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

public sealed class WorkerResultBlockersTests
{
    [Xunit.Fact(DisplayName = "WorkerResultBlockers_parses_inconclusive_tests_and_preserves_receipt")]
    public void WorkerResultBlockersParsesInconclusiveTestsAndPreservesReceipt()
    {
        var verification = new TaskVerificationRecord(
            "test",
            "C:\\repo",
            0,
            "WORKER_RESULT:\ntests: inconclusive - command timed out; no TRX\nblockers: none\nEND_WORKER_RESULT",
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.True(WorkerResultBlockers.TryGetTestsStatus(verification, out var status));
        Assert.Equal(WorkerResultBlockers.TestsStatus.Inconclusive, status);
        Assert.True(WorkerResultBlockers.TryFindTests(verification, out var tests));
        Assert.Equal("inconclusive - command timed out; no TRX", tests);
        Assert.False(WorkerResultBlockers.TryFindFailingTests(verification, out _));
    }

    [Xunit.Theory(DisplayName = "WorkerResultBlockers_parses_only_canonical_premise_invalid_evidence")]
    [Xunit.InlineData("premise-invalid - required API does not exist; see src/Api.cs", true, "required API does not exist; see src/Api.cs")]
    [Xunit.InlineData("premise-invalid", false, "")]
    [Xunit.InlineData("goal premise may be invalid", false, "")]
    public void WorkerResultBlockersParsesOnlyCanonicalPremiseInvalidEvidence(
        string blockers,
        bool expected,
        string expectedEvidence)
    {
        var verification = new TaskVerificationRecord(
            "plan",
            "C:\\repo",
            0,
            $"WORKER_RESULT:\nblockers: {blockers}\nEND_WORKER_RESULT",
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.Equal(expected, WorkerResultBlockers.TryFindPremiseInvalidEvidence(verification, out var evidence));
        Assert.Equal(expectedEvidence, evidence);
    }

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

    [Xunit.Theory(DisplayName = "ReviewFindingConvergence_keeps_identity_across_region_paraphrases_and_refreshes_raw_region")]
    [Xunit.InlineData("A.Run", "A.Run")]
    [Xunit.InlineData("GoalAcceptanceVerifier.RunFocusedEvidence", "RunFocusedEvidence()")]
    [Xunit.InlineData("Goal Acceptance Verifier Run Focused Evidence", "  gOaL   aCCeptance verifier run focused evidence  ")]
    [Xunit.InlineData("Namespace.Type.RunFocusedEvidence<T>", "RunFocusedEvidence(string value, int count)")]
    public void ReviewFindingConvergenceKeepsIdentityAcrossRegionParaphrasesAndRefreshesRawRegion(
        string previousRegion,
        string submittedRegion)
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", previousRegion, "old hunk"),
                "Missing guard.")
        };
        var submittedLocation = new ReviewFindingLocation("src/A.cs", submittedRegion, "new hunk");
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-1", ReviewFindingState.Open, submittedLocation, "Missing guard.")],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(submittedLocation, Assert.Single(state).Location);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_does_not_normalize_file_identity")]
    public void ReviewFindingConvergenceDoesNotNormalizeFileIdentity()
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "RunFocusedEvidence"),
                "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding(
                    "F-1",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/B.cs", "RunFocusedEvidence"),
                    "Missing guard.")
            ],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, error.Code);
    }

    [Xunit.Theory(DisplayName = "ReviewFindingConvergence_does_not_collapse_region_identity_to_empty")]
    [Xunit.InlineData("(first)", "(second)")]
    [Xunit.InlineData("<First>", "<Second>")]
    [Xunit.InlineData("A.Run.", "B.Run.")]
    public void ReviewFindingConvergenceDoesNotCollapseRegionIdentityToEmpty(
        string previousRegion,
        string submittedRegion)
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", previousRegion),
                "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding(
                    "F-1",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", submittedRegion),
                    "Missing guard.")
            ],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, error.Code);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_recycle_guard_uses_raw_region_identity")]
    public void ReviewFindingConvergenceRecycleGuardUsesRawRegionIdentity()
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "FirstType.Run"),
                "First defect.")
        };
        var next = new ReviewFindingRound(
            [
                previous[0],
                new ReviewFinding(
                    "F-2",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", "SecondType.Run"),
                    "Second defect.")
            ],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(2, state.Count);
        Assert.Contains(state, finding => finding.StableId == "F-2");
    }

    [Xunit.Fact(DisplayName = "ReviewFinding_missing_severity_deserializes_and_round_trips_as_blocking")]
    public void ReviewFindingMissingSeverityDeserializesAndRoundTripsAsBlocking()
    {
        const string legacyJson =
            """{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard."}""";

        var finding = JsonSerializer.Deserialize<ReviewFinding>(legacyJson);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);

        var serialized = JsonSerializer.Serialize(finding);
        var roundTripped = JsonSerializer.Deserialize<ReviewFinding>(serialized);
        Assert.NotNull(roundTripped);
        Assert.Equal(FindingSeverity.Blocking, roundTripped.Severity);
        Assert.Contains("\"severity\":\"Blocking\"", serialized, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "ReviewFinding_null_or_unrecognized_severity_defaults_to_blocking")]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("\"informational\"")]
    public void ReviewFindingNullOrUnrecognizedSeverityDefaultsToBlocking(string severityJson)
    {
        var findingsJson =
            $$"""[{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard.","severity":{{severityJson}}}]""";

        var parsed = ReviewFindingConvergence.TryParseJson(
            findingsJson,
            "[]",
            out var round,
            out var diagnostic);

        Assert.True(parsed, diagnostic);
        Assert.Equal(FindingSeverity.Blocking, Assert.Single(round.Findings).Severity);
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
