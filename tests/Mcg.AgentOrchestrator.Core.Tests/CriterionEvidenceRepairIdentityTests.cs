using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceRepairIdentityTests
{
    [Xunit.Fact]
    public void RepairOfCanonicalMalformedIdentityKeepsTheStoredReplacementPendingAcrossRestore()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Repair canonical identity");
        var spec = Spec("full acceptance receipt");
        kernel.RecordGoalRefinement(goal.Id, spec);
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "operator", 
            CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");
        var malformed = kernel.ExportGoalSnapshot(goal.Id) with
        {
            CriterionEvidenceObligations = [goal.CriterionEvidenceObligations.Single() with { Provenance = string.Empty }]
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([malformed], []));

        var replacement = kernel.RepairMalformedCriterionEvidenceObligation(
            goal.Id, "criterion-v1-0", 0, 1, CriterionEvidenceOwner.Acceptance, "repairer", "restore authority",
            CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");
        var repaired = kernel.GetGoal(goal.Id);

        Assert.Equal(replacement, Assert.Single(repaired.CriterionEvidenceObligations));
        Assert.Equal(CriterionEvidenceOwner.Acceptance, replacement.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, replacement.State);
        Assert.Single(repaired.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        var restored = Restore(kernel).GetGoal(goal.Id);
        Assert.Equal(replacement, Assert.Single(restored.CriterionEvidenceObligations));
        Assert.Single(restored.GetOutstandingCriterionEvidenceObligations("candidate-a"));
    }

    [Xunit.Fact]
    public void MissingPersistedRepairTargetRemainsAnActionableUnknownObligationAcrossRepeatedRestore()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Repair missing target");
        kernel.RecordGoalRefinement(goal.Id, Spec("full acceptance receipt"));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id) with
        {
            CriterionEvidenceObligations =
            [new CriterionEvidenceObligation("malformed-persisted-0", 99, 99, "corrupt", CriterionEvidenceOwner.Unknown,
                CriterionEvidenceState.Pending, "ownership mapping required", "malformed persisted obligation", DateTimeOffset.UnixEpoch)]
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        var source = Assert.Single(kernel.GetGoal(goal.Id).OutstandingCriterionEvidenceObligations);
        var replacement = kernel.RepairMalformedCriterionEvidenceObligation(
            goal.Id, source.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "repairer", "restore authority",
            CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");
        var withoutReplacement = kernel.ExportGoalSnapshot(goal.Id) with
        {
            CriterionEvidenceObligations = kernel.ExportGoalSnapshot(goal.Id).CriterionEvidenceObligations!
                .Where(item => item.Id != replacement.Id).ToArray()
        };

        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([withoutReplacement], []));
        var unknown = Assert.Single(restored.GetGoal(goal.Id).OutstandingCriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceOwner.Unknown, unknown.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, unknown.State);
        Assert.Contains("repair target", unknown.Provenance, StringComparison.Ordinal);
        Assert.Single(Restore(restored).GetGoal(goal.Id).OutstandingCriterionEvidenceObligations);
    }

    [Xunit.Fact]
    public void RewordedExplicitMappingRemainsUnknownUntilAnAuditedRemappingReconcilesIt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retain reworded mapping");
        kernel.RecordGoalRefinement(goal.Id, Spec("full acceptance receipt"));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "operator",
            CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");

        kernel.RecordGoalRefinement(goal.Id, Spec("reworded full acceptance receipt"));

        var unresolved = Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceOwner.Unknown, unresolved.Owner);
        Assert.Contains("unresolved during refinement", unresolved.Provenance, StringComparison.Ordinal);
        Assert.Single(Restore(kernel).GetGoal(goal.Id).OutstandingCriterionEvidenceObligations);
    }

    [Xunit.Fact]
    public void ExplicitMappingWinsOverNewSpecDefaultAndStartsTheNewVersionPending()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve explicit ownership");
        var criterion = "full acceptance receipt";
        kernel.RecordGoalRefinement(goal.Id, Spec(criterion));
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "operator",
            CriterionEvidenceScopes.FullAcceptanceGate, "full-gate", "candidate-a");

        kernel.RecordGoalRefinement(goal.Id, Spec(criterion, operatorOwned: true));

        var current = Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Equal(2, current.CriterionVersion);
        Assert.Equal(CriterionEvidenceOwner.Acceptance, current.Owner);
        Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, current.RequiredScope);
        Assert.Equal("candidate-a", current.ExpectedCandidateSha);
        Assert.Single(Restore(kernel).GetGoal(goal.Id).GetOutstandingCriterionEvidenceObligations("candidate-a"));
    }

    private static AgentOrchestratorKernel Restore(AgentOrchestratorKernel kernel) =>
        AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);

    private static RefinedSpec Spec(string criterion, bool operatorOwned = false) =>
        new("Verify the candidate", [criterion], VerificationClass.TestVerifiable, [], [])
        {
            OperatorOwnedAcceptanceCriteria = operatorOwned ? [criterion] : []
        };
}
