using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fact owns its kernel, fake provider and unique temporary directory.
public sealed class FallbackObjectiveCriterionReviewerTests
{
    private static readonly DateTimeOffset DispatchTime = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MetVerdictOnFallbackCriterionCompletesReviewer()
    {
        var (kernel, goal, reviewer) = await CreateScenario();

        RecordReviewer(kernel, goal, reviewer, "met");

        Assert.Equal(WorkTaskStatus.Completed, reviewer.Status);
        Assert.DoesNotContain(kernel.GetTimeline(goal.Id), evt =>
            evt.TaskId == reviewer.Id &&
            evt.Message.Contains("criteria attestation rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotVerifiableVerdictOnFallbackCriterionStaysRejected()
    {
        var (kernel, goal, reviewer) = await CreateScenario();

        RecordReviewer(kernel, goal, reviewer, "not-verifiable");

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        var failure = Assert.Single(kernel.GetTimeline(goal.Id).Where(evt =>
            evt.TaskId == reviewer.Id && evt.Kind == ProgressKind.TaskFailed));
        Assert.Contains("criteria attestation rejected", failure.Message, StringComparison.Ordinal);
        Assert.Contains("criterion_index=0 (criterion 1) verdict=not-verifiable", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(goal.CriterionEvidenceObligations, obligation => obligation.CriterionIndex == 0);
    }

    private static async Task<(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer)> CreateScenario()
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
        var reviewer = new TaskSpec(TaskId.New(), "Inspect fallback objective", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Show the widget label on the dashboard.", [reviewer]);

        var result = await service.RefineAsync(kernel, goal.Id);

        Assert.Equal(RefinementDisposition.Fallback, result.Disposition);
        var criterion = Assert.Single(goal.RefinedSpec!.AcceptanceCriteria);
        var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);
        Assert.Equal(DeclaredEvidenceOwnerKind.Worker, owner.Kind);
        Assert.Equal("Reviewer", owner.Role);
        Assert.Empty(goal.CriterionEvidenceObligations);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal, reviewer);
    }

    private static void RecordReviewer(AgentOrchestratorKernel kernel, Goal goal, TaskSpec reviewer, string verdict)
    {
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", DispatchTime));
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect candidate diff and recorded receipts",
            "tests: deferred - FallbackObjectiveCriterionReviewerTests",
            "commit: none",
            "blockers: none",
            "findings: []",
            "touched_anchors: []",
            $"criteria_verdicts: [{{\"criterion_index\":0,\"verdict\":\"{verdict}\",\"evidence\":\"Candidate diff and recorded receipts inspected.\"}}]",
            "verdict: pass",
            "model_fit: fixture/model - adequate - deterministic review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, DispatchTime.AddSeconds(1),
            WorkerResultPresent: true, DispatchStartedAt: DispatchTime));
    }
}
