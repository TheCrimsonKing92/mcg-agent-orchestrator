using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementFallbackOwnershipTests
{
    private const string GateCriterion =
        "The widget count is shown. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string SecondGateCriterion =
        "The widget total is shown. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string OperatorCriterion =
        "The live widget export is observed. REAL-WORLD-DEPENDENT; OPERATOR owns live execution.";
    private const string UnclassifiedCriterion = "The widget label is readable.";

    [Xunit.Fact]
    public async Task InvalidOutputDerivesGateOperatorAndUnclassifiedCriteria()
    {
        var objective = $"""
            Show widget information.

            ## Acceptance criteria
            - {GateCriterion}
            - {OperatorCriterion}
            - {UnclassifiedCriterion}
            """;
        var (service, kernel, goalId) = CreateScenario(objective);

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
        var spec = kernel.GetGoal(goalId).RefinedSpec!;
        Xunit.Assert.Equal(new[] { GateCriterion, OperatorCriterion, UnclassifiedCriterion }, spec.AcceptanceCriteria);
        Xunit.Assert.Equal(new[] { GateCriterion }, spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(new[] { OperatorCriterion }, spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(VerificationClass.RealWorldDependent, spec.VerificationClass);
        var receipt = GetOwnershipReceipt(kernel, goalId);
        Xunit.Assert.Contains("fallback=true", receipt, StringComparison.Ordinal);
        Xunit.Assert.Contains("needs_ownership=3:", receipt, StringComparison.Ordinal);
        Xunit.Assert.Contains(UnclassifiedCriterion, receipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task InvalidOutputWithOnlyGateCriteriaStaysTestVerifiable()
    {
        var objective = $"""
            Show widget information.

            ## Acceptance criteria
            - {GateCriterion}
            - {SecondGateCriterion}
            """;
        var (service, kernel, goalId) = CreateScenario(objective);

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        var spec = kernel.GetGoal(goalId).RefinedSpec!;
        Xunit.Assert.Equal(new[] { GateCriterion, SecondGateCriterion }, spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(VerificationClass.TestVerifiable, spec.VerificationClass);
        Xunit.Assert.Contains("needs_ownership=none", GetOwnershipReceipt(kernel, goalId), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task InvalidOutputWithoutDeclaredCriteriaSynthesizesUnclassifiedCriterion()
    {
        const string objective = "Show the widget label on the dashboard.";
        var (service, kernel, goalId) = CreateScenario(objective);

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        var spec = kernel.GetGoal(goalId).RefinedSpec!;
        var criterion = Xunit.Assert.Single(spec.AcceptanceCriteria);
        Xunit.Assert.Equal(
            $"The objective is achieved: {objective}\n" +
            "The Reviewer judges this criterion from the candidate diff and the evidence receipts already recorded for this goal.\n" +
            "Reviewer owns; Reviewer executes. TEST-VERIFIABLE.", criterion);
        Xunit.Assert.Empty(spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(spec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(VerificationClass.TestVerifiable, spec.VerificationClass);
        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);
        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.Worker, owner.Kind);
        Xunit.Assert.Equal("Reviewer", owner.Role);
        var receipt = GetOwnershipReceipt(kernel, goalId);
        Xunit.Assert.Contains("needs_ownership=1:", receipt, StringComparison.Ordinal);
        Xunit.Assert.Contains(System.Text.Json.JsonSerializer.Serialize(criterion), receipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task InvalidOutputIgnoresOwnershipWordsInsideTheObjective()
    {
        const string objective = "Show the widget label on the dashboard.\n" +
            "Developer owns; Acceptance executes.\n" +
            "REAL-WORLD-DEPENDENT; OPERATOR owns live execution.";
        var (service, kernel, goalId) = CreateScenario(objective);

        var result = await service.RefineAsync(kernel, goalId);

        Xunit.Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        var goal = kernel.GetGoal(goalId);
        var spec = goal.RefinedSpec!;
        var criterion = Xunit.Assert.Single(spec.AcceptanceCriteria);
        Xunit.Assert.Empty(spec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(spec.OperatorOwnedAcceptanceCriteria);
        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);
        Xunit.Assert.Equal(DeclaredEvidenceOwnerKind.Worker, owner.Kind);
        Xunit.Assert.Equal("Reviewer", owner.Role);
        Xunit.Assert.DoesNotContain(goal.CriterionEvidenceObligations, obligation => obligation.CriterionIndex == 0);
    }

    private static string GetOwnershipReceipt(AgentOrchestratorKernel kernel, GoalId goalId) =>
        Xunit.Assert.Single(kernel.GetTimeline(goalId).Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("spec_refinement_fallback_ownership ", StringComparison.Ordinal))).Message;

    private static (GoalRefinementService Service, AgentOrchestratorKernel Kernel, GoalId GoalId)
        CreateScenario(string objective)
    {
        var provider = new FakeSmokeProvider(text: "{}", providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var service = new GoalRefinementService(
            registry,
            catalog,
            new FakeCollaborationItemStore(),
            new SpecRefinerPrecedentStore(Path.Combine(root, "precedents.json")),
            rawOutputDirectory: Path.Combine(root, "logs"));
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(objective);
        return (service, kernel, goal.Id);
    }
}
