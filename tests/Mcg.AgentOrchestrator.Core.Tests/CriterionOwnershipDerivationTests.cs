using Mcg.AgentOrchestrator.Core;

public sealed class CriterionOwnershipDerivationTests
{
    [Xunit.Fact]
    public void OperatorPhraseOverridesPreservedGateOwnerAndGateMarker()
    {
        const string criterion = "The operator owns the check; Acceptance executes. TEST-VERIFIABLE.";

        var result = CriterionOwnershipDerivation.DeriveForRevision(
            [criterion], [criterion], []);

        Xunit.Assert.Empty(result.AcceptanceGateOwned);
        Xunit.Assert.Equal([criterion], result.OperatorOwned);
    }

    [Xunit.Fact]
    public void RevisionOrdersAndDeduplicatesExactTextFromNewCriteria()
    {
        const string gate = "A gate check.";
        const string operated = "An operator check.";
        const string marker = "Acceptance executes this check.";

        var result = CriterionOwnershipDerivation.DeriveForRevision(
            [operated, marker, gate, operated, gate, "a gate check."],
            [gate, "Removed gate check."],
            [operated, "Removed operator check."]);

        Xunit.Assert.Equal([marker, gate], result.AcceptanceGateOwned);
        Xunit.Assert.Equal([operated], result.OperatorOwned);
    }

    [Xunit.Fact]
    public void SubjectOperatorOwnershipMentionDoesNotOverrideTrailingDeveloperOwner()
    {
        const string criterion =
            "The report explains operator-owned mappings. The report is ready. Developer owns; Acceptance executes. TEST-VERIFIABLE.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], []);

        Xunit.Assert.Equal([criterion], result.AcceptanceGateOwned);
        Xunit.Assert.Empty(result.OperatorOwned);
    }

    [Xunit.Fact]
    public void OperatorExecutesRealWorldCriterionIsOperatorOwned()
    {
        const string criterion = "The live result is observed. Operator executes. REAL-WORLD-DEPENDENT.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], []);

        Xunit.Assert.Empty(result.AcceptanceGateOwned);
        Xunit.Assert.Equal([criterion], result.OperatorOwned);
    }
}
