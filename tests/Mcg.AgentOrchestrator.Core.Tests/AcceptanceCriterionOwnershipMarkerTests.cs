using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriterionOwnershipMarkerTests
{
    [Xunit.Theory]
    [Xunit.InlineData("REAL-WORLD-DEPENDENT, operator-owned.")]
    [Xunit.InlineData("REAL-WORLD-DEPENDENT, operator owned.")]
    [Xunit.InlineData("The operator owns this observation. REAL-WORLD-DEPENDENT.")]
    [Xunit.InlineData("This observation is owned by the operator. TEST-VERIFIABLE.")]
    [Xunit.InlineData("This observation is owned by operator. TEST-VERIFIABLE.")]
    public void ExplicitOperatorOwnershipPhrasesAreAccepted(string trailingText)
    {
        var result = AcceptanceCriterionOwnershipMarker.Classify(
            $"The deployment is observed. {trailingText}");

        Xunit.Assert.Equal(AcceptanceCriterionOwnershipClassification.OperatorOwned, result.Classification);
    }

    [Xunit.Fact]
    public void IncidentalOperatorMentionIsExcludedByDeveloperOwnedTrailingRegion()
    {
        const string criterion =
            "The operator-visible hold reason for a pending refinement reports the age of the pending entry. Developer owns; Acceptance executes. TEST-VERIFIABLE.";

        var result = AcceptanceCriterionOwnershipMarker.Classify(criterion);

        Xunit.Assert.Equal(AcceptanceCriterionOwnershipClassification.NotOperatorOwned, result.Classification);
        Xunit.Assert.DoesNotContain("operator-visible", result.TrailingRegion, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void ResearcherOwnedRealWorldCriterionIsNotOperatorOwned()
    {
        const string criterion =
            "The reproduction names the compiler process observed during both builds. Researcher owns; Reviewer reads. REAL-WORLD-DEPENDENT reproduction, recorded under .orchestrator/operator-evidence/<prefix>/.";

        var result = AcceptanceCriterionOwnershipMarker.Classify(criterion);

        Xunit.Assert.Equal(AcceptanceCriterionOwnershipClassification.NotOperatorOwned, result.Classification);
        Xunit.Assert.Equal("Researcher", result.ConflictingDeclaredOwner, ignoreCase: true);
    }

    [Xunit.Fact]
    public void UnownedRealWorldCriterionDefaultsToOperatorWithWeakSignal()
    {
        var result = AcceptanceCriterionOwnershipMarker.Classify(
            "The live deployment is observed. REAL-WORLD-DEPENDENT.");

        Xunit.Assert.Equal(
            AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal,
            result.Classification);
    }

    [Xunit.Fact]
    public void OwnershipPhraseOutsideTrailingRegionDoesNotClassifyCriterion()
    {
        var result = AcceptanceCriterionOwnershipMarker.Classify(
            "This observation is owned by the operator. The implementation records the result.");

        Xunit.Assert.Equal(AcceptanceCriterionOwnershipClassification.Unclassified, result.Classification);
        Xunit.Assert.Empty(result.TrailingRegion);
    }
}
