using Mcg.AgentOrchestrator.Core;

public sealed class DeclaredOwnerMarkerTests
{
    [Xunit.Fact]
    public void AcceptanceExecutionDeclaresGateOwner()
    {
        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(
            "The focused suite passes. Developer owns; Acceptance executes. TEST-VERIFIABLE.");

        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.AcceptanceGate, owner.Kind);
        Xunit.Assert.Null(owner.Role);
        Xunit.Assert.Equal("acceptance-gate", owner.WireToken);
    }

    [Xunit.Fact]
    public void ReviewerExecutionDeclaresWorkerAndRole()
    {
        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(
            "The Reviewer confirms the diff. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.");

        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.Worker, owner.Kind);
        Xunit.Assert.Equal("Reviewer", owner.Role);
        Xunit.Assert.Equal("worker", owner.WireToken);
    }

    [Xunit.Theory]
    [Xunit.InlineData("The live result is observed. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.")]
    [Xunit.InlineData("The result is observed. Operator owns; Developer executes. TEST-VERIFIABLE.")]
    [Xunit.InlineData("The Reviewer confirms the diff. TEST-VERIFIABLE by reading.")]
    [Xunit.InlineData("Developer owns. The result is recorded.")]
    public void OperatorOrAbsentTrailingDeclarationReturnsNone(string criterion)
    {
        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);

        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.None, owner.Kind);
        Xunit.Assert.Null(owner.Role);
    }
}
