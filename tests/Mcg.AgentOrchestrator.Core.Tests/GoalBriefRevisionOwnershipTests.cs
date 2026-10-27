using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each scenario owns an in-memory kernel and snapshot.
public sealed class GoalBriefRevisionOwnershipTests
{
    [Xunit.Theory]
    [Xunit.InlineData("The result is observed. Operator owns execution; Acceptance owns execution. TEST-VERIFIABLE.",
        "both Operator and Acceptance")]
    [Xunit.InlineData("The live behavior is observed. REAL-WORLD-DEPENDENT.",
        "without an authoritative evidence owner")]
    public void UnsafeOwnership_Revision_RequiresClarificationAfterReload(string revised, string diagnostic)
    {
        const string original = "The result is computed. Developer owns; Developer executes. TEST-VERIFIABLE.";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], []));
        Xunit.Assert.False(goal.RefinedSpec!.HasOpenQuestions);
        Xunit.Assert.Empty(goal.CriterionEvidenceObligations);

        kernel.ReviseGoalBrief(goal.Id, Brief(revised));

        var spec = goal.RefinedSpec!;
        Xunit.Assert.Equal([revised], spec.AcceptanceCriteria);
        Xunit.Assert.Empty(spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.CriterionEvidenceObligations);
        Xunit.Assert.True(spec.HasOpenQuestions);
        var question = Xunit.Assert.Single(spec.OpenQuestions);
        Xunit.Assert.Equal(revised, question.Criterion);
        Xunit.Assert.Equal("ownership-lifecycle", question.ForkKind);
        Xunit.Assert.Contains(diagnostic, question.Question, StringComparison.Ordinal);
        Xunit.Assert.Contains("Revise the versioned criterion", question.Question, StringComparison.Ordinal);
        Xunit.Assert.Contains("criterion-evidence-map", question.Question, StringComparison.Ordinal);
        Xunit.Assert.StartsWith("ownership-v2-", question.TopicKey);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetGoal(goal.Id);
        Xunit.Assert.True(restored.RefinedSpec!.HasOpenQuestions);
        Xunit.Assert.Equal(question, Xunit.Assert.Single(restored.RefinedSpec.OpenQuestions));
        Xunit.Assert.Equal(2, restored.AuthoritativeRefinedSpecVersion!.Version);
        Xunit.Assert.Empty(restored.CriterionEvidenceObligations);
    }

    [Xunit.Fact]
    public void CorrectedOwnership_Revision_ReplacesOnlyDerivedOwnershipQuestion()
    {
        const string original = "The result is computed. TEST-VERIFIABLE.";
        const string unsafeCriterion = "The live behavior is observed. REAL-WORLD-DEPENDENT.";
        const string corrected = "The result is observed. Acceptance owns execution. TEST-VERIFIABLE.";
        var unrelated = new RefinedSpecOpenQuestion("source-question", "Which algorithm?", "behavior", "Open");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(original));
        kernel.SetGoalRefinedSpec(goal.Id, Spec([original], []) with { OpenQuestions = [unrelated] });
        kernel.ReviseGoalBrief(goal.Id, Brief(unsafeCriterion));
        Xunit.Assert.Equal(2, goal.RefinedSpec!.OpenQuestions.Count);

        kernel.ReviseGoalBrief(goal.Id, Brief(corrected));

        Xunit.Assert.Equal(unrelated, Xunit.Assert.Single(goal.RefinedSpec!.OpenQuestions));
        Xunit.Assert.Equal([corrected], goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(3, obligation.CriterionVersion);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        var historical = goal.RefinedSpecVersions.Single(version => version.Version == 2);
        Xunit.Assert.True(historical.Spec.HasOpenQuestions);
        Xunit.Assert.Equal(2, historical.Spec.OpenQuestions.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Developer owns; Acceptance executes.")]
    [Xunit.InlineData("Acceptance owns execution.")]
    [Xunit.InlineData("Reviewer owns source inspection; Acceptance owns execution.")]
    public void RewordedMarkerCriterionKeepsAcceptanceOwnership(string declaration)
    {
        const string original = "A test asserts X. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        var revised = $"A test asserts X and Y. {declaration} TEST-VERIFIABLE.";
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
