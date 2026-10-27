using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each scenario uses its own in-memory kernel and goal.
public sealed class GoalBriefRevisionWorkerOwnershipTests
{
    [Xunit.Fact]
    public void AcceptanceCriterionRevisedToReviewerOwnerCarriesNoUnknownObligation()
    {
        const string original = "The diff stays narrow. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        const string revised = "The diff stays narrow. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], [original]));

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        AssertWorkerOwnership(goal, CriterionEvidenceOwner.Acceptance, "Reviewer");
    }

    [Xunit.Theory]
    [Xunit.InlineData("Tester")]
    [Xunit.InlineData("Reviewer")]
    public void OperatorCriterionRevisedToWorkerOwnerCarriesNoUnknownObligation(string role)
    {
        const string original = "The host reports the state. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.";
        var revised = $"The host reports the state. {role} owns; {role} executes. TEST-VERIFIABLE.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], [], [original]));

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        AssertWorkerOwnership(goal, CriterionEvidenceOwner.Operator, role);
    }

    [Xunit.Fact]
    public void RevisedOwnerDeclarationControlsCarryOutcome()
    {
        var unmarked = ReviseAcceptanceCriterion("The revised result is recorded.");
        var unknown = Xunit.Assert.Single(unmarked.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Unknown, unknown.Owner);
        Xunit.Assert.Equal("ownership mapping required", unknown.RequiredScope);

        var operated = ReviseAcceptanceCriterion(
            "The revised result is observed. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.");
        var operatorObligation = Xunit.Assert.Single(operated.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, operatorObligation.Owner);
        Xunit.Assert.DoesNotContain(operated.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.Owner == CriterionEvidenceOwner.Unknown);

        var reviewed = ReviseAcceptanceCriterion(
            "The diff stays narrow. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.");
        Xunit.Assert.DoesNotContain(reviewed.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
    }

    private static Goal ReviseAcceptanceCriterion(string revised)
    {
        const string original = "The diff stays narrow. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], [original]));

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        return goal;
    }

    private static void AssertWorkerOwnership(Goal goal, CriterionEvidenceOwner priorOwner, string role)
    {
        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations,
            item => item.RequiredScope == "ownership mapping required");
        var historical = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 1 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(priorOwner, historical.Owner);
        var diagnostic = Xunit.Assert.Single(goal.RefinementOwnershipDiagnostics);
        Xunit.Assert.Equal(2, diagnostic.CriterionVersion);
        Xunit.Assert.Equal(0, diagnostic.CriterionIndex);
        Xunit.Assert.Equal(1, diagnostic.PriorCriterionVersion);
        Xunit.Assert.Equal(priorOwner, diagnostic.PriorOwner);
        Xunit.Assert.Contains(priorOwner.ToString(), diagnostic.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(role, diagnostic.Message, StringComparison.Ordinal);
    }

    private static RefinedSpec Spec(
        IReadOnlyList<string> criteria,
        IReadOnlyList<string> gateOwned,
        IReadOnlyList<string>? operatorOwned = null) =>
        new("Observe revised ownership", criteria, VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = gateOwned,
            OperatorOwnedAcceptanceCriteria = operatorOwned ?? []
        };

    private static string Brief(params IReadOnlyList<string> criteria) =>
        "Brief.\n\n## Acceptance criteria\n\n" +
        string.Join("\n", criteria.Select((criterion, index) => $"{index + 1}. {criterion}"));
}
