using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns its in-memory kernel and uses no external resources.
public sealed class ReviewerVerdictEvidenceClosureTestsWorkerOwner
{
    private const string MetEvidence = "Inspected each diff requirement on candidate B.";
    private const string ClosurePrefix = "REVIEWER_VERDICT_EVIDENCE task=";
    private const string SkippedPrefix = "REVIEWER_VERDICT_EVIDENCE_SKIPPED";
    private static readonly DateTimeOffset DispatchTime = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("worker")]
    [InlineData("Worker")]
    [InlineData(" WORKER ")]
    public void MetVerdictOnCurrentCandidateClosesWorkerOwnedWait(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, request.AuthoritativeAnswer.BriefVersion);
        Assert.StartsWith($"{ClosurePrefix}{scenario.Reviewer.Id.Value}", request.Answer);
        Assert.Contains($"criterion_index=2 owner=worker owner_task={scenario.Developer.Id.Value}", request.Answer);
        Assert.Contains("candidate=B", request.Answer);
        Assert.Contains("worker_result_log=unavailable", request.Answer);
        Assert.Contains(MetEvidence, request.Answer);
        var received = Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Message.Contains("REVIEWER_VERDICT_EVIDENCE", StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.HumanInputReceived, received.Kind);
        Assert.Equal(request.TaskId, received.TaskId);
        Assert.Equal(request.Answer, received.Message);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains(SkippedPrefix, StringComparison.Ordinal));
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
    }

    [Fact]
    public void WorkerOwnedWaitStaysOpenWhenTheVerdictIsNotMet()
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, "worker", criterionIndex: 2);

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Null(request.AuthoritativeAnswer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains(ClosurePrefix, StringComparison.Ordinal));
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, skipped.Kind);
        Assert.Equal(scenario.Reviewer.Id, skipped.TaskId);
        Assert.Equal($"{SkippedPrefix} request={request.Id.Value[..8]} reason=verdict-not-met", skipped.Message);
        Assert.Same(request, Assert.Single(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id)));
    }

    private static Scenario CreateScenario()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect diff", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Close current Reviewer evidence waits", [planner, developer, reviewer]);
        string[] criteria = ["Full gate passes.", "Focused tests pass.", "Diff inspection is complete."];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Bind evidence to the current candidate", criteria, VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [criteria[0], criteria[1]]
        });
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        kernel.ReportWorkerTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Candidate B is ready.");
        return new Scenario(kernel, goal, planner, developer, reviewer);
    }

    private static HumanInputRequest CreateWait(Scenario scenario, string owner, int criterionIndex = 3)
    {
        var identity = new PlannerEvidenceIdentity(scenario.Goal.Id.Value, scenario.Planner.Id.Value,
            scenario.Goal.AuthoritativeBrief.Version, criterionIndex, "diff-inspection");
        var payload = new PlannerEvidencePayload(criterionIndex, "diff-inspection", "prospective", owner,
            "Inspect diff", "Reviewer owns this evidence", null, null, null);
        var request = scenario.Kernel.RequestHumanInputDeduplicated(scenario.Goal.Id, scenario.Planner.Id,
            "Provide the diff inspection evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: owner, plannerIdentity: identity, plannerEvidence: payload).Request;
        Assert.False(request.IsCompleted);
        return request;
    }

    private static void RecordReviewer(Scenario scenario)
    {
        var startedAt = DispatchTime.AddSeconds(2);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: "B"));
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            "criteria_verdicts: [" +
                """{"criterion_index":0,"verdict":"not-verifiable","evidence":"Full acceptance gate"},""" +
                """{"criterion_index":1,"verdict":"not-verifiable","evidence":"Focused tests"},""" +
                $"{{\"criterion_index\":2,\"verdict\":\"met\",\"evidence\":\"{MetEvidence}\"}}]",
            "verdict: pass", "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, startedAt.AddSeconds(1),
            WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate", DispatchStartedAt: startedAt));
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Planner,
        TaskSpec Developer, TaskSpec Reviewer);
}
