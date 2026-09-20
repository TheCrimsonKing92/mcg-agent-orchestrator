using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsCriterionEvidenceCandidate
{
    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, true, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, false, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, true, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, true, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, false, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, true, true)]
    public void LandingRequiresEvidenceForTheCandidateBeingAccepted(
        CriterionEvidenceOwner owner, bool restart, bool sameCandidate)
    {
        var (kernel, goal) = SimpleGoal("Preserve candidate-bound criterion evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Prove the required behavior"], VerificationClass.TestVerifiable, [], []));
        var version = goal.RefinedSpecVersions.Single(item => !item.IsSuperseded).Version;
        var acceptedSha = new string('a', 40);
        var landingSha = sameCandidate ? acceptedSha : new string('b', 40);
        var scope = owner == CriterionEvidenceOwner.Acceptance
            ? CriterionEvidenceScopes.FullAcceptanceGate : "operator:native-observation";
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 0, version, owner,
            "operator", scope, expectedCandidateSha: acceptedSha);
        var receipt = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            acceptedSha, "accepted-candidate-a", scope, true, "Candidate A observed");
        PassVerification(kernel, goal, goal.Tasks.Single());
        if (restart)
        {
            var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(kernel.ExportSnapshot()))!;
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
            goal = kernel.GetGoal(goal.Id);
        }

        var landCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [], BranchHeadSha: landingSha),
            resolveAcceptanceHeads: _ => (landingSha, new string('c', 40)),
            land: item =>
            {
                landCalls++;
                return new LandingResult(item.Id.Value, item.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            });
        driver.BeginTick(kernel, 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(sameCandidate ? 1 : 0, landCalls);
        if (sameCandidate) Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        else Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(receipt, Assert.Single(goal.CriterionEvidenceObligations));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void ReboundAcceptanceObligationRequiresTheNewGateResult(bool gatePassed, bool restart)
    {
        var (kernel, goal) = SimpleGoal("Accept a repaired candidate with retained evidence history");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["Full acceptance succeeds"], VerificationClass.TestVerifiable, [], []));
        var oldSha = new string('a', 40);
        var newSha = new string('b', 40);
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: oldSha);
        kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner, oldSha, "old-gate",
            mapped.RequiredScope, true, "Old candidate passed");
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, mapped.Owner, "operator",
            mapped.RequiredScope, expectedCandidateSha: newSha);
        PassVerification(kernel, goal, goal.Tasks.Single());
        if (restart)
        {
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
            goal = kernel.GetGoal(goal.Id);
        }

        var landCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(gatePassed, [], BranchHeadSha: newSha),
            resolveAcceptanceHeads: _ => (newSha, new string('c', 40)),
            land: item =>
            {
                landCalls++;
                return new LandingResult(item.Id.Value, item.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            });
        driver.BeginTick(kernel, 1);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(gatePassed ? 1 : 0, landCalls);
        var obligation = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(gatePassed ? CriterionEvidenceState.Satisfied : CriterionEvidenceState.Pending, obligation.State);
        Assert.Equal(gatePassed ? newSha : null, obligation.CandidateSha);
        var historical = Assert.Single(obligation.PriorReceipts!);
        Assert.Equal(oldSha, historical.CandidateSha);
        Assert.Equal("old-gate", historical.ReceiptId);
        Assert.True(historical.Passed);
    }
}
