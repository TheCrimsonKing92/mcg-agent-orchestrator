using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceRestoreTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, "absent")]
    [Xunit.InlineData(false, "empty")]
    [Xunit.InlineData(false, "partial")]
    [Xunit.InlineData(true, "absent")]
    [Xunit.InlineData(true, "empty")]
    [Xunit.InlineData(true, "partial")]
    public void RestoreRetainsEverySpecOwnedObligation(bool legacy, string persistedShape)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Observe both externally visible behaviors");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Observe the saved result and notification", ["Saved result is visible", "Notification arrives"],
            VerificationClass.RealWorldDependent, [], [])
        {
            OperatorOwnedAcceptanceCriteria = ["Saved result is visible", "Notification arrives"]
        });
        var observed = goal.CriterionEvidenceObligations[0];
        var satisfied = kernel.RecordCriterionEvidence(goal.Id, observed.Id, CriterionEvidenceOwner.Operator,
            "candidate-a", "saved-result", observed.RequiredScope, true, "Saved result observed");
        var original = kernel.ExportGoalSnapshot(goal.Id);
        var snapshot = original with
        {
            RefinedSpecVersions = legacy ? null : original.RefinedSpecVersions,
            CriterionEvidenceObligations = persistedShape switch
            {
                "absent" => null,
                "empty" => [],
                _ => [satisfied]
            }
        };

        var restored = Restore(snapshot);

        Assert.Equal(2, restored.CriterionEvidenceObligations.Count);
        Assert.Contains(restored.GetOutstandingCriterionEvidenceObligations("candidate-a"),
            item => item.Criterion == "Notification arrives" && item.Owner == CriterionEvidenceOwner.Operator);
        if (persistedShape == "partial")
        {
            Assert.Equal(satisfied, Assert.Single(restored.CriterionEvidenceObligations, item => item.Id == satisfied.Id));
            Assert.Single(restored.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        }
        else Assert.Equal(2, restored.GetOutstandingCriterionEvidenceObligations("candidate-a").Count);
        Assert.Equal(JsonSerializer.Serialize(restored.CriterionEvidenceObligations),
            JsonSerializer.Serialize(Restore(restored.ToSnapshot()).CriterionEvidenceObligations));
    }

    private static Goal Restore(GoalSnapshot snapshot) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])).GetGoal(new GoalId(snapshot.Id));
}
