using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class PlannerEvidenceStoreReferenceDispatchTests
{
    [Fact]
    public void LateDirectiveSurvivesVerificationAndSerializedRequestRoundTrip()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        var planner = new TaskSpec(TaskId.New(), "Plan implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Retrieve historical evidence.", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Plan from the receipt.",
            ["Retrieve historical receipt."], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        const string value = "goal-events:" + WorkerStoreReferenceFixture.EventGoalId + "#contains=feasibility-0d8fe45d51913385";
        var directive = "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(new
        {
            criterion_index = 1, evidence_key = "Feasibility Receipt", availability = "retrievable",
            store = ".orchestrator/goal-events/" + WorkerStoreReferenceFixture.EventGoalId + ".jsonl",
            store_ref = value, needed = "historical receipt", reason = "worker cannot reach store"
        });
        var expected = Assert.IsType<PlannerEvidenceStoreReference>(
            AgentOutputDirectives.ParseHumanInputRequest(directive, AgentRole.Planner).Directive!.StoreReference);
        var stdout = Path.Combine(fixture.Root, "planner.out.log");
        var stderr = Path.Combine(fixture.Root, "planner.err.log");
        var exit = Path.Combine(fixture.Root, "planner.exit.txt");
        var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
            "1. disposition=planned; plan=retrieve the historical receipt from the named store before implementation",
            StringComparison.Ordinal);
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            new string('H', VerificationTextBounds.PreviewHeadChars), plan, directive,
            new string('T', VerificationTextBounds.PreviewTailChars + 1_000),
            "WORKER_RESULT:", "files: none", "commands: source inspection", "tests: not-run - evidence required",
            "commit: none", "blockers: evidence required", "model_fit: OpenAI/gpt-6.1-sol - adequate - planning",
            "skills: none", "confidence: high", "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        kernel.RecordTaskDispatch(goal.Id, planner.Id,
            new TaskDispatchRecord("planner-worker", "planner.exe", fixture.Root, fixture.Clock.UtcNow));
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
            new TaskProcessRecord(4242, "planner.exe", fixture.Root, stdout, stderr, exit, fixture.Clock.UtcNow, null, null));

        new BackgroundDispatchRunner(clock: fixture.Clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Equal(HumanWaitKind.PlannerPrerequisiteEvidence, request.Kind);
        Assert.Equal(expected, request.StoreReference);
        Assert.Equal(expected, planner.LastVerification!.HumanInputStoreReference);
        Assert.DoesNotContain("PLANNER_EVIDENCE_REQUEST:", planner.LastVerification.StandardOutput, StringComparison.Ordinal);
        var snapshot = kernel.ExportSnapshot();
        var json = JsonSerializer.Serialize(snapshot);
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!, fixture.Clock);
        Assert.Equal(expected, Assert.Single(restored.GetPendingHumanInput(goal.Id)).StoreReference);
        Assert.Equal(expected, restored.GetGoal(goal.Id).Tasks.Single(task => task.Id == planner.Id).LastVerification!.HumanInputStoreReference);

        // Absence on an older persisted request remains an ordinary request, without text inference.
        var legacy = JsonNode.Parse(json)!;
        legacy["HumanInputRequests"]![0]!.AsObject().Remove("StoreReference");
        restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(legacy.ToJsonString())!, fixture.Clock);
        Assert.Null(Assert.Single(restored.GetPendingHumanInput(goal.Id)).StoreReference);

        var stripped = planner.LastVerification with { HumanInputStoreReference = null };
        Assert.Equal(expected, stripped.MergeSameRoundEnrichment(planner.LastVerification).HumanInputStoreReference);
        Assert.Equal(expected, planner.LastVerification.MergeSameRoundEnrichment(stripped).HumanInputStoreReference);
    }
}
