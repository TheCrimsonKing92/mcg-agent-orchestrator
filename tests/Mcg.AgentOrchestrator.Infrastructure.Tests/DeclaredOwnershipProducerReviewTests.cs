using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeclaredOwnershipProducerReviewTests
{
    private const string Candidate = "ownership-candidate";
    private const string Fallback = "Local fallback behavior remains unchanged.";
    private const string Windows = "Windows padding/rebase behavior and normal gates remain unchanged.";
    private const string WorkerCriterion = "The implementation preserves source correctness.";

    [Xunit.Theory]
    [Xunit.InlineData(Fallback, "Acceptance owns execution.")]
    [Xunit.InlineData(Windows, "Acceptance owns execution.")]
    [Xunit.InlineData(Fallback, "Reviewer owns source inspection; Acceptance owns execution.")]
    [Xunit.InlineData(Windows, "Reviewer owns source inspection; Acceptance owns execution.")]
    [Xunit.InlineData(Fallback, "Developer owns; Acceptance executes.")]
    [Xunit.InlineData(Windows, "Developer owns; Acceptance executes.")]
    public async Task DeclaredExecutionOwnerSurvivesProducerAndDeferredReview(string behavior, string declaration)
    {
        var criterion = $"{behavior} {declaration} TEST-VERIFIABLE.";
        using var scenario = await Refine(criterion);

        RecordReview(scenario);

        // Check the consumer first: RED must expose rejected deferral, rather than only a parser result.
        Xunit.Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Xunit.Assert.Equal(GoalStatus.Verified, scenario.Goal.Status);
        Xunit.Assert.Equal([criterion, WorkerCriterion], scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        Xunit.Assert.Equal([criterion], scenario.Goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(scenario.Goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        var obligation = Xunit.Assert.Single(scenario.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(0, obligation.CriterionIndex);
        Xunit.Assert.Equal(criterion, obligation.Criterion);
        Xunit.Assert.Equal(scenario.Goal.AuthoritativeRefinedSpecVersion!.Version, obligation.CriterionVersion);
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Xunit.Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
        Xunit.Assert.Null(obligation.ReceiptId);
        Xunit.Assert.Contains(scenario.Goal.Timeline, item =>
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("Deferred criterion evidence remains pending", StringComparison.Ordinal) &&
            item.Message.Contains(obligation.Id, StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.TaskFailed);

        var serialized = JsonSerializer.Serialize(scenario.Kernel.ExportSnapshot());
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(
            JsonSerializer.Deserialize<OrchestratorSnapshot>(serialized)!);
        var restored = restoredKernel.GetGoal(scenario.Goal.Id);
        Xunit.Assert.Equal(obligation, Xunit.Assert.Single(restored.CriterionEvidenceObligations));
        Xunit.Assert.Equal([criterion], restored.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Equal(obligation,
            Xunit.Assert.Single(restored.GetOutstandingCriterionEvidenceObligations(Candidate)));
        var error = Xunit.Assert.Throws<InvalidOperationException>(() =>
            restoredKernel.CompleteGoal(restored.Id, "Source review passed; execution is still required."));
        Xunit.Assert.Contains("criterion evidence obligations remain outstanding", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("TEST-VERIFIABLE.")]
    [Xunit.InlineData("Reviewer owns source inspection. TEST-VERIFIABLE.")]
    public async Task SameReviewCannotCreateAnExecutionOwner(string declaration)
    {
        using var scenario = await Refine($"{Fallback} {declaration}");

        // Identical output to the positive cases; only upstream declared authority changes.
        RecordReview(scenario);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, scenario.Reviewer.Status);
        Xunit.Assert.Empty(scenario.Goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Xunit.Assert.Empty(scenario.Goal.CriterionEvidenceObligations);
        var failure = Xunit.Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskFailed));
        Xunit.Assert.Contains("attestation rejected", failure.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("criterion-evidence-map --goal", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task DeferredExecutionDoesNotExcuseUnmetSourceCriterion()
    {
        using var scenario = await Refine($"{Fallback} Developer owns; Acceptance executes. TEST-VERIFIABLE.");
        Xunit.Assert.Equal(CriterionEvidenceOwner.Acceptance,
            Xunit.Assert.Single(scenario.Goal.CriterionEvidenceObligations).Owner);

        RecordReview(scenario, workerVerdict: "not-met");

        Xunit.Assert.Equal(WorkTaskStatus.Failed, scenario.Reviewer.Status);
        Xunit.Assert.NotEqual(GoalStatus.Verified, scenario.Goal.Status);
        var obligation = Xunit.Assert.Single(scenario.Goal.CriterionEvidenceObligations);
        Xunit.Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Xunit.Assert.Null(obligation.ReceiptId);
        Xunit.Assert.Contains(scenario.Goal.Timeline, item =>
            item.Kind == ProgressKind.TaskFailed && item.Message.Contains("attestation rejected", StringComparison.Ordinal));
    }

    private static async Task<Scenario> Refine(string criterion)
    {
        var objective = $"""
            Preserve behavior through source review and acceptance.

            ## Acceptance criteria

            1. {criterion}
            2. {WorkerCriterion}
            """;
        // Omit model ownership claims: the producer must translate the actual brief declaration.
        // Indexed paraphrases must retain the original declared criterion identity.
        var response = $$"""
            {
              "behavioralContract": "Preserve behavior through source review and acceptance.",
              "acceptanceCriteria": [
                {"text": "Preserve the execution behavior.", "declared_index": 1},
                {"text": {{JsonSerializer.Serialize(WorkerCriterion)}}, "declared_index": 2}
              ],
              "verificationClass": "TestVerifiable",
              "decisions": [],
              "forks": []
            }
            """;
        var provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
        var tempDirectory = Directory.CreateTempSubdirectory("ownership-producer-review-");
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal(objective, [reviewer]);
        var scenario = new Scenario(kernel, goal, reviewer, tempDirectory);
        try
        {
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

            var result = await service.RefineAsync(kernel, goal.Id);

            Xunit.Assert.NotNull(provider.LastRequest);
            Xunit.Assert.Equal(RefinementOutcome.AutoRefined, result.Outcome);
            Xunit.Assert.NotNull(goal.RefinedSpec);
            Xunit.Assert.Equal([criterion, WorkerCriterion], goal.RefinedSpec.AcceptanceCriteria);
            Xunit.Assert.Empty(goal.RefinedSpec.OpenQuestions);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
                "fixture", "review", tempDirectory.FullName, DateTimeOffset.UtcNow));
            kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, Candidate);
            return scenario;
        }
        catch
        {
            scenario.Dispose();
            throw;
        }
    }

    private static void RecordReview(Scenario scenario, string workerVerdict = "met")
    {
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: source review",
            "tests: deferred - acceptance execution", "commit: none", "blockers: none",
            "findings: []", "touched_anchors: []",
            $$"""criteria_verdicts: [{"criterion_index":0,"verdict":"not-verifiable","evidence":"Acceptance owns fallback and Windows execution; source inspection passed."},{"criterion_index":1,"verdict":"{{workerVerdict}}","evidence":"Source inspection."}]""",
            "verdict: pass", "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(
            scenario.Goal.Id,
            scenario.Reviewer.Id,
            new TaskVerificationRecord(
                "review", scenario.TempDirectory.FullName, 0, output, string.Empty, DateTimeOffset.UtcNow,
                WorkerResultPresent: true, ReviewedCommit: Candidate));
    }

    private sealed record Scenario(
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Reviewer,
        DirectoryInfo TempDirectory) : IDisposable
    {
        public void Dispose() => TempDirectory.Delete(recursive: true);
    }
}
