using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsUnchangedCandidateRejectedVerdict
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void ConductorRejectedExitSuccessIsDispatchableOnUnchangedCandidate(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify candidate", role);
        var goal = kernel.CreateGoal("Reject a blocking finding", [task]);
        kernel.ActivateGoal(goal.Id, ConductorDriverTests.DefaultAgents());
        var identity = new CandidateIdentity("patch", "base", "manifest");
        const string selection = "Infrastructure.Tests: FullyQualifiedName~GoalWorktreeTests";
        const string sha = "same-sha";
        if (role == AgentRole.Reviewer)
            kernel.RecordPreReviewEvidence(goal.Id, task.Id, new PreReviewEvidenceReceipt(
                goal.Id.Value, 1, sha, [selection], PreReviewEvidenceDisposition.Green,
                1, 0, [new PreReviewEvidenceCheckReceipt("focused", selection, true, 0)],
                [], "seed", "fixture://green", DateTimeOffset.UtcNow.AddMinutes(-2)));

        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow.AddMinutes(-2),
            CandidateIdentity: identity));
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 0, BlockingFindingResult(), "",
            DateTimeOffset.UtcNow.AddMinutes(-1), WorkerResultPresent: true,
            CandidateIdentity: identity));

        var rejection = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("open blocking stable_id(s): B-1", StringComparison.Ordinal)));
        Xunit.Assert.Contains("WORKER_RESULT", rejection.Message, StringComparison.Ordinal);
        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        kernel.RetryTask(goal.Id, task.Id, "repeat without new input", RetryCause.UnchangedContextRepeat);

        Xunit.Assert.Null(UnchangedCandidateRule.Evaluate(goal, task, identity));
        var dispatches = 0;
        var evidenceRuns = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                sha, [selection], selection, "same focused tests", false, false),
            runFocusedEvidence: (_, _) =>
            {
                evidenceRuns++;
                return ConductorDriverTests.PassingPreReviewEvidence(selection);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); });
        driver.OverrideCandidateIdentityResolverForTests(_ => identity);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(0, evidenceRuns);
        Xunit.Assert.Equal(1, dispatches);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    private static string BlockingFindingResult() => string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: test.exe",
        "tests: pass - focused fixture",
        "commit: none",
        "blockers: none",
        """findings: [{"stable_id":"B-1","state":"open","location":{"file":"src/A.cs","region":"A.Run"},"description":"Correctness defect.","severity":"blocking"}]""",
        "touched_anchors: []",
        "criteria_verdicts: []",
        "verdict: pass",
        "model_fit: fixture/model - adequate - deterministic verification",
        "skills: none",
        "confidence: high",
        "END_WORKER_RESULT");
}
