using Mcg.AgentOrchestrator.Core;

public sealed class DeclaredOwnerMarkerTests
{
    [Xunit.Theory]
    [Xunit.InlineData("Acceptance owns execution.")]
    [Xunit.InlineData("Reviewer owns source inspection; Acceptance owns execution.")]
    [Xunit.InlineData("Acceptance gate owns execution.")]
    [Xunit.InlineData("Gate owns execution.")]
    [Xunit.InlineData("Acceptance executes.")]
    public void ExplicitAcceptanceOwnerDoesNotRequireSeparateExecutionMarker(string declaration)
    {
        var criterion = $"The fallback is preserved. {declaration} TEST-VERIFIABLE.";

        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);

        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.AcceptanceGate, owner.Kind);
        Xunit.Assert.True(AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion));
        Xunit.Assert.False(AcceptanceCriterionOwnershipMarker.HasConflictingDeclaredEvidenceOwners(criterion));
    }

    [Xunit.Theory]
    [Xunit.InlineData("The report mentions Acceptance owns execution. TEST-VERIFIABLE.")]
    [Xunit.InlineData("The UI displays 'Acceptance owns execution'. TEST-VERIFIABLE.")]
    public void DescriptiveAcceptanceMentionDoesNotDeclareOwner(string criterion)
    {
        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.None,
            AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion).Kind);
        Xunit.Assert.False(AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Operator owns execution; Acceptance owns execution")]
    [Xunit.InlineData("OPERATOR-OWNED; ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("Operator owns execution; ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("OPERATOR-OWNED; Acceptance owns execution")]
    [Xunit.InlineData("owned by the operator; executed by the acceptance gate")]
    [Xunit.InlineData("OPERATOR-OWNED. ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("Operator owns execution. executed by the acceptance gate")]
    [Xunit.InlineData("executed by the acceptance gate. OPERATOR-OWNED")]
    [Xunit.InlineData("Acceptance executes; OPERATOR-OWNED")]
    public void IndependentExternalDeclarationsAreConflicting(string declarations)
    {
        Xunit.Assert.True(AcceptanceCriterionOwnershipMarker.HasConflictingDeclaredEvidenceOwners(
            $"The runtime behavior is verified. {declarations}. TEST-VERIFIABLE."));
        var marker = AcceptanceCriterionOwnershipMarker.Classify(
            $"The runtime behavior is verified. {declarations}. TEST-VERIFIABLE.");
        Xunit.Assert.NotNull(marker.ConflictingDeclaredOwner);
    }

    [Xunit.Theory]
    [Xunit.InlineData("The report describes acceptance-gate-owned findings. Operator owns execution. TEST-VERIFIABLE.")]
    [Xunit.InlineData("The report describes operator-owned findings. Acceptance owns execution. TEST-VERIFIABLE.")]
    [Xunit.InlineData("The UI displays 'ACCEPTANCE-GATE-OWNED'. Operator owns execution. TEST-VERIFIABLE.")]
    [Xunit.InlineData("The report mentions Operator owns execution. Acceptance owns execution. TEST-VERIFIABLE.")]
    [Xunit.InlineData("Reviewer owns source inspection; Acceptance owns execution. TEST-VERIFIABLE.")]
    public void DescriptiveMentionsAndSourceResponsibilityDoNotConflict(string criterion)
    {
        Xunit.Assert.False(AcceptanceCriterionOwnershipMarker.HasConflictingDeclaredEvidenceOwners(
            criterion));
    }

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
