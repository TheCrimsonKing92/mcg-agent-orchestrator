using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

public sealed class WorkerResultBlockersTests
{
    [Xunit.Fact]
    public void TryFindBlockers_MultipleFindings_ReturnsEveryTokenInOrder()
    {
        const string output = """
            Needs work:
            - src/ReceiptWriter.cs:42 blocking violates AC-2 - receipt is written too early
            - src/Schema.cs:18 blocking violates AC-5 - schema version is stale
            no other blocking findings exist in this diff
            WORKER_RESULT:
            blockers: src/ReceiptWriter.cs:42 blocking violates AC-2 - receipt is written too early; src/Schema.cs:18 blocking violates AC-5 - schema version is stale
            findings: [{"stable_id":"receipt-order","state":"open","severity":"blocking","category":"correctness","location":{"file":"src/ReceiptWriter.cs","region":"Write"},"description":"Violates AC-2: receipt is written too early."},{"stable_id":"schema-version","state":"open","severity":"blocking","category":"spec-compliance","location":{"file":"src/Schema.cs","region":"Version"},"description":"Violates AC-5: schema version is stale."}]
            criteria_verdicts: [{"criterion_index":1,"verdict":"not-met","evidence":"src/ReceiptWriter.cs:42"},{"criterion_index":4,"verdict":"not-met","evidence":"src/Schema.cs:18"}]
            verdict: needs-work
            END_WORKER_RESULT
            """;

        Assert.True(WorkerResultBlockers.TryFindBlockers(output, out var blockers));
        Assert.Equal(
            [
                "src/ReceiptWriter.cs:42 blocking violates AC-2 - receipt is written too early",
                "src/Schema.cs:18 blocking violates AC-5 - schema version is stale"
            ],
            blockers);
    }

    [Xunit.Fact]
    public void TryFindBlockers_SingleFinding_PreservesExistingRawValue()
    {
        const string output = """
            WORKER_RESULT:
            blockers: src/ReceiptWriter.cs:42 blocking violates AC-2 - receipt is written too early
            verdict: needs-work
            END_WORKER_RESULT
            """;

        Assert.True(WorkerResultBlockers.TryFindBlocker(output, out var rawBlocker));
        Assert.Equal("src/ReceiptWriter.cs:42 blocking violates AC-2 - receipt is written too early", rawBlocker);
        Assert.True(WorkerResultBlockers.TryFindBlockers(output, out var blockers));
        Assert.Equal([rawBlocker], blockers);
    }

    [Xunit.Fact(DisplayName = "WorkerResultBlockers_parses_blocked_at_cap_with_open_blocker")]
    public void WorkerResultBlockersParsesBlockedAtCapWithOpenBlocker()
    {
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\repo",
            0,
            "WORKER_RESULT:\nblockers: F-CAP - Missing guard.\nfindings: []\ntouched_anchors: []\nverdict: blocked-at-cap\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.True(WorkerResultBlockers.TryFindBlockedAtCapVerdict(verification, out var blocker));
        Assert.Equal("F-CAP - Missing guard.", blocker);
        Assert.False(WorkerResultBlockers.TryFindPassVerdict(verification));
    }

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
    [Xunit.InlineData("premise-invalid: required API does not exist", false, "")]
    [Xunit.InlineData("premise-invalid required API does not exist", false, "")]
    [Xunit.InlineData("premise-invalid - ", false, "")]
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

