using Mcg.AgentOrchestrator.Core;

// Pure domain inputs; no shared state or external resources.
public sealed class ReviewFindingConvergenceTouchedAnchorUnchangedClaimTests
{
    // Keep the expected code literal so the baseline negative control compiles without the new constant.
    private const string ViolationCode = "ERR_REVIEW_FINDING_TOUCHED_ANCHOR_CLAIMED_UNCHANGED";
    private static readonly ReviewFindingLocation Anchor = new("src/A.cs", "A.Run", "guard");
    private const string ContinuityClaim = "REVIEW DEFECT: unchanged since the earlier reviewed abc1234 candidate";

    [Xunit.Fact]
    public void TouchedAnchorClaimedUnchanged_Rejected()
    {
        var prior = Finding("Guard missing.");
        var submitted = prior with { Description = ContinuityClaim };

        var error = Assert.Throws<ReviewFindingConvergenceException>(() =>
            ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([submitted], [Anchor])));

        Assert.Equal(ViolationCode, error.Code);
        Assert.Equal(ViolationCode, error.Violation.Code);
        Assert.Contains(prior.StableId, error.Message, StringComparison.Ordinal);
        Assert.Equal(prior.StableId, error.Violation.PriorStableId);
        Assert.Equal(submitted.StableId, error.Violation.SubmittedStableId);
        Assert.Equal(Anchor, error.Violation.PriorLocation);
        Assert.Equal(Anchor, error.Violation.SubmittedLocation);
    }

    [Xunit.Fact]
    public void UntouchedAnchorClaimedUnchanged_MergesSubmittedFinding()
    {
        var prior = Finding("Guard missing.");
        var submitted = prior with { Description = ContinuityClaim };

        var merged = ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([submitted], []));

        Assert.Equal(submitted, Assert.Single(merged));
    }

    [Xunit.Fact]
    public void TouchedAnchorCurrentDefectDescription_MergesSubmittedFinding()
    {
        // A previous continuity claim must not control the submitted description's outcome.
        var prior = Finding(ContinuityClaim);
        var submitted = prior with { Description = "Guard is still missing at A.Run line 42." };

        var merged = ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([submitted], [Anchor]));

        Assert.Equal(submitted, Assert.Single(merged));
    }

    [Xunit.Theory]
    [Xunit.InlineData("UNCHANGED since abc1234")]
    [Xunit.InlineData("(Unchanged), still missing.")]
    public void TouchedAnchorContinuityClaim_IgnoresCaseAndAcceptsWordBoundaries(string description)
    {
        var prior = Finding("Guard missing.");
        var submitted = prior with { Description = description };

        var error = Assert.Throws<ReviewFindingConvergenceException>(() =>
            ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([submitted], [Anchor])));

        Assert.Equal(ViolationCode, error.Code);
    }

    [Xunit.Theory]
    [Xunit.InlineData("unchangedness")]
    [Xunit.InlineData("un-changed")]
    [Xunit.InlineData("unchanged_since abc1234")]
    [Xunit.InlineData("not changed since abc1234")]
    public void TouchedAnchorWithoutLiteralWord_MergesSubmittedFinding(string description)
    {
        var prior = Finding("Guard missing.");
        var submitted = prior with { Description = description };

        var merged = ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([submitted], [Anchor]));

        Assert.Equal(submitted, Assert.Single(merged));
    }

    [Xunit.Theory]
    [Xunit.InlineData(ReviewFindingState.Open, ReviewFindingState.Resolved)]
    [Xunit.InlineData(ReviewFindingState.Resolved, ReviewFindingState.Resolved)]
    [Xunit.InlineData(ReviewFindingState.Resolved, ReviewFindingState.Open)]
    public void TouchedAnchorWithoutCarriedOpenState_MergesSubmittedFinding(
        ReviewFindingState priorState, ReviewFindingState submittedState)
    {
        var prior = Finding("Guard missing.") with { State = priorState };
        var submitted = prior with { State = submittedState, Description = ContinuityClaim };

        var merged = ReviewFindingConvergence.ApplyRound([prior], new ReviewFindingRound([submitted], [Anchor]));

        Assert.Equal(submitted, Assert.Single(merged));
    }

    [Xunit.Fact]
    public void NewOrOmittedFindingWithContinuityClaim_PreservesExistingMerge()
    {
        var finding = Finding(ContinuityClaim);

        var introduced = ReviewFindingConvergence.ApplyRound([], new ReviewFindingRound([finding], [Anchor]));
        var omitted = ReviewFindingConvergence.ApplyRound([finding], new ReviewFindingRound([], [Anchor]));

        Assert.Equal(finding, Assert.Single(introduced));
        Assert.Equal(finding, Assert.Single(omitted));
    }

    [Xunit.Fact]
    public void LaterUntouchedReopen_KeepsExistingViolationPrecedence()
    {
        var prior = Finding("Guard missing.");
        var resolved = prior with
        {
            StableId = "F-2",
            State = ReviewFindingState.Resolved,
            Location = new ReviewFindingLocation("src/B.cs", "B.Run", "guard")
        };
        var submitted = prior with { Description = ContinuityClaim };

        var error = Assert.Throws<ReviewFindingConvergenceException>(() =>
            ReviewFindingConvergence.ApplyRound([prior, resolved], new ReviewFindingRound(
                [submitted, resolved with { State = ReviewFindingState.Open }], [Anchor])));

        Assert.Equal(ReviewFindingConvergence.UntouchedReopenViolationCode, error.Code);
    }

    private static ReviewFinding Finding(string description) =>
        new("F-1", ReviewFindingState.Open, Anchor, description);
}
