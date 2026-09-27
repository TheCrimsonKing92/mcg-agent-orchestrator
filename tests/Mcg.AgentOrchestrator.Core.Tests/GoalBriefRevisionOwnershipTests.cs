using Mcg.AgentOrchestrator.Core;

public sealed class GoalBriefRevisionOwnershipTests
{
    [Xunit.Fact]
    public void RewordedMarkerCriterionKeepsAcceptanceOwnership()
    {
        const string original = "A test asserts X. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        const string revised = "A test asserts X and Y. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], [original]));

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Xunit.Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, obligation.RequiredScope);
        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.RequiredScope == "ownership mapping required");
    }

    [Xunit.Theory]
    [Xunit.InlineData("Acceptance executes")]
    [Xunit.InlineData("ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("executed by the acceptance gate")]
    public void RewordedCreationTimeGateMarkerKeepsAcceptanceOwnership(string gateMarker)
    {
        const string original = "The original behavior works. Acceptance executes.";
        var revised = $"The focused behavior works. {gateMarker}.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], [original]));

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        Xunit.Assert.Equal([revised], goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Xunit.Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, obligation.RequiredScope);
        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.RequiredScope == "ownership mapping required");
    }

    [Xunit.Fact]
    public void UnchangedCriteriaKeepOwnersAndNewOperatorPhraseAssignsOperator()
    {
        const string gate = "The gate observes the unchanged result.";
        const string operated = "The observation remains unchanged.";
        const string added = "The operator owns this observation.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(gate, operated));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([gate, operated], [gate], [operated]));

        kernel.ReviseGoalBrief(goal.Id, Brief(gate, operated, added));

        Xunit.Assert.Equal([gate], goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal([operated, added], goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        var obligations = goal.CriterionEvidenceObligations
            .Where(item => item.CriterionVersion == 2)
            .OrderBy(item => item.CriterionIndex)
            .ToArray();
        Xunit.Assert.Equal(
            [CriterionEvidenceOwner.Acceptance, CriterionEvidenceOwner.Operator, CriterionEvidenceOwner.Operator],
            obligations.Select(item => item.Owner));
    }

    [Xunit.Fact]
    public void ChangedCriterionCountSkipsIndexCarryAndRecordsDiagnostic()
    {
        var original = Enumerable.Range(0, 15)
            .Select(index => $"Original criterion {index}.")
            .ToArray();
        var revised = Enumerable.Range(0, 4)
            .Select(index => $"Revised criterion {index}.")
            .Append("The Reviewer confirms by reading the diff that the change is narrow. TEST-VERIFIABLE by reading.")
            .ToArray();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec(original, [], [original[4]]));

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 4);
        var diagnostic = Xunit.Assert.Single(goal.RefinementOwnershipDiagnostics);
        Xunit.Assert.Equal(2, diagnostic.CriterionVersion);
        Xunit.Assert.Equal(4, diagnostic.CriterionIndex);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, diagnostic.PriorOwner);
        Xunit.Assert.Contains("criterion count changed", diagnostic.Message);
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
