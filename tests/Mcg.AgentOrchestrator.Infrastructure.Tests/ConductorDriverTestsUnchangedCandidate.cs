using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsUnchangedCandidate
{
    [Xunit.Fact]
    public void SameCandidateHoldsWithTypedReasonWithoutDispatch()
    {
        var (goal, task, identity) = RetriedGoal();
        var calls = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { calls++; return DispatchStartOutcome.Started(); });
        driver.OverrideCandidateIdentityResolverForTests(_ => identity);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.Equal(0, calls);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Equal(task.RequiredRole, Xunit.Assert.IsType<UnchangedCandidateHoldReason>(held.TypedReason).Role);
        Xunit.Assert.Contains("verdict=passed", held.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ChangedCandidateDispatchesAsBefore()
    {
        var (goal, _, _) = RetriedGoal();
        var calls = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { calls++; return DispatchStartOutcome.Started(); });
        driver.OverrideCandidateIdentityResolverForTests(_ => new CandidateIdentity("different", "base", "manifest"));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Xunit.Assert.Equal(1, calls);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact]
    public void ReviewerReuseOnSameCandidateHoldsBeforePreReviewStage()
    {
        var kernel = new AgentOrchestratorKernel();
        var reviewer = new TaskSpec(TaskId.New(), "Review the change", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Review the candidate", [reviewer]);
        kernel.ActivateGoal(goal.Id, ConductorDriverTests.DefaultAgents());
        var identity = new CandidateIdentity("patch", "base", "manifest");
        const string selection = "Infrastructure.Tests: FullyQualifiedName~GoalWorktreeTests";
        const string sha = "same-sha";
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value, 1, sha, [selection], PreReviewEvidenceDisposition.Green,
            1, 0, [new PreReviewEvidenceCheckReceipt("focused", selection, true, 0)],
            [], "seed", "fixture://green", DateTimeOffset.UtcNow.AddMinutes(-2)));
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow.AddMinutes(-2),
            CandidateIdentity: identity));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "test.exe", "C:\\tmp", 0, "WORKER_RESULT:\nblockers: none\nverdict: pass\nEND_WORKER_RESULT", "", DateTimeOffset.UtcNow.AddMinutes(-1),
            WorkerResultPresent: true, CandidateIdentity: identity));
        kernel.RetryTask(goal.Id, reviewer.Id, "repeat without new input", RetryCause.UnchangedContextRepeat);

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
        Xunit.Assert.Equal(0, dispatches);
        Xunit.Assert.IsType<UnchangedCandidateHoldReason>(
            Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).TypedReason);
        Xunit.Assert.Single(reviewer.PreReviewEvidenceHistory);
    }

    private static (Goal Goal, TaskSpec Task, CandidateIdentity Identity) RetriedGoal()
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal();
        var task = Xunit.Assert.Single(goal.Tasks);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        ConductorDriverTests.DispatchTask(kernel, goal, task);
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, CandidateIdentity: identity));
        kernel.RetryTask(goal.Id, task.Id, "retry without new input", RetryCause.UnchangedContextRepeat);
        return (goal, task, identity);
    }
}
