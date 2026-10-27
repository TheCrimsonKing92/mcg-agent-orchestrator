using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeclaredOwnerAuthorityRefinementTests
{
    [Xunit.Theory]
    [Xunit.InlineData("operator")]
    [Xunit.InlineData("worker")]
    public async Task AcceptanceDeclarationOverridesGeneratedOwner(string modelOwner)
    {
        const string criterion = "The fallback is preserved. Reviewer owns source inspection; Acceptance owns execution. TEST-VERIFIABLE.";
        var goal = await Refine(criterion, modelOwner);

        Xunit.Assert.Equal([criterion], goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance,
            Xunit.Assert.Single(goal.CriterionEvidenceObligations).Owner);
        AssertSingleOverride(goal,
            $"Spec refiner owner overridden: declared criterion 1 declares acceptance-gate; refiner claimed {modelOwner}");
    }

    [Xunit.Theory]
    [Xunit.InlineData("Operator owns execution; Acceptance owns execution")]
    [Xunit.InlineData("OPERATOR-OWNED; ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("Operator owns execution; ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("OPERATOR-OWNED; Acceptance owns execution")]
    [Xunit.InlineData("Operator owns execution. executed by the acceptance gate")]
    public async Task ConflictingExternalDeclarationsRequireClarification(string declarations)
    {
        var criterion = $"The runtime behavior is verified. {declarations}. TEST-VERIFIABLE.";
        var goal = await Refine(criterion, "worker", expectClarification: true);

        Xunit.Assert.Empty(goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.CriterionEvidenceObligations);
        var question = Xunit.Assert.Single(goal.RefinedSpec.OpenQuestions);
        Xunit.Assert.Equal(criterion, question.Criterion);
        Xunit.Assert.Contains("both Operator and Acceptance", question.Question, StringComparison.Ordinal);
        Xunit.Assert.Contains("Revise the versioned criterion", question.Question, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task ExactTextFallbackKeepsExplicitAcceptanceAuthority()
    {
        const string criterion = "The fallback is preserved. Acceptance owns execution. TEST-VERIFIABLE.";
        var goal = await Refine(criterion, "acceptance-gate", declaredIndex: null);

        Xunit.Assert.Equal([criterion], goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.RefinedSpec.OpenQuestions);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance,
            Xunit.Assert.Single(goal.CriterionEvidenceObligations).Owner);
    }

    [Xunit.Fact]
    public async Task ReviewerDeclarationOverridesOperatorClaim()
    {
        const string criterion =
            "The Reviewer confirms by reading the diff that X holds. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";
        var goal = await Refine(criterion, "operator");

        Xunit.Assert.Empty(goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations,
            obligation => obligation.Owner == CriterionEvidenceOwner.Operator);
        AssertSingleOverride(goal,
            "Spec refiner owner overridden: declared criterion 1 declares worker; refiner claimed operator");
    }

    [Xunit.Fact]
    public async Task AcceptanceExecutionOverridesOperatorClaim()
    {
        const string criterion =
            "The focused suite passes. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
        var goal = await Refine(criterion, "operator");

        Xunit.Assert.Empty(goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal([criterion], goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Xunit.Assert.Equal(0, obligation.CriterionIndex);
        AssertSingleOverride(goal,
            "Spec refiner owner overridden: declared criterion 1 declares acceptance-gate; refiner claimed operator");
    }

    [Xunit.Fact]
    public async Task WorkerDeclarationRejectsGateClaim()
    {
        const string criterion =
            "The Reviewer confirms by reading the diff that X holds. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";
        var goal = await Refine(criterion, "acceptance-gate");

        Xunit.Assert.Empty(goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.CriterionEvidenceObligations);
        AssertSingleOverride(goal,
            "Spec refiner owner overridden: declared criterion 1 declares worker; refiner claimed acceptance-gate");
    }

    [Xunit.Theory]
    [Xunit.InlineData("operator", true)]
    [Xunit.InlineData("worker", false)]
    public async Task NoOwnerSentenceKeepsModelClaim(string modelOwner, bool operatorExpected)
    {
        const string criterion =
            "The Reviewer confirms by reading the diff that X holds. TEST-VERIFIABLE by reading.";
        var goal = await Refine(criterion, modelOwner);

        if (operatorExpected)
        {
            Xunit.Assert.Equal([criterion], goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
            Xunit.Assert.Equal(CriterionEvidenceOwner.Operator,
                Xunit.Assert.Single(goal.CriterionEvidenceObligations).Owner);
        }
        else
        {
            Xunit.Assert.Empty(goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
            Xunit.Assert.Empty(goal.CriterionEvidenceObligations);
        }

        Xunit.Assert.Empty(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.DoesNotContain(goal.Timeline,
            evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
                   evt.Message.Contains("Spec refiner owner overridden", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task OperatorDeclarationKeepsTextMarkerPrecedence()
    {
        const string criterion =
            "The live result is observed. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.";
        var goal = await Refine(criterion, "acceptance-gate");

        Xunit.Assert.Equal([criterion], goal.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator,
            Xunit.Assert.Single(goal.CriterionEvidenceObligations).Owner);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains(
                "text marker claims operator for declared criterion 1, refiner claims acceptance-gate",
                StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(goal.Timeline,
            evt => evt.Message.Contains("Spec refiner owner overridden", StringComparison.Ordinal));
    }

    private static void AssertSingleOverride(Goal goal, string expected)
    {
        var decision = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Spec refiner owner overridden", StringComparison.Ordinal)));
        Xunit.Assert.Contains(expected, decision.Message, StringComparison.Ordinal);
    }

    private static async Task<Goal> Refine(
        string criterion, string modelOwner, bool expectClarification = false, int? declaredIndex = 1)
    {
        var objective = $"""
            Implement the focused behavior.

            ## Acceptance criteria

            1. {criterion}
            """;
        var indexProperty = declaredIndex is { } index ? $"\"declared_index\": {index}," : string.Empty;
        var response = $$"""
            {
              "behavioralContract": "Implement the focused behavior.",
              "acceptanceCriteria": [
                {"text": {{JsonSerializer.Serialize(criterion)}}, {{indexProperty}} "evidence_owner": {{JsonSerializer.Serialize(modelOwner)}}}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
        var tempDirectory = Directory.CreateTempSubdirectory("declared-owner-refinement-");
        var service = new GoalRefinementService(
            new InMemoryModelProviderRegistry([provider]),
            new ModelFunctionCatalog([
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]),
            new FakeCollaborationItemStore(),
            new SpecRefinerPrecedentStore(Path.Combine(tempDirectory.FullName, "precedents.json")),
            WorkerProfileCatalog.Default(),
            rawOutputDirectory: Path.Combine(tempDirectory.FullName, "logs"));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(objective);

        var result = await service.RefineAsync(kernel, goal.Id);

        Xunit.Assert.NotNull(provider.LastRequest);
        Xunit.Assert.Equal(expectClarification ? RefinementOutcome.AwaitingClarification : RefinementOutcome.AutoRefined,
            result.Outcome);

        return kernel.GetGoal(goal.Id);
    }
}
