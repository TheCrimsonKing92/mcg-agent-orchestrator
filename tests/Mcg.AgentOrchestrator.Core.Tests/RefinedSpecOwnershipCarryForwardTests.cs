using Mcg.AgentOrchestrator.Core;

public sealed class RefinedSpecOwnershipCarryForwardTests
{
    [Xunit.Fact]
    public void RecordRefinedSpecKeepsNonWorkerOwnerAsUnknownWhenRewrittenTextMatchesNoList()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Keep ownership through a brief revision");
        var recordedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        const string priorCriterion = "Acceptance observes the result.";
        const string rewrittenCriterion = "The gate records the result.";
        var original = Spec(["Worker builds the feature.", priorCriterion], [priorCriterion]);
        var revised = Spec(["Worker builds the feature.", rewrittenCriterion], [priorCriterion]);

        goal.RecordRefinedSpec(original, recordedAt);
        goal.RecordRefinedSpec(revised, recordedAt.AddMinutes(1));

        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 1);
        Xunit.Assert.Equal(rewrittenCriterion, obligation.Criterion);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Unknown, obligation.Owner);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Xunit.Assert.Equal("ownership mapping required", obligation.RequiredScope);

        var diagnostic = Xunit.Assert.Single(goal.RefinementOwnershipDiagnostics);
        Xunit.Assert.Equal(2, diagnostic.CriterionVersion);
        Xunit.Assert.Equal(1, diagnostic.CriterionIndex);
        Xunit.Assert.Equal(1, diagnostic.PriorCriterionVersion);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, diagnostic.PriorOwner);
    }

    [Xunit.Fact]
    public void ExactListMatchKeepsItsDeclaredOwnerWithoutDiagnostic()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Honor the revised ownership list");
        const string priorCriterion = "Acceptance observes the old result.";
        const string revisedCriterion = "Operator observes the new result.";
        goal.RecordRefinedSpec(Spec([priorCriterion], [priorCriterion]), DateTimeOffset.UtcNow);
        goal.RecordRefinedSpec(Spec([revisedCriterion], [], [revisedCriterion]), DateTimeOffset.UtcNow);

        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
        Xunit.Assert.Empty(goal.RefinementOwnershipDiagnostics);
    }

    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator)]
    public void RewrittenExplicitMappingRequiresCurrentVersionOwnership(CriterionEvidenceOwner priorOwner)
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Keep explicit ownership through a rewrite");
        var recordedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
        goal.RecordRefinedSpec(Spec(["Original criterion."], []), recordedAt);
        goal.MapCriterionEvidenceOwner(0, 1, priorOwner, "operator", recordedAt,
            priorOwner == CriterionEvidenceOwner.Acceptance
                ? CriterionEvidenceScopes.FullAcceptanceGate
                : "operator observation", "mapping", "candidate-a");

        goal.RecordRefinedSpec(Spec(["Rewritten criterion."], []), recordedAt.AddMinutes(1));

        var historical = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 1 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Unknown, historical.Owner);
        Xunit.Assert.Contains("unresolved during refinement", historical.Provenance);
        var current = Xunit.Assert.Single(goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == 2 && item.CriterionIndex == 0);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Unknown, current.Owner);
        Xunit.Assert.Equal("ownership mapping required", current.RequiredScope);
        Xunit.Assert.Equal(current, Xunit.Assert.Single(goal.OutstandingCriterionEvidenceObligations));
        var diagnostic = Xunit.Assert.Single(goal.RefinementOwnershipDiagnostics);
        Xunit.Assert.Equal(0, diagnostic.CriterionIndex);
        Xunit.Assert.Equal(priorOwner, diagnostic.PriorOwner);
    }

    private static RefinedSpec Spec(
        IReadOnlyList<string> criteria,
        IReadOnlyList<string> acceptanceOwned,
        IReadOnlyList<string>? operatorOwned = null) => new(
        "Observe the authoritative behavior", criteria, VerificationClass.TestVerifiable, [], [])
    {
        AcceptanceGateOwnedAcceptanceCriteria = acceptanceOwned,
        OperatorOwnedAcceptanceCriteria = operatorOwned ?? []
    };
}
