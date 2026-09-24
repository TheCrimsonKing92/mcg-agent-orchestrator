using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceCriterionExplicitOwnerSentenceTests
{
    [Xunit.Theory]
    [Xunit.InlineData(
        "A Reviewer finding with category operator-owned routes to the operator, not the Developer. Acceptance owns this criterion.",
        "Acceptance")]
    [Xunit.InlineData(
        "The finding is operator-owned in the report. Developer owns; Acceptance executes. TEST-VERIFIABLE.",
        "Developer")]
    public void ExplicitNonOperatorOwnerBeatsDescriptiveOperatorCategory(string criterion, string owner)
    {
        var result = AcceptanceCriterionOwnershipMarker.Classify(criterion);

        Xunit.Assert.Equal(AcceptanceCriterionOwnershipClassification.NotOperatorOwned, result.Classification);
        Xunit.Assert.Equal(owner, result.ConflictingDeclaredOwner, ignoreCase: true);
    }

    [Xunit.Fact]
    public void ExplicitOperatorOwnerBeatsDescriptiveAcceptanceCategory()
    {
        var result = AcceptanceCriterionOwnershipMarker.Classify(
            "A Reviewer finding with category acceptance-gate-owned routes to the gate. The operator owns this criterion.");

        Xunit.Assert.Equal(AcceptanceCriterionOwnershipClassification.OperatorOwned, result.Classification);
    }
}
