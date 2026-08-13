using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerEvidenceDispatchTests
{
    [Xunit.Fact]
    public void RefreshLatestProcessRetainsLatePlannerEvidenceRequestAndCreatesStructuredHumanInput()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Planner evidence escalation", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Plan the evidence-backed implementation.",
            ["Retrieve the historical receipt."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        var evidenceRequest =
            "PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":1,\"evidence_key\":\"third-round-receipt\",\"availability\":\"retrievable\",\"store\":\".orchestrator/goal-events/<goal>.jsonl\",\"needed\":\"timestamp, stdout, and classifier receipt\",\"reason\":\"the worker cannot read orchestrator state\"}";
        var plannerPlan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
            "1. disposition=planned; plan=retrieve the historical receipt from the named store before implementation",
            StringComparison.Ordinal);
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            new string('H', VerificationTextBounds.PreviewHeadChars),
            plannerPlan,
            evidenceRequest,
            new string('T', VerificationTextBounds.PreviewTailChars + 1_000),
            "WORKER_RESULT:",
            "files: none",
            "commands: none",
            "tests: not-run - planning requires operator evidence",
            "commit: none",
            "blockers: operator evidence required",
            "model_fit: OpenAI/gpt-5.5 - adequate - planning", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT"));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        var expectedFingerprint = HumanInputRequest.BuildPlannerEvidenceFingerprint(1, "third-round-receipt");
        var request = kernel.GetPendingHumanInput(goal.Id).Single();
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Contains("Availability: retrievable", request.Question, StringComparison.Ordinal);
        Assert.Contains(".orchestrator/goal-events/<goal>.jsonl", request.Question, StringComparison.Ordinal);
        Assert.Equal(expectedFingerprint, request.QuestionFingerprint);
        Assert.Equal(expectedFingerprint, request.BlockerFingerprint);
        Assert.Equal(expectedFingerprint, planner.LastVerification!.HumanInputQuestionFingerprint);
        Assert.Equal(expectedFingerprint, planner.LastVerification.HumanInputBlockerFingerprint);
        Assert.DoesNotContain("PLANNER_EVIDENCE_REQUEST:", planner.LastVerification.StandardOutput, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RefreshLatestProcessRejectsMalformedLatePlannerEvidenceRequestExplicitly()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Malformed Planner evidence escalation", [planner]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-planner-evidence-malformed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdout = Path.Combine(root, "planner.out.log");
        var stderr = Path.Combine(root, "planner.err.log");
        var exit = Path.Combine(root, "planner.exit.txt");
        File.WriteAllText(stdout, string.Join(Environment.NewLine,
            new string('H', VerificationTextBounds.PreviewHeadChars),
            "PLANNER_EVIDENCE_REQUEST: not-json",
            new string('T', VerificationTextBounds.PreviewTailChars + 1_000)));
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", root, now));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            planner.Id,
            new TaskProcessRecord(4242, "planner.exe", root, stdout, stderr, exit, now, null, null));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, planner.Id);

        Assert.Equal(WorkTaskStatus.Failed, planner.Status);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Assert.Contains(
            "Malformed PLANNER_EVIDENCE_REQUEST: payload is not valid JSON",
            planner.LastVerification!.StandardError,
            StringComparison.Ordinal);
    }
}
