using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static ConductorDriverTests;

// Parallel-safe: kernel-only state owned by each test.
public sealed class ResolvedCandidateObligationRebinderTests
{
    [Fact]
    public void Rebind_ChangesOnlyPendingAcceptanceObligationsOnOtherHeads()
    {
        var (kernel, goal) = SimpleGoal();
        var oldHead = new string('a', 40);
        var newHead = new string('b', 40);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["first", "second", "third", "operator", "already bound"], VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < 5; index++)
            kernel.MapCriterionEvidenceOwner(goal.Id, index, 1,
                index == 3 ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance, "seed",
                index == 3 ? "operator observation" : CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: index == 4 ? newHead : oldHead);
        var before = goal.CriterionEvidenceObligations.ToArray();

        var changed = ResolvedCandidateObligationRebinder.Rebind(kernel, goal, newHead);

        Assert.Equal(new[] { 0, 1, 2 }, changed.Select(item => item.CriterionIndex));
        for (var index = 0; index < 3; index++)
        {
            var obligation = Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionIndex == index);
            Assert.Equal(before[index].Id, obligation.Id);
            Assert.Equal(before[index].CriterionVersion, obligation.CriterionVersion);
            Assert.Equal(newHead, obligation.ExpectedCandidateSha);
            Assert.Contains("conductor ratchet conflict resolution", obligation.Provenance);
        }
        Assert.Equal(before[3], Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionIndex == 3));
        Assert.Equal(before[4], Assert.Single(goal.CriterionEvidenceObligations, item => item.CriterionIndex == 4));
        Assert.Empty(ResolvedCandidateObligationRebinder.Rebind(kernel, goal, newHead));
    }
}
