using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceCandidateTests
{
    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, true)]
    public void NewCandidateRequiresFreshEvidenceAndRetainsPreviousReceipts(CriterionEvidenceOwner owner, bool restart)
    {
        var (kernel, goal, mapped) = Create(owner);
        var accepted = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, true, "A passed");
        Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-b"));
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations(null));

        var pending = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, owner, "operator",
            mapped.RequiredScope, expectedCandidateSha: "candidate-b");
        Assert.Equal(CriterionEvidenceState.Pending, pending.State);
        Assert.Null(pending.CandidateSha);
        var previous = Assert.Single(pending.PriorReceipts!);
        Assert.Equal(accepted.CandidateSha, previous.CandidateSha);
        Assert.Equal(accepted.ReceiptId, previous.ReceiptId);
        Assert.True(previous.Passed);
        if (restart)
        {
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
            goal = kernel.GetGoal(goal.Id);
        }
        Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, true, "A passed"));
        kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-b", "receipt-b-red", mapped.RequiredScope, false, "B failed");
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-b"));
        var completed = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-b", "receipt-b-green", mapped.RequiredScope, true, "B passed after correction");

        Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations("candidate-b"));
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Collection(completed.PriorReceipts!,
            receipt => { Assert.Equal("receipt-a", receipt.ReceiptId); Assert.True(receipt.Passed); },
            receipt => { Assert.Equal("receipt-b-red", receipt.ReceiptId); Assert.False(receipt.Passed); });
    }

    [Xunit.Fact]
    public void RepeatingSatisfiedMappingIsIdempotent()
    {
        var (kernel, goal, mapped) = Create(CriterionEvidenceOwner.Operator);
        var accepted = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, true, "A passed");
        var repeated = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, mapped.Owner, "operator",
            mapped.RequiredScope, expectedCandidateSha: "candidate-a");

        Assert.Equal(accepted, repeated);
        Assert.Null(repeated.PriorReceipts);
        Assert.Single(goal.CriterionEvidenceObligations);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "Failed observation")]
    [Xunit.InlineData(false, "Different observation")]
    public void FailedReceiptIdentityCannotBeRewritten(bool passed, string detail)
    {
        var (kernel, goal, mapped) = Create(CriterionEvidenceOwner.Operator);
        var failed = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, false, "Failed observation");

        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, passed, detail));
        Assert.Equal(failed, Assert.Single(goal.CriterionEvidenceObligations));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, CriterionEvidenceObligation Mapped) Create(CriterionEvidenceOwner owner)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Require fresh candidate-bound evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe behavior", ["Behavior is proved"],
            VerificationClass.RealWorldDependent, [], []));
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, owner, "operator",
            owner == CriterionEvidenceOwner.Acceptance ? CriterionEvidenceScopes.FullAcceptanceGate : "operator observation",
            expectedCandidateSha: "candidate-a");
        return (kernel, goal, mapped);
    }
}
