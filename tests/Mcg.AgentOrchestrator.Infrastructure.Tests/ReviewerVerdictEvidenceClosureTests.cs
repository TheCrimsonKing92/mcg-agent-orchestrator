using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ReviewerVerdictEvidenceClosureTests
{
    private const string MetEvidence = "Inspected each diff requirement on candidate B.";
    private const string ClosurePrefix = "REVIEWER_VERDICT_EVIDENCE task=";
    private const string SkippedPrefix = "REVIEWER_VERDICT_EVIDENCE_SKIPPED";
    private static readonly DateTimeOffset DispatchTime = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Reviewer")]
    [InlineData("reviewer")]
    [InlineData(" REVIEWER ")]
    public void MetVerdictOnCurrentCandidateClosesReviewerOwnedWait(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);
        var plannerStatus = scenario.Planner.Status;
        var developerStatus = scenario.Developer.Status;

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, request.AuthoritativeAnswer.BriefVersion);
        Assert.StartsWith(ClosurePrefix, request.Answer);
        Assert.Contains($"task={scenario.Reviewer.Id.Value}", request.Answer);
        Assert.Contains("criterion_index=2", request.Answer);
        Assert.Contains("candidate=B", request.Answer);
        Assert.Contains("worker_result_log=unavailable", request.Answer);
        Assert.Contains(MetEvidence, request.Answer);
        var received = Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
        Assert.Equal(request.Answer, received.Message);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        Assert.Equal(plannerStatus, scenario.Planner.Status);
        Assert.Equal(developerStatus, scenario.Developer.Status);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains(SkippedPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-verifiable", "verdict-not-met")]
    [InlineData("missing-entry", "verdict-not-met")]
    [InlineData("old-candidate", "candidate-differs")]
    [InlineData("new-developer-running", "candidate-unknown")]
    [InlineData("needs-work", "review-not-pass")]
    [InlineData("untyped", "untyped")]
    [InlineData("old-brief", "brief-version-differs")]
    public void ReviewerOwnedWaitStaysOpenAndNamesTheFailingCondition(string scenarioName, string reason)
    {
        var scenario = CreateScenario(secondDeveloper: scenarioName == "new-developer-running");
        // A missing Reviewer index fails coverage before the completion tail. Use an unmapped
        // Planner index instead, as planned, to exercise a missing mapped verdict at that tail.
        var criterionIndex = scenarioName == "not-verifiable" ? 2 : scenarioName == "missing-entry" ? 4 : 3;
        var request = CreateWait(scenario, "Reviewer", criterionIndex,
            typed: scenarioName != "untyped", briefVersion: scenarioName == "old-brief" ? 0 : null);

        RecordReviewer(scenario, candidate: scenarioName == "old-candidate" ? "A" : "B",
            finding: scenarioName == "needs-work");

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Null(request.AuthoritativeAnswer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains(ClosurePrefix, StringComparison.Ordinal));
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, skipped.Kind);
        Assert.Equal(scenario.Reviewer.Id, skipped.TaskId);
        Assert.Equal($"{SkippedPrefix} request={request.Id.Value[..8]} reason={reason}", skipped.Message);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("acceptance-gate")]
    [InlineData("acceptance")]
    [InlineData("developer requests; conductor executes")]
    [InlineData("acceptance gate")]
    [InlineData("")]
    public void NonReviewerOwnedWaitIsNeverEvaluated(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains("REVIEWER_VERDICT_EVIDENCE", StringComparison.Ordinal));
        AssertPassingReviewClosesControlWait();
    }

    [Fact]
    public void GoalWithoutReviewerOwnedWaitIsUnchanged()
    {
        var scenario = CreateScenario();

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains("REVIEWER_VERDICT_EVIDENCE", StringComparison.Ordinal));
        AssertPassingReviewClosesControlWait();
    }

    [Theory]
    [InlineData("Developer")]
    [InlineData("developer")]
    [InlineData("Tester")]
    [InlineData(" TESTER ")]
    public void MetVerdictOnCurrentCandidateClosesWorkerOwnedWait(string owner)
    {
        var scenario = CreateScenario(testerBase: "B");
        var request = CreateWait(scenario, owner);
        var normalizedOwner = owner.Trim().ToLowerInvariant();
        var ownerTask = normalizedOwner == "developer" ? scenario.Developer : scenario.Tester!;

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.StartsWith($"{ClosurePrefix}{scenario.Reviewer.Id.Value}", request.Answer);
        Assert.Contains($"criterion_index=2 owner={normalizedOwner} owner_task={ownerTask.Id.Value}", request.Answer);
        Assert.Contains("candidate=B", request.Answer);
    }

    [Theory]
    [InlineData(null, WorkTaskStatus.Completed, "owner-round-missing")]
    [InlineData("B", WorkTaskStatus.Failed, "owner-round-incomplete")]
    [InlineData("A", WorkTaskStatus.Completed, "owner-candidate-differs")]
    public void TesterOwnedWaitStaysOpenWhenTheTesterRoundDoesNotMatch(
        string? testerBase, WorkTaskStatus testerStatus, string reason)
    {
        var scenario = CreateScenario(testerBase: testerBase, testerStatus: testerStatus);
        var request = CreateWait(scenario, "Tester");

        RecordReviewer(scenario);

        AssertSkippedWait(scenario, request, reason);
    }

    [Fact]
    public void DeveloperOwnedWaitUsesTheSliceOneConditions()
    {
        var running = CreateScenario(secondDeveloper: true);
        var runningRequest = CreateWait(running, "Developer");
        RecordReviewer(running);
        AssertSkippedWait(running, runningRequest, "candidate-unknown");

        var notMet = CreateScenario();
        var notMetRequest = CreateWait(notMet, "Developer", criterionIndex: 2);
        RecordReviewer(notMet);
        AssertSkippedWait(notMet, notMetRequest, "verdict-not-met");
    }

    private static void AssertSkippedWait(Scenario scenario, HumanInputRequest request, string reason)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Null(request.AuthoritativeAnswer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.Contains(ClosurePrefix, StringComparison.Ordinal));
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, skipped.Kind);
        Assert.Equal(scenario.Reviewer.Id, skipped.TaskId);
        Assert.Equal($"{SkippedPrefix} request={request.Id.Value[..8]} reason={reason}", skipped.Message);
    }

    // Pair no-op scenarios with a positive control so they also fail when the closure is absent.
    private static void AssertPassingReviewClosesControlWait()
    {
        var control = CreateScenario();
        var request = CreateWait(control, "Reviewer");
        RecordReviewer(control);
        Assert.Equal(WorkTaskStatus.Completed, control.Reviewer.Status);
        Assert.True(request.IsCompleted);
    }

    private static Scenario CreateScenario(bool secondDeveloper = false, string? testerBase = null,
        WorkTaskStatus testerStatus = WorkTaskStatus.Completed)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect diff", AgentRole.Reviewer);
        var tasks = new List<TaskSpec> { planner, developer, reviewer };
        var tester = testerBase is null ? null : new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester);
        if (tester is not null)
            tasks.Add(tester);
        var laterDeveloper = new TaskSpec(TaskId.New(), "Revise candidate", AgentRole.Developer);
        if (secondDeveloper)
            tasks.Add(laterDeveloper);
        var goal = kernel.CreateGoal("Close current Reviewer evidence waits", tasks);
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
        if (secondDeveloper)
        {
            kernel.RecordTaskDispatch(goal.Id, laterDeveloper.Id, new TaskDispatchRecord(
                "codex-cli", "revise", "C:\\fixture", DispatchTime.AddSeconds(1), BaseCommit: "B"));
            Assert.Equal(WorkTaskStatus.Running, laterDeveloper.Status);
        }
        if (tester is not null)
        {
            var startedAt = DispatchTime.AddSeconds(1);
            kernel.RecordTaskDispatch(goal.Id, tester.Id, new TaskDispatchRecord(
                "codex-cli", "verify", "C:\\fixture", startedAt, BaseCommit: testerBase));
            kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
                "verify", "C:\\fixture", 0, string.Empty, string.Empty, startedAt.AddMilliseconds(1),
                WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate"));
            kernel.ReportWorkerTaskProgress(goal.Id, tester.Id, testerStatus, "Tester round recorded.");
            Assert.Equal(testerStatus, tester.Status);
            Assert.Equal(testerBase, tester.LastVerification!.ReviewedCommit);
        }
        return new Scenario(kernel, goal, planner, developer, reviewer, tester);
    }

    private static HumanInputRequest CreateWait(Scenario scenario, string owner, int criterionIndex = 3,
        bool typed = true, int? briefVersion = null)
    {
        var identity = typed ? new PlannerEvidenceIdentity(scenario.Goal.Id.Value, scenario.Planner.Id.Value,
            briefVersion ?? scenario.Goal.AuthoritativeBrief.Version, criterionIndex, "diff-inspection") : null;
        var payload = new PlannerEvidencePayload(criterionIndex, "diff-inspection", "prospective", owner,
            "Inspect diff", "Reviewer owns this evidence", null, null, null);
        var request = scenario.Kernel.RequestHumanInputDeduplicated(scenario.Goal.Id, scenario.Planner.Id,
            "Provide the diff inspection evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: owner, plannerIdentity: identity, plannerEvidence: payload).Request;
        Assert.False(request.IsCompleted);
        return request;
    }

    private static void RecordReviewer(Scenario scenario, string candidate = "B", bool finding = false)
    {
        if (finding)
            scenario.Kernel.MapCriterionEvidenceOwner(scenario.Goal.Id, 0,
                scenario.Goal.AuthoritativeRefinedSpecVersion!.Version, CriterionEvidenceOwner.Acceptance,
                "operator", findingStableId: "gate-proof", expectedCandidateSha: candidate);
        var startedAt = DispatchTime.AddSeconds(2);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: candidate));
        var findings = finding
            ? """[{"stable_id":"gate-proof","state":"open","location":{"file":"docs/operator-runbook.md","region":"gate-proof"},"description":"Full acceptance evidence remains pending.","severity":"blocking","category":"acceptance-owned"}]"""
            : "[]";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", $"blockers: {(finding ? "Required owner evidence remains pending." : "none")}",
            $"findings: {findings}", "touched_anchors: []",
            "criteria_verdicts: [" +
                """{"criterion_index":0,"verdict":"not-verifiable","evidence":"Full acceptance gate"},""" +
                """{"criterion_index":1,"verdict":"not-verifiable","evidence":"Focused tests"},""" +
                $"{{\"criterion_index\":2,\"verdict\":\"met\",\"evidence\":\"{MetEvidence}\"}}]",
            $"verdict: {(finding ? "needs-work" : "pass")}",
            "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, startedAt.AddSeconds(1),
            WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate", DispatchStartedAt: startedAt));
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Planner,
        TaskSpec Developer, TaskSpec Reviewer, TaskSpec? Tester);
}
