using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateOwnedCriterionRefinementTests
{
    private const string Candidate = "candidate-c";

    [Xunit.Fact]
    public void UnknownEvidenceOwnerDegradesToWorkerWithParseDiagnostic()
    {
        var output = SpecRefinerPlanner.Parse("""
            {"behavioralContract":"Contract","acceptanceCriteria":[{"text":"Criterion","evidence_owner":"mystery"}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """);

        Xunit.Assert.True(output.IsValid);
        Xunit.Assert.Equal(["Criterion"], output.AcceptanceCriteria);
        Xunit.Assert.Empty(output.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(output.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Contains(output.ParseDiagnostics, item =>
            item.Contains("unrecognized evidence_owner 'mystery'; treated as worker", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task RefinementRecordsStructuredOwnersAndSnapshotsRemainBackwardCompatible()
    {
        const string gateOne = "Focused test A passes.";
        const string gateTwo = "Focused test B passes.";
        const string operatorCriterion = "A deployment observation is recorded.";
        const string workerCriterion = "The source returns the expected value.";
        var response = $$"""
            ```json
            {
              "behavioralContract": "Route each criterion to its declared evidence owner.",
              "acceptanceCriteria": [
                {"text": {{System.Text.Json.JsonSerializer.Serialize(gateOne)}}, "evidence_owner": "acceptance-gate"},
                {"text": {{System.Text.Json.JsonSerializer.Serialize(gateTwo)}}, "evidence_owner": "acceptance_gate"},
                {"text": {{System.Text.Json.JsonSerializer.Serialize(operatorCriterion)}}, "evidence_owner": "operator"},
                {{System.Text.Json.JsonSerializer.Serialize(workerCriterion)}}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        var scenario = CreateRefinementScenario(response);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var goal = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([gateOne, gateTwo], goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal([operatorCriterion], goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        var obligations = goal.CriterionEvidenceObligations.OrderBy(item => item.CriterionIndex).ToArray();
        Xunit.Assert.Equal(3, obligations.Length);
        Xunit.Assert.All(obligations[..2], obligation =>
        {
            Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
            Xunit.Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, obligation.RequiredScope);
            Xunit.Assert.Null(obligation.ExpectedCandidateSha);
        });
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, obligations[2].Owner);
        Xunit.Assert.DoesNotContain(obligations, obligation => obligation.CriterionIndex == 3);

        var snapshot = scenario.Kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot).GetGoal(goal.Id);
        Xunit.Assert.Equal(goal.CriterionEvidenceObligations, restored.CriterionEvidenceObligations);

        var goalSnapshot = Xunit.Assert.Single(snapshot.Goals);
        var currentSpec = goalSnapshot.RefinedSpec!;
        var oldSpec = new RefinedSpecSnapshot(
            currentSpec.BehavioralContract,
            currentSpec.AcceptanceCriteria,
            currentSpec.VerificationClass,
            currentSpec.Decisions,
            currentSpec.OpenQuestions,
            currentSpec.OperatorOwnedAcceptanceCriteria,
            currentSpec.ClarificationAnswerHistory);
        var oldGoalSnapshot = goalSnapshot with
        {
            RefinedSpec = oldSpec,
            RefinedSpecVersions = goalSnapshot.RefinedSpecVersions!
                .Select(version => version with { Spec = oldSpec })
                .ToArray(),
            CriterionEvidenceObligations = null
        };
        var oldRestored = AgentOrchestratorKernel.FromSnapshot(
            snapshot with { Goals = [oldGoalSnapshot] }).GetGoal(goal.Id);
        Xunit.Assert.Empty(oldRestored.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.DoesNotContain(
            oldRestored.CriterionEvidenceObligations,
            obligation => obligation.Owner == CriterionEvidenceOwner.Acceptance);
    }

    [Xunit.Fact]
    public async Task OperatorOwnedFeasibilityAnswerCreatesExactlyOneOperatorObligation()
    {
        const string criterion =
            "Benchmark wall-clock performance on this host while the machine is idle.";
        var response = $$"""
            ```json
            {
              "behavioralContract": "Exercise the requested behavior.",
              "acceptanceCriteria": [{{System.Text.Json.JsonSerializer.Serialize(criterion)}}],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        var scenario = CreateRefinementScenario(response);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var resolved = await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel,
            Xunit.Assert.Single(scenario.Collaboration.Items).CorrelationKey!,
            "OPERATOR-OWNED");

        Xunit.Assert.True(resolved);
        var recordedGoal = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recordedGoal.RefinedSpec!.AcceptanceCriteria);
        var obligation = Xunit.Assert.Single(recordedGoal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
    }

    [Xunit.Fact]
    public void PassingFullGateDischargesOnlyTheBoundCandidate()
    {
        var matching = CreateBoundGateScenario();

        var hold = AcceptanceCriterionEvidence.RecordAndCreateHold(
            matching.Goal,
            Candidate,
            matching.Kernel);

        Xunit.Assert.Null(hold);
        var satisfied = Xunit.Assert.Single(matching.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceState.Satisfied, satisfied.State);
        Xunit.Assert.Equal(Candidate, satisfied.CandidateSha);
        Xunit.Assert.NotNull(satisfied.ReceiptId);

        var mismatched = CreateBoundGateScenario();
        var mismatchHold = AcceptanceCriterionEvidence.RecordAndCreateHold(
            mismatched.Goal,
            "candidate-c2",
            mismatched.Kernel);
        Xunit.Assert.NotNull(mismatchHold);
        Xunit.Assert.Contains("Rebind", mismatchHold!.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains(Candidate, mismatchHold.Reason, StringComparison.Ordinal);
        Xunit.Assert.Equal(
            CriterionEvidenceState.Pending,
            Xunit.Assert.Single(mismatched.Goal.CriterionEvidenceObligations).State);
    }

    [Xunit.Fact]
    public void UnboundGateOwnedObligationDoesNotCreateAnAcceptanceHold()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Await reviewer binding");
        const string criterion = "The full acceptance gate passes.";
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Require deterministic gate evidence.",
            [criterion],
            VerificationClass.TestVerifiable,
            [],
            [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [criterion]
        });

        var hold = AcceptanceCriterionEvidence.RecordAndCreateHold(goal, Candidate, kernel);

        Xunit.Assert.Null(hold);
        Xunit.Assert.Equal(
            CriterionEvidenceState.Pending,
            Xunit.Assert.Single(goal.CriterionEvidenceObligations).State);
    }

    private static (GoalRefinementService Service, AgentOrchestratorKernel Kernel, Goal Goal, FakeCollaborationItemStore Collaboration)
        CreateRefinementScenario(string response)
    {
        var provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
        var collaboration = new FakeCollaborationItemStore();
        var tempDirectory = Directory.CreateTempSubdirectory("gate-owned-refinement-");
        var service = new GoalRefinementService(
            new InMemoryModelProviderRegistry([provider]),
            new ModelFunctionCatalog([
                new ModelFunctionBinding(
                    ModelFunctionPurposes.SpecRefiner,
                    ModelLane.CheapApi,
                    new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]),
            collaboration,
            new SpecRefinerPrecedentStore(Path.Combine(tempDirectory.FullName, "precedents.json")),
            WorkerProfileCatalog.Default());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Integrate the billing system");
        return (service, kernel, goal, collaboration);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateBoundGateScenario()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Require full-gate evidence", [reviewer]);
        const string criterion = "The full acceptance gate passes.";
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Require deterministic gate evidence.",
            [criterion],
            VerificationClass.TestVerifiable,
            [],
            [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [criterion]
        });
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, Candidate);
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: review",
            "tests: pass - deterministic fixture", "commit: none", "blockers: none",
            "findings: []", "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"not-verifiable\",\"evidence\":\"full acceptance gate\"}]",
            "verdict: pass", "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, ReviewedCommit: Candidate));
        return (kernel, goal);
    }
}
