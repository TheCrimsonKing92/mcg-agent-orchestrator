using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceReceiptIdentityTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, "Observed the required behavior.")]
    [Xunit.InlineData(true, "A different observation reused the receipt id.")]
    public void ConflictingPayloadCannotReplayAnAcceptedReceipt(bool passed, string detail)
    {
        var (kernel, goal, accepted) = AcceptedObservation();

        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(
            goal.Id, accepted.Id, CriterionEvidenceOwner.Operator, accepted.CandidateSha!,
            accepted.ReceiptId!, accepted.RequiredScope, passed, detail));

        Assert.Equal(accepted, Assert.Single(goal.CriterionEvidenceObligations));
    }

    [Xunit.Fact]
    public void IdenticalAcceptedReceiptReplayIsIdempotent()
    {
        var (kernel, goal, accepted) = AcceptedObservation();
        var replayed = kernel.RecordCriterionEvidence(
            goal.Id, accepted.Id, CriterionEvidenceOwner.Operator, accepted.CandidateSha!,
            accepted.ReceiptId!, accepted.RequiredScope, true, accepted.Detail!);

        Assert.Equal(accepted, replayed);
        Assert.Single(goal.CriterionEvidenceObligations);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, CriterionEvidenceObligation Accepted) AcceptedObservation()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retain authentic operator evidence");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Observe the current candidate", ["The required behavior is observed."],
            VerificationClass.RealWorldDependent, [], []));
        var mapped = kernel.MapCriterionEvidenceOwner(
            goal.Id, 0, 1, CriterionEvidenceOwner.Operator, "operator",
            requiredScope: "public adapter observation", expectedCandidateSha: new string('a', 40));
        var accepted = kernel.RecordCriterionEvidence(
            goal.Id, mapped.Id, CriterionEvidenceOwner.Operator, new string('a', 40),
            "observation-1", mapped.RequiredScope, true, "Observed the required behavior.");
        return (kernel, goal, accepted);
    }
}