    [Xunit.Theory(DisplayName = "WorkerResultBlockers_requires_current_round_evidence_for_inconclusive")]
    [Xunit.InlineData("inconclusive - command timed out; no TRX", true)]
    [Xunit.InlineData("inconclusive", false)]
    [Xunit.InlineData("inconclusive: command timed out", false)]
    [Xunit.InlineData("inconclusive command timed out", false)]
    [Xunit.InlineData("inconclusive - ", false)]
    public void WorkerResultBlockersRequiresCurrentRoundEvidenceForInconclusive(
        string tests,
        bool expected)
    {
        var verification = new TaskVerificationRecord(
            "test",
            "C:\\repo",
            0,
            $"WORKER_RESULT:\ntests: {tests}\nblockers: none\nEND_WORKER_RESULT",
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.Equal(expected, WorkerResultBlockers.TryGetTestsStatus(verification, out var status));
        Assert.Equal(
            expected ? WorkerResultBlockers.TestsStatus.Inconclusive : WorkerResultBlockers.TestsStatus.Unknown,
            status);
    }

    [Xunit.Fact(DisplayName = "WorkerResultBlockers_field_scan_without_opener_matches_dispatch_parser")]
    public void WorkerResultBlockersFieldScanWithoutOpenerMatchesDispatchParser()
    {
        const string output = """
            files: none
            commands: dotnet test --no-build --filter Focused
            tests: inconclusive - command timed out; no TRX
            blockers: none
            model_fit: OpenAI/test - adequate - verification - sufficient
            skills: dotnet-windows-build-hygiene
            confidence: high
            """;

        Assert.True(WorkerResultBlockers.HasCompleteWorkerResult(output));
        Assert.True(WorkerResultBlockers.TryGetTestsStatus(output, out var status));
        Assert.Equal(WorkerResultBlockers.TestsStatus.Inconclusive, status);
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

    [Xunit.Fact(DisplayName = "TryFindReviewFindingRound_preserves_system_touch_proof_diagnostic")]
    public void TryFindReviewFindingRoundPreservesSystemTouchProofDiagnostic()
    {
        const string touchProofDiagnostic =
            "Round-diff touch proof unavailable because the carried finding round has no reviewed-commit baseline.";
        var verification = new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            0,
            """
            WORKER_RESULT:
            findings: [{"stable_id":"reported-goals-may-lack-timeline-evidence","state":"open","location":{"file":"src/TerminalGoalTimeline.cs","region":"BuildReportedGoals"},"description":"Reported goals may lack timeline evidence."}]
            touched_anchors: []
            verdict: pass
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow,
            ReviewFindingTouchedAnchors: [],
            ReviewFindingTouchProofDiagnostic: touchProofDiagnostic);

        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var diagnostic), diagnostic);
        Assert.Equal(touchProofDiagnostic, round.TouchProofDiagnostic);
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

    [Xunit.Theory(DisplayName = "Computed-empty proof carries unchanged open and resolved findings")]
    [Xunit.InlineData(ReviewFindingState.Open)]
    [Xunit.InlineData(ReviewFindingState.Resolved)]
    public void UnchangedFinding_EmptyTouchProof_CarriesForward(ReviewFindingState state)
    {
        var carried = new ReviewFinding(
            "F-1",
            state,
            new ReviewFindingLocation("src/A.cs", "A.Run", "guard"),
            "Missing guard.");

        var result = ReviewFindingConvergence.ApplyRound(
            [carried],
            new ReviewFindingRound([carried], []));

        Assert.Equal(carried, Assert.Single(result));
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

    [Xunit.Fact]
    public void ApplyRound_ResolvedPriorAndNewSameAnchor_AcceptsBothStates()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding(
                "F-A",
                ReviewFindingState.Open,
                anchor,
                "Original issue.",
                FindingSeverity.Blocking)
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding(
                    "F-A",
                    ReviewFindingState.Resolved,
                    anchor,
                    "Original issue resolved.",
                    FindingSeverity.Blocking),
                new ReviewFinding(
                    "F-B",
                    ReviewFindingState.Open,
                    anchor,
                    "Different issue at the same anchor.",
                    FindingSeverity.Blocking)
            ],
            [anchor]);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(2, state.Count);
        Assert.Equal(1, ReviewFindingConvergence.CountOpen(state));
        Assert.Equal(ReviewFindingState.Resolved, state.Single(finding => finding.StableId == "F-A").State);
        Assert.Equal(ReviewFindingState.Open, state.Single(finding => finding.StableId == "F-B").State);
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
        Assert.Equal("F-1", error.Violation.PriorStableId);
        Assert.Equal("F-RECYCLED", error.Violation.SubmittedStableId);
        Assert.Equal(anchor, error.Violation.PriorLocation);
        Assert.Equal(anchor, error.Violation.SubmittedLocation);
        var mismatch = Assert.Single(error.Violation.IdentityMismatches!);
        Assert.Equal(error.Violation.Code, mismatch.Code);
        Assert.Equal(error.Violation.Message, mismatch.Message);
    }

    [Xunit.Fact]
    public void MultipleIdentityMismatchesReturnOneOrderedViolation()
    {
        var previous = Enumerable.Range(0, 15)
            .Select(index => new ReviewFinding(
                $"F-{index:D2}",
                ReviewFindingState.Open,
                new ReviewFindingLocation($"src/F{index:D2}.cs", $"F{index:D2}.Run", "guard"),
                $"Finding {index:D2}."))
            .ToArray();
        var submitted = previous
            .Reverse()
            .Select(finding => finding.StableId == "F-10"
                ? finding with { Location = new ReviewFindingLocation("src/Moved.cs", "Moved.Run", "guard") }
                : finding)
            .Prepend(new ReviewFinding(
                "F-RECYCLED",
                ReviewFindingState.Open,
                previous[2].Location,
                "Recycled anchor."))
            .ToArray();

        var error = Assert.Throws<ReviewFindingConvergenceException>(() =>
            ReviewFindingConvergence.ApplyRound(previous, new ReviewFindingRound(submitted, [])));

        var mismatches = Assert.IsAssignableFrom<IReadOnlyList<ReviewFindingIdentityMismatch>>(
            error.Violation.IdentityMismatches);
        Assert.Equal(2, mismatches.Count);
        Assert.Equal(
            [
                ReviewFindingConvergence.RecycledAnchorIdentityViolationCode,
                ReviewFindingConvergence.IdentityMovedViolationCode
            ],
            mismatches.Select(mismatch => mismatch.Code));
        Assert.Equal(["F-02", "F-10"], mismatches.Select(mismatch => mismatch.PriorStableId));
        Assert.Equal("F-RECYCLED", mismatches[0].SubmittedStableId);
        Assert.Contains("cannot be recycled", mismatches[0].Message, StringComparison.Ordinal);
        Assert.Contains("different structural anchor", mismatches[1].Message, StringComparison.Ordinal);
        Assert.Equal(mismatches[0].Code, error.Violation.Code);
        Assert.Equal(mismatches[0].Message, error.Violation.Message);
        Assert.Equal(previous[2].Location, previous.Single(finding => finding.StableId == "F-02").Location);
        Assert.Equal(previous, ReviewFindingConvergence.ApplyRound(
            previous,
            new ReviewFindingRound(previous.Reverse().ToArray(), [])));
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_canonicalizes_a_lone_new_stable_id_at_an_omitted_open_prior_exact_anchor")]
    public void ReviewFindingConvergenceCanonicalizesLoneNewStableIdAtOmittedOpenPriorExactAnchor()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, anchor, "Original issue.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-2", ReviewFindingState.Resolved, anchor, "Original issue fixed.")],
            [anchor]);

        var state = ReviewFindingConvergence.ApplyRound(previous, next, out var canonicalizations);

        var finding = Assert.Single(state);
        Assert.Equal("F-1", finding.StableId);
        Assert.Equal(ReviewFindingState.Resolved, finding.State);
        Assert.Equal(anchor, finding.Location);
        var canonicalization = Assert.Single(canonicalizations);
        Assert.Equal("F-1", canonicalization.PriorStableId);
        Assert.Equal("F-2", canonicalization.SubmittedStableId);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_does_not_canonicalize_a_lone_new_stable_id_at_a_different_hunk")]
    public void ReviewFindingConvergenceDoesNotCanonicalizeLoneNewStableIdAtDifferentHunk()
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "A.Run", "guard-a"),
                "First issue.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding(
                    "F-2",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", "A.Run", "guard-b"),
                    "Second issue.")
            ],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next, out var canonicalizations);

        Assert.Equal(2, state.Count);
        Assert.Empty(canonicalizations);
        Assert.Contains(state, finding => finding.StableId == "F-1");
        Assert.Contains(state, finding => finding.StableId == "F-2");
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_does_not_canonicalize_when_two_open_priors_are_omitted")]
    public void ReviewFindingConvergenceDoesNotCanonicalizeWhenTwoOpenPriorsAreOmitted()
    {
        var anchorA = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, anchorA, "First issue."),
            new ReviewFinding(
                "F-2",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/B.cs", "B.Run", "guard"),
                "Second issue.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-NEW", ReviewFindingState.Open, anchorA, "First issue.")],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next, out _));

        Assert.Equal(ReviewFindingConvergence.RecycledAnchorIdentityViolationCode, error.Code);
    }

    [Xunit.Fact]
    public void ApplyRound_OmittedOpenPriorWithTwoNewFindings_RejectsAnchorReuse()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding("F-A", ReviewFindingState.Open, anchor, "Original issue.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding("F-B", ReviewFindingState.Open, anchor, "Replacement identity."),
                new ReviewFinding(
                    "F-C",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/C.cs", "C.Run", "guard"),
                    "Independent issue.")
            ],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.RecycledAnchorIdentityViolationCode, error.Code);
        Assert.Equal("F-A", error.Violation.PriorStableId);
        Assert.Equal("F-B", error.Violation.SubmittedStableId);
        Assert.Contains("cannot be recycled", error.Message, StringComparison.Ordinal);
        Assert.Contains("Report the same defect under 'F-A'", error.Message, StringComparison.Ordinal);
        Assert.Contains("submit 'F-A' as resolved in this same round", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RejectedCapResolution_NewSameAnchor_RetainsReportablePrior()
    {
        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var previous = new[]
        {
            new ReviewFinding(
                "F-A",
                ReviewFindingState.Open,
                anchor,
                "Original issue.",
                FindingSeverity.Blocking)
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding(
                    "F-A",
                    ReviewFindingState.Resolved,
                    anchor,
                    "Original issue resolved.",
                    FindingSeverity.Blocking),
                new ReviewFinding(
                    "F-B",
                    ReviewFindingState.Open,
                    anchor,
                    "Different issue at the same anchor.",
                    FindingSeverity.Blocking)
            ],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(() =>
            ReviewFindingConvergence.ValidateResolutionAtCap(previous, next, [], "candidate", []));
        var state = ReviewFindingConvergence.ApplyRejectedCapResolutionRound(
            previous,
            next,
            error.Violation);

        Assert.Equal(ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode, error.Code);
        var retained = Assert.Single(state);
        Assert.Equal("F-A", retained.StableId);
        Assert.Equal(ReviewFindingState.Open, retained.State);
        Assert.Equal("Different issue at the same anchor.", retained.Description);
        Assert.Equal(1, ReviewFindingConvergence.CountOpen(state));
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
        Assert.Equal("F-1", error.Violation.PriorStableId);
        Assert.Equal("F-1", error.Violation.SubmittedStableId);
        Assert.Equal(previous[0].Location, error.Violation.PriorLocation);
        Assert.Equal(next.Findings[0].Location, error.Violation.SubmittedLocation);
        var mismatch = Assert.Single(error.Violation.IdentityMismatches!);
        Assert.Equal(error.Violation.Code, mismatch.Code);
        Assert.Equal(error.Violation.Message, mismatch.Message);
        Assert.Equal(error.Violation.PriorStableId, mismatch.PriorStableId);
        Assert.Equal(error.Violation.SubmittedStableId, mismatch.SubmittedStableId);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_afc62d88_allows_persistent_finding_to_follow_touched_code")]
    public void ReviewFindingConvergenceAfc62d88AllowsPersistentFindingToFollowTouchedCode()
    {
        // Regression fixture from afc62d88/bb5ff6ed. The dispatch artifact preserved the real stable_id
        // and TerminalGoalS... source prefix, but truncated the remainder of both recorded locations.
        var priorLocation = new ReviewFindingLocation(
            "src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSummary.cs",
            "TerminalGoalSummary.BuildReportedGoals",
            "timeline evidence");
        var movedLocation = new ReviewFindingLocation(
            "src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalTimeline.cs",
            "TerminalGoalTimeline.BuildReportedGoals",
            "timeline evidence");
        var previous = new[]
        {
            new ReviewFinding(
                "reported-goals-may-lack-timeline-evidence",
                ReviewFindingState.Open,
                priorLocation,
                "Reported goals may lack timeline evidence.")
        };
        var next = new ReviewFindingRound(
            [previous[0] with { Location = movedLocation }],
            [priorLocation]);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        var carried = Assert.Single(state);
        Assert.Equal("reported-goals-may-lack-timeline-evidence", carried.StableId);
        Assert.Equal(ReviewFindingState.Open, carried.State);
        Assert.Equal(movedLocation, carried.Location);
    }

    [Xunit.Fact(DisplayName = "ReviewFindingConvergence_afc62d88_untouched_reopen_reports_unavailable_touch_proof")]
    public void ReviewFindingConvergenceAfc62d88UntouchedReopenReportsUnavailableTouchProof()
    {
        // Regression fixture from the consecutive afc62d88/bb5ff6ed violation record. The dispatch
        // artifact preserved the real stable_id and file prefix but truncated the recorded location.
        const string diagnostic =
            "Round-diff touch proof unavailable because the carried finding round has no reviewed-commit baseline.";
        var anchor = new ReviewFindingLocation(
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTests.cs",
            "ConductorBatchLoopTests.NoBranchSlugDerivation",
            "branch slug derivation");
        var previous = new[]
        {
            new ReviewFinding(
                "no-branch-slug-derivation-untested",
                ReviewFindingState.Resolved,
                anchor,
                "Branch slug derivation lacks coverage.")
        };
        var next = new ReviewFindingRound(
            [previous[0] with { State = ReviewFindingState.Open }],
            [],
            diagnostic);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.UntouchedReopenViolationCode, error.Code);
        Assert.Equal("no-branch-slug-derivation-untested", error.Violation.PriorStableId);
        Assert.Contains(diagnostic, error.Violation.Message, StringComparison.Ordinal);
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

    [Xunit.Theory]
    [Xunit.InlineData("collapse branch :1078-1102", "collapse branch :1078-1097")]
    [Xunit.InlineData("collapse branch :1078", "collapse branch :1097")]
    [Xunit.InlineData("GoalStatus [24-37]", "GoalStatus [39-52]")]
    [Xunit.InlineData("GoalStatus [24]", "GoalStatus [39]")]
    [Xunit.InlineData("RunFocusedEvidence L1078-L1102", "RunFocusedEvidence L1080-L1097")]
    [Xunit.InlineData("RunFocusedEvidence lines 1078-1102", "RunFocusedEvidence lines 1080-1097")]
    [Xunit.InlineData("RunFocusedEvidence1078-1102", "RunFocusedEvidence1080-1097")]
    public void ApplyRound_ShiftedTrailingLineRange_KeepsAnchor(
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

    [Xunit.Fact]
    public void ApplyRound_DifferentRegionWithLineRanges_RejectsMove()
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "FirstRegion :1078-1102"),
                "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [
                new ReviewFinding(
                    "F-1",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/A.cs", "SecondRegion :1080-1097"),
                    "Missing guard.")
            ],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, error.Code);
    }

    [Xunit.Fact]
    public void ApplyRound_BracketedPackedLineRange_MatchesSeparateHunkPresentation()
    {
        var previous = new[]
        {
            new ReviewFinding(
                "F-1",
                ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "TryGetProviderSubscriptionCooldown", "lines 1585-1626"),
                "Missing guard.")
        };
        var submittedLocation = new ReviewFindingLocation(
            "src/A.cs",
            "TryGetProviderSubscriptionCooldown [lines 1585-1626]");
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-1", ReviewFindingState.Open, submittedLocation, "Missing guard.")],
            []);

        var state = ReviewFindingConvergence.ApplyRound(previous, next);

        Assert.Equal(submittedLocation, Assert.Single(state).Location);
    }

    [Xunit.Fact]
    public void ApplyRound_IdenticalRenderedLocations_ReportsNormalizedRegions()
    {
        var previousLocation = new ReviewFindingLocation("src/A.cs", "GoalStatus", "current block");
        var submittedLocation = new ReviewFindingLocation("src/A.cs", "GoalStatus [current block]");
        Assert.Equal(previousLocation.ToString(), submittedLocation.ToString());
        var previous = new[]
        {
            new ReviewFinding("F-1", ReviewFindingState.Open, previousLocation, "Missing guard.")
        };
        var next = new ReviewFindingRound(
            [new ReviewFinding("F-1", ReviewFindingState.Open, submittedLocation, "Missing guard.")],
            []);

        var error = Assert.Throws<ReviewFindingConvergenceException>(
            () => ReviewFindingConvergence.ApplyRound(previous, next));

        Assert.Contains("normalized_prior_region='goalstatus'", error.Violation.Message, StringComparison.Ordinal);
        Assert.Contains(
            "normalized_submitted_region='goalstatus [current block]'",
            error.Violation.Message,
            StringComparison.Ordinal);
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

    [Xunit.Theory(DisplayName = "ReviewFinding_invalid_or_missing_category_defaults_to_unspecified")]
    [Xunit.InlineData("")]
    [Xunit.InlineData(",\"category\":null")]
    [Xunit.InlineData(",\"category\":42")]
    [Xunit.InlineData(",\"category\":[]")]
    [Xunit.InlineData(",\"category\":{}")]
    [Xunit.InlineData(",\"category\":\"unknown\"")]
    public void ReviewFindingInvalidOrMissingCategoryDefaultsToUnspecified(string categoryJson)
    {
        var findingsJson =
            $$"""[{"stable_id":"F-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Missing guard."{{categoryJson}}}]""";

        Assert.True(
            ReviewFindingConvergence.TryParseJson(findingsJson, "[]", out var round, out var diagnostic),
            diagnostic);
        Assert.Equal(FindingCategory.Unspecified, Assert.Single(round.Findings).Category);
    }

    [Xunit.Fact(DisplayName = "ReviewFinding_category_round_trips_with_wire_name")]
    public void ReviewFindingCategoryRoundTripsWithWireName()
    {
        var finding = new ReviewFinding(
            "F-1",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/A.cs", "A.Run"),
            "Missing guard.",
            FindingSeverity.Blocking,
            FindingCategory.SpecCompliance);

        var json = JsonSerializer.Serialize(finding);
        var restored = JsonSerializer.Deserialize<ReviewFinding>(json);

        Assert.Contains("\"category\":\"spec-compliance\"", json, StringComparison.Ordinal);
        Assert.Equal(FindingCategory.SpecCompliance, restored!.Category);
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

    [Xunit.Fact(DisplayName = "Finding evidence request is parsed only from the typed finding field")]
    public void FindingEvidenceRequestIsParsedFromTypedFindingField()
    {
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            """
            WORKER_RESULT:
            blockers: missing focused evidence
            findings: [{"stable_id":"evidence-1","state":"open","severity":"blocking","category":"correctness","location":{"file":"ConductorDriver.cs","region":"routing"},"description":"Run Infrastructure.Tests ConductorDriverTests","evidence_request":{"selections":[{"test_project":"Infrastructure.Tests","test_class":"ConductorDriverTests"}]}}]
            touched_anchors: []
            verdict: needs-work
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out _));
        var request = Assert.Single(round.Findings).EvidenceRequest;
        var selection = Assert.Single(Assert.IsType<FindingEvidenceRequest>(request).Selections);
        Assert.Equal("Infrastructure.Tests", selection.TestProject);
        Assert.Equal("ConductorDriverTests", selection.TestClass);
    }
}
