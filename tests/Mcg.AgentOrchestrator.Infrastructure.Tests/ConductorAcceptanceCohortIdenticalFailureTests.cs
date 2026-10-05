using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortIdenticalFailureTests
{
    private static readonly GoalId A = new(new string('1', 32));
    private static readonly GoalId B = new(new string('2', 32));
    private static readonly GoalId C = new(new string('3', 32));
    private static readonly GoalId D = new(new string('4', 32));
    private static readonly string[] Tests = ["Tests.T.One", "Tests.T.Two", "Tests.T.Three"];
    private static AcceptanceCohortAttributedMember Blame(IReadOnlyList<string> tests) => new(A, 0, "candidate", tests);

    [Fact]
    public void LaterIndeterminateCohortWithoutA_RetractsEarlierBlame()
    {
        var earlier = new CohortFailureEvidence("earlier", [A, B], "main", Tests, [Blame(Tests)]);
        var result = Assert.IsType<ConductorCohortIdenticalFailureDecision>(
            ConductorAcceptanceCohortIdenticalFailure.Decide("later", [B, C], "main", Tests, [], [earlier]));
        Assert.Empty(result.Withheld);
        var retraction = Assert.Single(result.Retractions);
        Assert.Equal("earlier", retraction.EarlierCohortId);
        Assert.Equal(A, retraction.Member.GoalId);
        Assert.Equal("candidate", retraction.Member.CandidateRevision);
        Assert.Equal(Tests.Order(StringComparer.Ordinal), result.Tests);
    }

    [Fact]
    public void EarlierCohortWithoutA_WithholdsCurrentBlame()
    {
        var earlier = new CohortFailureEvidence("earlier", [B, C], "main", Tests, []);
        var result = Assert.IsType<ConductorCohortIdenticalFailureDecision>(
            ConductorAcceptanceCohortIdenticalFailure.Decide("later", [A, D], "main", Tests, [Blame(Tests)], [earlier]));
        Assert.Empty(result.Retractions);
        var withheld = Assert.Single(result.Withheld);
        Assert.Equal("earlier", withheld.EarlierCohortId);
        Assert.Equal(A, withheld.Member.GoalId);
    }

    [Theory]
    [InlineData("different-test")]
    [InlineData("different-main")]
    [InlineData("member-present")]
    [InlineData("empty-current")]
    [InlineData("empty-earlier")]
    [InlineData("case")]
    [InlineData("whitespace")]
    [InlineData("self")]
    public void NonQualifyingEvidence_LeavesBothDirectionsUnchanged(string variation)
    {
        string[] earlierTests = variation switch
        {
            "different-test" => [Tests[0], Tests[1], "Tests.T.Other"],
            "empty-earlier" => [],
            "case" => [Tests[0].ToLowerInvariant(), Tests[1], Tests[2]],
            "whitespace" => [Tests[0] + " ", Tests[1], Tests[2]],
            _ => Tests
        };
        var currentTests = variation == "empty-current" ? [] : Tests;
        var main = variation == "different-main" ? "other-main" : "main";
        var earlierId = variation == "self" ? "later" : "earlier";
        var withholding = new CohortFailureEvidence(earlierId,
            variation == "member-present" ? [A, B] : [B, C], main, earlierTests, []);
        Assert.Null(ConductorAcceptanceCohortIdenticalFailure.Decide("later", [A, D], "main",
            currentTests, [Blame(currentTests)], [withholding]));
        var retracting = new CohortFailureEvidence(earlierId, [A, B], main, earlierTests, [Blame(earlierTests)]);
        Assert.Null(ConductorAcceptanceCohortIdenticalFailure.Decide("later",
            variation == "member-present" ? [A, C] : [B, C], "main", currentTests, [], [retracting]));
    }

    [Fact]
    public void IdentityOrderAndDuplicates_DoNotChangeSetEquality()
    {
        var reordered = Tests.Reverse().Concat([Tests[0]]).ToArray();
        var earlier = new CohortFailureEvidence("earlier", [A, B], "main", reordered, [Blame(reordered)]);
        Assert.Single(ConductorAcceptanceCohortIdenticalFailure.Decide("later", [B, C], "main",
            Tests, [], [earlier])!.Retractions);
    }

    [Fact]
    public void ReproducerSubset_DoesNotMatchRecordedCompleteGateSet()
    {
        var earlier = new CohortFailureEvidence("earlier", [A, B], "main",
            Tests.Concat(["Tests.T.Extra"]).ToArray(), [Blame(Tests)]);
        Assert.Null(ConductorAcceptanceCohortIdenticalFailure.Decide("later", [B, C], "main", Tests, [], [earlier]));
    }

    [Fact]
    public void RecordedEmptyGateSet_DoesNotRetractNonemptyReproducerSet()
    {
        var earlier = new CohortFailureEvidence("earlier", [A, B], "main", [], [Blame(Tests)]);
        Assert.Null(ConductorAcceptanceCohortIdenticalFailure.Decide("later", [B, C], "main", Tests, [], [earlier]));
    }

    [Fact]
    public void LegacyReceipt_UsesExactBlamedSetForRetractionButCannotWithhold()
    {
        var earlier = new CohortFailureEvidence("earlier", [A, B], "main", null, [Blame(Tests)]);
        Assert.Single(ConductorAcceptanceCohortIdenticalFailure.Decide("later", [B, C], "main",
            Tests, [], [earlier])!.Retractions);
        Assert.Null(ConductorAcceptanceCohortIdenticalFailure.Decide("later", [C, D], "main",
            Tests, [new(C, 0, "other-candidate", Tests)], [earlier with { AttributedMembers = [] }]));
    }

    [Fact]
    public void RetractionEvent_IsOperatorDecisionOnlyWithExactToken()
    {
        Assert.Equal(ConductEventOperatorClassifier.Decision, ConductEventOperatorClassifier.Classify(
            "cohort-attribution-retracted", "COHORT_ATTRIBUTION_RETRACTED goal=11111111 earlier=e later=l tests=3"));
        Assert.Null(ConductEventOperatorClassifier.Classify("cohort-attribution-retracted", "COHORT_ATTRIBUTION_RETRACTED_EXTRA"));
    }
}
