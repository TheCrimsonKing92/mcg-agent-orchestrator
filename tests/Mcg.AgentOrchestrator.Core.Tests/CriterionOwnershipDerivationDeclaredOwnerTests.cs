using Mcg.AgentOrchestrator.Core;

public sealed class CriterionOwnershipDerivationDeclaredOwnerTests
{
    [Xunit.Theory]
    [Xunit.InlineData("Acceptance owns execution.")]
    [Xunit.InlineData("Reviewer owns source inspection; Acceptance owns execution.")]
    public void ExplicitAcceptanceOwnershipSurvivesRevision(string declaration)
    {
        var criterion = $"The fallback is preserved. {declaration} TEST-VERIFIABLE.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], []);

        Xunit.Assert.Equal([criterion], result.AcceptanceGateOwned);
        Xunit.Assert.Empty(result.OperatorOwned);
    }

    [Xunit.Fact]
    public void ClassificationAloneDoesNotGrantOperatorOwnership()
    {
        const string criterion = "The live behavior is observed. REAL-WORLD-DEPENDENT.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], []);

        Xunit.Assert.Empty(result.AcceptanceGateOwned);
        Xunit.Assert.Empty(result.OperatorOwned);
    }

    [Xunit.Fact]
    public void WorkerDeclarationRemovesPriorGateOwnership()
    {
        const string criterion = "The Reviewer confirms the diff. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [criterion], []);

        Xunit.Assert.Empty(result.AcceptanceGateOwned);
        Xunit.Assert.Empty(result.OperatorOwned);
    }

    [Xunit.Fact]
    public void WorkerDeclarationRemovesPriorOperatorOwnership()
    {
        const string criterion = "The Reviewer confirms the diff. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], [criterion]);

        Xunit.Assert.Empty(result.OperatorOwned);
        Xunit.Assert.Empty(result.AcceptanceGateOwned);
    }

    [Xunit.Fact]
    public void GateDeclarationReplacesPriorOperatorOwnership()
    {
        const string criterion = "The focused suite passes. Developer owns; Acceptance executes. TEST-VERIFIABLE.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], [criterion]);

        Xunit.Assert.Empty(result.OperatorOwned);
        Xunit.Assert.Equal([criterion], result.AcceptanceGateOwned);
    }

    [Xunit.Fact]
    public void UndeclaredCriterionKeepsPriorOperatorOwnership()
    {
        const string criterion = "The Reviewer confirms the diff. TEST-VERIFIABLE by reading.";

        var result = CriterionOwnershipDerivation.DeriveForRevision([criterion], [], [criterion]);

        Xunit.Assert.Equal([criterion], result.OperatorOwned);
        Xunit.Assert.Empty(result.AcceptanceGateOwned);
    }
}
