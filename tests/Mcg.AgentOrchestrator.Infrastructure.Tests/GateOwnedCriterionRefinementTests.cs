using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class GateOwnedCriterionRefinementTests
{
    private const string Candidate = "candidate-c";

    [Xunit.Theory]
    [Xunit.InlineData("Acceptance executes")]
    [Xunit.InlineData("ACCEPTANCE-GATE-OWNED")]
    [Xunit.InlineData("executed by the acceptance gate")]
    public async Task MarkerTextDeterministicallyCreatesGateOwnershipAndAcceptanceObligation(string marker)
    {
        var criterion = $"The focused behavior works. {marker}.";
        var objective = $"""
            Implement the focused behavior.

            ## Acceptance criteria

            1. {criterion}
            """;
        var response = $$"""
            {
              "behavioralContract": "Implement the focused behavior.",
              "acceptanceCriteria": [
                {"text": {{System.Text.Json.JsonSerializer.Serialize(criterion)}}, "declared_index": 1}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recorded.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(recorded.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Xunit.Assert.Equal(0, obligation.CriterionIndex);
        Xunit.Assert.Equal(recorded.AuthoritativeRefinedSpecVersion!.Version, obligation.CriterionVersion);
    }

    [Xunit.Fact]
    public async Task MixedMarkersKeepOperatorOwnershipAndUnmarkedStaysWorkerOwned()
    {
        const string mixedCriterion =
            "The operator owns this criterion; Acceptance executes. TEST-VERIFIABLE.";
        const string unmarkedCriterion = "The focused behavior remains unchanged. TEST-VERIFIABLE.";
        var objective = $"""
            Implement the focused behavior.

            ## Acceptance criteria

            1. {mixedCriterion}
            2. {unmarkedCriterion}
            """;
        var response = $$"""
            {
              "behavioralContract": "Implement the focused behavior.",
              "acceptanceCriteria": [
                {"text": {{System.Text.Json.JsonSerializer.Serialize(mixedCriterion)}}, "declared_index": 1},
                {"text": {{System.Text.Json.JsonSerializer.Serialize(unmarkedCriterion)}}, "declared_index": 2}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([mixedCriterion], recorded.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(recorded.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(recorded.CriterionEvidenceObligations);
        Xunit.Assert.Equal(0, obligation.CriterionIndex);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
    }

    [Xunit.Fact]
    public async Task ModelAndMarkerGateOwnershipUnionProducesOneAcceptanceObligation()
    {
        const string criterion = "The focused suite passes. ACCEPTANCE-GATE-OWNED.";
        var objective = $"""
            Implement the focused behavior.

            ## Acceptance criteria

            1. {criterion}
            """;
        var response = $$"""
            {
              "behavioralContract": "Implement the focused behavior.",
              "acceptanceCriteria": [
                {"text": {{System.Text.Json.JsonSerializer.Serialize(criterion)}}, "declared_index": 1, "evidence_owner": "acceptance-gate"}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recorded.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(recorded.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
    }

    [Xunit.Fact]
    public async Task MarkerDerivedGateCriterionDefersButWorkerCriterionNotVerifiableStillFailsClosed()
    {
        const string gateCriterion = "The focused suite passes. Acceptance executes.";
        const string workerCriterion = "The implementation returns the expected result.";

        var accepted = await CreateMarkerReviewScenario(gateCriterion, workerCriterion);
        RecordReviewerRound(accepted, gateVerdict: "not-verifiable", workerVerdict: "met");

        Xunit.Assert.Equal(WorkTaskStatus.Completed, accepted.Reviewer.Status);
        Xunit.Assert.Equal(GoalStatus.Verified, accepted.Goal.Status);
        var gateObligation = Xunit.Assert.Single(accepted.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, gateObligation.Owner);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, gateObligation.State);
        Xunit.Assert.Equal(Candidate, gateObligation.ExpectedCandidateSha);

        var rejected = await CreateMarkerReviewScenario(gateCriterion, workerCriterion);
        RecordReviewerRound(rejected, gateVerdict: "met", workerVerdict: "not-verifiable");

        Xunit.Assert.Equal(WorkTaskStatus.Failed, rejected.Reviewer.Status);
        var failure = Xunit.Assert.Single(rejected.Goal.Timeline.Where(item =>
            item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.Contains("attestation rejected", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("criterion-evidence-map --goal", failure.Message, StringComparison.Ordinal);
    }

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
    public async Task DeclaredIndexesMapParaphrasedOwnersToDeclaredCriteria()
    {
        const string declaredOne = "The acceptance gate runs the focused tests.";
        const string declaredTwo = "The operator records the live observation.";
        const string declaredThree = "The implementation preserves existing behavior.";
        var objective = $"""
            Implement evidence-owner mapping.

            ## Acceptance criteria

            1. {declaredOne}
            2. {declaredTwo}
            3. {declaredThree}
            """;
        const string response = """
            ```json
            {
              "behavioralContract": "Keep declared criteria and their evidence owners.",
              "acceptanceCriteria": [
                {"text": "Run focused acceptance checks", "declared_index": 1, "evidence_owner": "acceptance-gate"},
                {"text": "Observe the deployed behavior.", "declared_index": 2, "evidence_owner": "operator"},
                {"text": "Do not regress existing behavior.", "declared_index": 3}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            ```
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([declaredOne, declaredTwo, declaredThree], recorded.RefinedSpec!.AcceptanceCriteria);
        Xunit.Assert.Equal([declaredOne], recorded.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal([declaredTwo], recorded.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.DoesNotContain(recorded.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Spec refiner owner dropped", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task TextOperatorMarkerWinsGateConflictAndRecordsDiagnostic()
    {
        const string criterion =
            "The operator records the live deployment. REAL-WORLD-DEPENDENT, operator-owned.";
        var objective = $"""
            Record the live deployment.

            ## Acceptance criteria

            1. {criterion}
            """;
        var response = $$"""
            {
              "behavioralContract": "Record the live deployment.",
              "acceptanceCriteria": [
                {"text": {{System.Text.Json.JsonSerializer.Serialize(criterion)}}, "declared_index": 1, "evidence_owner": "acceptance-gate"}
              ],
              "verificationClass": "RealWorldDependent",
              "decisions": [],
              "forks": []
            }
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recorded.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(recorded.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Contains(recorded.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains(
                "text marker claims operator for declared criterion 1, refiner claims acceptance-gate",
                StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task DuplicateOperatorMarkedTextAppendsOnceAndDiagnosesBothIndexes()
    {
        const string criterion = "The operator records the live result. REAL-WORLD-DEPENDENT, operator-owned.";
        var objective = $"""
            Record the live result.

            ## Acceptance criteria

            1. {criterion}
            2. {criterion}
            """;
        var response = $$"""
            {
              "behavioralContract": "Record the live result.",
              "acceptanceCriteria": [
                {{System.Text.Json.JsonSerializer.Serialize(criterion)}},
                {{System.Text.Json.JsonSerializer.Serialize(criterion)}}
              ],
              "verificationClass": "RealWorldDependent",
              "decisions": [],
              "forks": []
            }
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recorded.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Contains(recorded.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("declared criteria 1 and 2 share text", StringComparison.Ordinal));
        Xunit.Assert.All(recorded.CriterionEvidenceObligations, obligation =>
            Xunit.Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner));
    }

    [Xunit.Fact]
    public async Task UnownedRealWorldCriterionDefaultsToOperatorWithDiagnostic()
    {
        const string criterion = "The live deployment is observed. REAL-WORLD-DEPENDENT.";
        var objective = $"""
            Observe the live deployment.

            ## Acceptance criteria

            1. {criterion}
            """;
        var response = $$"""
            {"behavioralContract":"Observe the live deployment.","acceptanceCriteria":[{{System.Text.Json.JsonSerializer.Serialize(criterion)}}],"verificationClass":"RealWorldDependent","decisions":[],"forks":[]}
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recorded.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Contains(recorded.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains(
                "declared criterion 1 is real-world-dependent with no declared owner, classified operator-owned",
                StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task TextOperatorMarkerAgreesWithRefinerWithoutConflictDiagnostic()
    {
        const string criterion = "The operator records the live result. REAL-WORLD-DEPENDENT, operator-owned.";
        var objective = $"""
            Record the live result.

            ## Acceptance criteria

            1. {criterion}
            """;
        var response = $$"""
            {"behavioralContract":"Record the live result.","acceptanceCriteria":[{"text":{{System.Text.Json.JsonSerializer.Serialize(criterion)}},"declared_index":1,"evidence_owner":"operator"}],"verificationClass":"RealWorldDependent","decisions":[],"forks":[]}
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal([criterion], recorded.RefinedSpec!.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.DoesNotContain(recorded.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Spec refiner owner conflict", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task UnmatchedParaphrasedOwnerRecordsOneDroppedDecision()
    {
        const string entryText = "Observe the rewritten deployment behavior.";
        const string objective = """
            Implement evidence-owner mapping.

            ## Acceptance criteria

            1. The operator observes the deployment.
            """;
        const string response = """
            {"behavioralContract":"Keep declared criteria.","acceptanceCriteria":[{"text":"Observe the rewritten deployment behavior.","evidence_owner":"operator"}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Empty(recorded.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(recorded.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        var decision = Xunit.Assert.Single(recorded.Timeline.Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Spec refiner owner dropped", StringComparison.Ordinal)));
        Xunit.Assert.Contains(entryText[..20], decision.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task RawOutputIsPersistedBeforeValidAndInvalidParsing()
    {
        var responses = new[]
        {
            """
            {"behavioralContract":"Valid response.","acceptanceCriteria":["Criterion"],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """,
            "This response is not parseable JSON."
        };

        foreach (var response in responses)
        {
            var scenario = CreateRefinementScenario(response);

            await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

            var decision = Xunit.Assert.Single(scenario.Kernel.GetGoal(scenario.Goal.Id).Timeline.Where(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.StartsWith("Spec refiner raw output: ", StringComparison.Ordinal)));
            var path = decision.Message["Spec refiner raw output: ".Length..];
            Xunit.Assert.True(File.Exists(path), $"Expected raw refiner sidecar '{path}' to exist.");
            Xunit.Assert.Equal(response, await File.ReadAllTextAsync(path));
        }
    }

    [Xunit.Fact]
    public async Task RawOutputWriteFailureIsAdvisory()
    {
        const string response = """
            {"behavioralContract":"Valid response.","acceptanceCriteria":["Criterion"],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """;
        var scenario = CreateRefinementScenario(response, blockRawOutputDirectory: true);

        var result = await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        Xunit.Assert.Equal(RefinementDisposition.Completed, result.Disposition);
        Xunit.Assert.Single(scenario.Kernel.GetGoal(scenario.Goal.Id).Timeline.Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Spec refiner raw output not persisted: ", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public async Task RawOutputUsesProvidedExecutorStamp()
    {
        const string stamp = "20260920044315000";
        const string response =
            "{\"behavioralContract\":\"Valid response.\",\"acceptanceCriteria\":[\"Criterion\"],\"verificationClass\":\"TestVerifiable\",\"decisions\":[],\"forks\":[]}";
        var scenario = CreateRefinementScenario(response, rawOutputStamp: stamp);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var decision = Xunit.Assert.Single(scenario.Kernel.GetGoal(scenario.Goal.Id).Timeline.Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Spec refiner raw output: ", StringComparison.Ordinal)));
        Xunit.Assert.EndsWith($"-{stamp}.refiner.txt", decision.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task DuplicateDeclaredOwnerClaimsKeepFirstAndRecordConflict()
    {
        const string objective = """
            Implement evidence-owner mapping.

            ## Acceptance criteria

            1. The focused test passes.
            """;
        const string response = """
            {"behavioralContract":"Keep one owner.","acceptanceCriteria":[{"text":"First wording","declared_index":1,"evidence_owner":"acceptance-gate"},{"text":"Second wording","declared_index":1,"evidence_owner":"operator"}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """;
        var scenario = CreateRefinementScenario(response, objective);

        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);

        var recorded = scenario.Kernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal(["The focused test passes."], recorded.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(recorded.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Single(recorded.Timeline.Where(evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Spec refiner owner conflict: operator for declared criterion 1 already owned by acceptance-gate", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void DeclaredIndexParsingDiagnosesMalformedValuesAndPreservesLegacyEntries()
    {
        var output = SpecRefinerPlanner.Parse("""
            {"behavioralContract":"Parse indexes.","acceptanceCriteria":[{"text":"One","declared_index":1,"evidence_owner":"acceptance-gate"},{"text":"Two","declared_index":"two","evidence_owner":"operator"},{"text":"Three","declared_index":9,"evidence_owner":"operator"},"Four"],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """);

        Xunit.Assert.True(output.IsValid);
        Xunit.Assert.Equal(["One", "Two", "Three", "Four"], output.AcceptanceCriteria);
        Xunit.Assert.Equal(["One"], output.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(["Two", "Three"], output.OperatorOwnedAcceptanceCriteria);
        Xunit.Assert.Equal([1, null, null, null], output.CriterionOwnerships.Select(item => item.DeclaredIndex));
        Xunit.Assert.Contains(output.ParseDiagnostics, diagnostic =>
            diagnostic.Contains("non-integer declared_index", StringComparison.Ordinal) &&
            diagnostic.Contains("two", StringComparison.Ordinal));
        Xunit.Assert.Contains(output.ParseDiagnostics, diagnostic =>
            diagnostic.Contains("out-of-range declared_index '9'", StringComparison.Ordinal));
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
        var landCalled = false;
        var matchingDriver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                true,
                [],
                BranchHeadSha: Candidate),
            land: goal =>
            {
                landCalled = true;
                return new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });
        matchingDriver.BeginTick(matching.Kernel, 1);

        var matchingResult = matchingDriver.AdvanceOnce(
            matching.Goal,
            ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(matchingResult.Outcome);
        var satisfied = Xunit.Assert.Single(matching.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceState.Satisfied, satisfied.State);
        Xunit.Assert.Equal(Candidate, satisfied.CandidateSha);
        Xunit.Assert.NotNull(satisfied.ReceiptId);
        Xunit.Assert.True(landCalled, "The production conductor acceptance path must proceed to landing.");

        var mismatched = CreateBoundGateScenario();
        var mismatchLanded = false;
        var mismatchedDriver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                true,
                [],
                BranchHeadSha: "candidate-c2"),
            land: goal =>
            {
                mismatchLanded = true;
                return new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });
        mismatchedDriver.BeginTick(mismatched.Kernel, 1);

        var mismatchResult = mismatchedDriver.AdvanceOnce(
            mismatched.Goal,
            ConductorAutonomyPolicy.Conservative);

        var mismatchHold = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(mismatchResult.Outcome);
        Xunit.Assert.Contains("Rebind", mismatchHold.Reason, StringComparison.Ordinal);
        Xunit.Assert.Contains(Candidate, mismatchHold.Reason, StringComparison.Ordinal);
        Xunit.Assert.False(mismatchLanded);
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
        CreateRefinementScenario(
            string response,
            string objective = "Integrate the billing system",
            bool blockRawOutputDirectory = false,
            string? rawOutputStamp = null,
            IReadOnlyList<TaskSpec>? tasks = null)
    {
        var provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
        var collaboration = new FakeCollaborationItemStore();
        var tempDirectory = Directory.CreateTempSubdirectory("gate-owned-refinement-");
        var rawOutputDirectory = Path.Combine(tempDirectory.FullName, "logs");
        if (blockRawOutputDirectory)
            File.WriteAllText(rawOutputDirectory, "This file prevents directory creation.");
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
            WorkerProfileCatalog.Default(),
            rawOutputDirectory: rawOutputDirectory,
            rawOutputStamp: rawOutputStamp);
        var kernel = new AgentOrchestratorKernel();
        var goal = tasks is null
            ? kernel.CreateGoal(objective)
            : kernel.CreateGoal(objective, tasks);
        return (service, kernel, goal, collaboration);
    }

    private static async Task<(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer)>
        CreateMarkerReviewScenario(string gateCriterion, string workerCriterion)
    {
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var objective = $"""
            Implement the focused behavior.

            ## Acceptance criteria

            1. {gateCriterion}
            2. {workerCriterion}
            """;
        var response = $$"""
            {
              "behavioralContract": "Implement the focused behavior.",
              "acceptanceCriteria": [
                {"text": {{System.Text.Json.JsonSerializer.Serialize(gateCriterion)}}, "declared_index": 1},
                {"text": {{System.Text.Json.JsonSerializer.Serialize(workerCriterion)}}, "declared_index": 2}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var scenario = CreateRefinementScenario(response, objective, tasks: [reviewer]);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        scenario.Kernel.ActivateGoal(scenario.Goal.Id, AgentCatalog.Default().Agents);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, reviewer.Id, new TaskDispatchRecord(
            "fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
        scenario.Kernel.RecordDispatchBaseCommit(scenario.Goal.Id, reviewer.Id, Candidate);
        return (scenario.Kernel, scenario.Goal, reviewer);
    }

    private static void RecordReviewerRound(
        (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) scenario,
        string gateVerdict,
        string workerVerdict)
    {
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: review",
            "tests: pass - deterministic fixture", "commit: none", "blockers: none",
            "findings: []", "touched_anchors: []",
            $"criteria_verdicts: [{{\"criterion_index\":0,\"verdict\":\"{gateVerdict}\",\"evidence\":\"focused acceptance gate\"}},{{\"criterion_index\":1,\"verdict\":\"{workerVerdict}\",\"evidence\":\"source and tests\"}}]",
            "verdict: pass", "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(
            scenario.Goal.Id,
            scenario.Reviewer.Id,
            new TaskVerificationRecord(
                "review", "C:\\fixture", 0, output, string.Empty, DateTimeOffset.UtcNow,
                WorkerResultPresent: true, ReviewedCommit: Candidate));
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
