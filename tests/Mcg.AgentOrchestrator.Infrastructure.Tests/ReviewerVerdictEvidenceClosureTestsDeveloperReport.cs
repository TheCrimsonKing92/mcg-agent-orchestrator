using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns an in-memory kernel and uses no external resources.
public sealed class ReviewerVerdictEvidenceClosureTestsDeveloperReport
{
    private const string ReportCriterion =
        "The Developer reports `tests: deferred - AlphaTests, BetaTests`; Developer owns; Acceptance executes.";
    private const string MatchingTests = "deferred - BetaTests, AlphaTests";
    private const string ClosurePrefix = "DEVELOPER_REPORT_EVIDENCE task=";
    private const string SkippedPrefix = "REVIEWER_VERDICT_EVIDENCE_SKIPPED";
    private static readonly DateTimeOffset DispatchTime = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("developer")]
    [InlineData("Developer")]
    [InlineData(" DEVELOPER ")]
    public void MatchingReportedClassSetClosesDeveloperOwnedWait(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, request.AuthoritativeAnswer.BriefVersion);
        Assert.StartsWith($"{ClosurePrefix}{scenario.Developer.Id.Value}", request.Answer);
        Assert.Contains("criterion_index=2", request.Answer);
        Assert.Contains($"reviewer_task={scenario.Reviewer.Id.Value}", request.Answer);
        Assert.Contains("candidate=B", request.Answer);
        Assert.Contains("worker_result_log=unavailable", request.Answer);
        Assert.Contains($"tests={MatchingTests}", request.Answer);
        var received = Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
        Assert.Equal(request.TaskId, received.TaskId);
        Assert.Equal(request.Answer, received.Message);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal));
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
    }

    [Theory]
    [InlineData("deferred - AlphaTests", "developer-report-differs")]
    [InlineData("deferred - AlphaTests, BetaTests, GammaTests", "developer-report-differs")]
    [InlineData("deferred - alphaTests, BetaTests", "developer-report-differs")]
    [InlineData("pass - AlphaTests, BetaTests", "developer-report-missing")]
    public void ReportedSetThatDiffersKeepsTheWaitOpen(string testsField, string reason)
    {
        var scenario = CreateScenario(testsField);
        var request = CreateWait(scenario, "developer");

        RecordReviewer(scenario);

        AssertSkippedWait(scenario, request, reason);
    }

    [Fact]
    public void ManualDeveloperRecordKeepsTheWaitOpen()
    {
        var scenario = CreateScenario();
        scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, scenario.Developer.Id, new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, "Adjudicated complete.", string.Empty, DispatchTime.AddSeconds(2)));
        Assert.Equal(VerificationEvidenceSource.Manual, scenario.Developer.LastVerification!.EvidenceSource);
        Assert.False(scenario.Developer.LastVerification.WorkerResultPresent);
        Assert.Equal("B", scenario.Developer.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Completed, scenario.Developer.Status);
        var request = CreateWait(scenario, "developer");

        RecordReviewer(scenario);

        AssertSkippedWait(scenario, request, "developer-report-missing");
    }

    [Fact]
    public void AdjudicatedDeveloperWithoutResultCommitStaysCandidateUnknown()
    {
        var scenario = CreateScenario(resultCommit: false);
        Assert.Equal(WorkTaskStatus.Completed, scenario.Developer.Status);
        Assert.True(string.IsNullOrWhiteSpace(scenario.Developer.LastDispatch!.ResultCommit));
        Assert.Contains($"tests: {MatchingTests}", scenario.Developer.LastVerification!.StandardOutput);
        var request = CreateWait(scenario, "developer");

        RecordReviewer(scenario, candidate: "A");

        AssertSkippedWait(scenario, request, "candidate-unknown");
        AssertMatchingReportClosesControlWait();
    }

    [Theory]
    [InlineData("developer", 2)]
    [InlineData("reviewer", 3)]
    [InlineData("worker", 3)]
    [InlineData("tester", 3)]
    public void OnlyDeveloperOwnedReportCriteriaUseTheReport(string owner, int criterionIndex)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner, criterionIndex);

        RecordReviewer(scenario);

        AssertSkippedWait(scenario, request, "verdict-not-met");
        AssertMatchingReportClosesControlWait();
    }

    [Theory]
    [InlineData("The Developer reports tests: deferred - AlphaTests, BetaTests.")]
    [InlineData("Report `tests: pass - AlphaTests, BetaTests`.")]
    [InlineData("Report `Tests: deferred - AlphaTests, BetaTests`.")]
    [InlineData("Report `tests: Deferred - AlphaTests, BetaTests`.")]
    [InlineData("Report `tests: deferredExtra - AlphaTests, BetaTests`.")]
    [InlineData("Report `tests: deferred - ` then `tests: deferred - AlphaTests, BetaTests`.")]
    public void UnrecognizedOrEmptyFirstReportLiteralKeepsTheVerdictReason(string criterion)
    {
        var scenario = CreateScenario(reportCriterion: criterion);
        var request = CreateWait(scenario, "developer");

        RecordReviewer(scenario);

        AssertSkippedWait(scenario, request, "verdict-not-met");
        AssertMatchingReportClosesControlWait();
    }

    [Fact]
    public void DifferentCandidateCannotBeReplacedByMatchingReport()
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, "developer");

        RecordReviewer(scenario, candidate: "A");

        AssertSkippedWait(scenario, request, "candidate-differs");
        AssertMatchingReportClosesControlWait();
    }

    private static void AssertMatchingReportClosesControlWait()
    {
        var control = CreateScenario();
        var request = CreateWait(control, "developer");
        RecordReviewer(control);
        Assert.Equal(WorkTaskStatus.Completed, control.Reviewer.Status);
        Assert.True(request.IsCompleted);
        Assert.StartsWith(ClosurePrefix, request.Answer);
    }

    private static void AssertSkippedWait(Scenario scenario, HumanInputRequest request, string reason)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Null(request.AuthoritativeAnswer);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, skipped.Kind);
        Assert.Equal(scenario.Reviewer.Id, skipped.TaskId);
        Assert.Equal($"{SkippedPrefix} request={request.Id.Value[..8]} reason={reason}", skipped.Message);
        Assert.Same(request, Assert.Single(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id)));
    }

    private static Scenario CreateScenario(string testsField = MatchingTests, bool resultCommit = true,
        string reportCriterion = ReportCriterion)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect diff", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Close Developer report evidence waits", [planner, developer, reviewer]);
        string[] criteria = ["Full gate passes.", "Focused tests pass.", reportCriterion];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Bind evidence to the current candidate", criteria, VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = criteria
        });
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        if (resultCommit)
            kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: src/Candidate.cs", "commands: implement", $"tests: {testsField}",
            $"commit: {(resultCommit ? "B" : "none")}", "blockers: none", "assigned_scope_complete: true",
            "model_fit: fixture/model - adequate - deterministic implementation",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, output, string.Empty, DispatchTime.AddSeconds(1),
            WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: DispatchTime);
        if (resultCommit)
            kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, verification);
        else
        {
            // Model an adjudicated close without dispatch's no-change ResultCommit backfill.
            kernel.RecordTaskVerification(goal.Id, developer.Id, verification);
            kernel.ReportWorkerTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Adjudicated complete.");
        }
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.True(developer.LastVerification!.WorkerResultPresent);
        if (resultCommit)
        {
            Assert.Equal("B", developer.LastDispatch!.ResultCommit);
            Assert.Equal(VerificationEvidenceSource.Dispatch, developer.LastVerification.EvidenceSource);
        }
        return new Scenario(kernel, goal, planner, developer, reviewer);
    }

    private static HumanInputRequest CreateWait(Scenario scenario, string owner, int criterionIndex = 3)
    {
        var identity = new PlannerEvidenceIdentity(scenario.Goal.Id.Value, scenario.Planner.Id.Value,
            scenario.Goal.AuthoritativeBrief.Version, criterionIndex, "developer-report");
        var payload = new PlannerEvidencePayload(criterionIndex, "developer-report", "prospective", owner,
            "Recorded tests field", "Worker reports; Acceptance executes", null, null, null);
        var request = scenario.Kernel.RequestHumanInputDeduplicated(scenario.Goal.Id, scenario.Planner.Id,
            "Provide the recorded tests evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: owner, plannerIdentity: identity, plannerEvidence: payload).Request;
        Assert.False(request.IsCompleted);
        return request;
    }

    private static void RecordReviewer(Scenario scenario, string candidate = "B")
    {
        var startedAt = DispatchTime.AddSeconds(3);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: candidate));
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            "criteria_verdicts: [" +
                """{"criterion_index":0,"verdict":"not-verifiable","evidence":"Full acceptance gate"},""" +
                """{"criterion_index":1,"verdict":"not-verifiable","evidence":"Focused tests"},""" +
                """{"criterion_index":2,"verdict":"not-verifiable","evidence":"Developer reports; Acceptance executes"}]""",
            "verdict: pass", "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, startedAt.AddSeconds(1),
            WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate", DispatchStartedAt: startedAt));
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Planner,
        TaskSpec Developer, TaskSpec Reviewer);
}
