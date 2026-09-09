using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceVersionTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SupersededPendingEvidenceRemainsHistoricalWithoutBlockingSatisfiedCurrentSpec(bool restart)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe the authoritative requirement");
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Old observation"));
        var old = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Replacement observation"));
        var current = Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionVersion == 2);
        kernel.RecordCriterionEvidence(goal.Id, current.Id, CriterionEvidenceOwner.Operator,
            "candidate-b", "current-observation", current.RequiredScope, true, "Replacement behavior observed");
        if (restart)
        {
            var json = JsonSerializer.Serialize(kernel.ExportSnapshot());
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
            goal = kernel.GetGoal(goal.Id);
        }

        Assert.Empty(goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(old, Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == old.Id));
        Assert.Equal(CriterionEvidenceState.Satisfied,
            Assert.Single(goal.CriterionEvidenceObligations, item => item.Id == current.Id).State);
    }

    [Xunit.Fact]
    public void LateHistoricalReceiptDoesNotSatisfyCurrentRequirement()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe the authoritative requirement");
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Old observation"));
        var old = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Replacement observation"));
        kernel.RecordCriterionEvidence(goal.Id, old.Id, CriterionEvidenceOwner.Operator,
            "candidate-a", "old-observation", old.RequiredScope, true, "Old behavior observed");

        var remaining = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(2, remaining.CriterionVersion);
        Assert.Equal(CriterionEvidenceState.Pending, remaining.State);
        Assert.Equal("Replacement observation", remaining.Criterion);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void SupersessionDoesNotHideMalformedOrUnrecognizedEvidence(bool malformed)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retain unresolved evidence identity");
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Old observation"));
        var old = Assert.Single(goal.CriterionEvidenceObligations);
        kernel.RecordGoalRefinement(goal.Id, OwnedSpec("Replacement observation"));
        var current = Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionVersion == 2);
        var satisfied = kernel.RecordCriterionEvidence(goal.Id, current.Id, CriterionEvidenceOwner.Operator,
            "candidate-b", "current-observation", current.RequiredScope, true, "Replacement behavior observed");
        var unknown = malformed ? old with { Id = "" } : old with { CriterionVersion = 99 };
        var snapshot = kernel.ExportGoalSnapshot(goal.Id) with { CriterionEvidenceObligations = [unknown, satisfied] };

        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).GetGoal(goal.Id);

        var remaining = Assert.Single(restored.OutstandingCriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Pending, remaining.State);
        if (malformed) Assert.Equal(CriterionEvidenceOwner.Unknown, remaining.Owner);
        else Assert.Equal(99, remaining.CriterionVersion);
    }

    private static RefinedSpec OwnedSpec(string criterion) => new(
        "Observe the authoritative behavior", [criterion], VerificationClass.RealWorldDependent, [], [])
    {
        OperatorOwnedAcceptanceCriteria = [criterion]
    };
}
