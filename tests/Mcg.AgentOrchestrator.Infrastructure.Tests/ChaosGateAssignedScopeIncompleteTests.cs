using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ChaosGateAssignedScopeIncompleteTests : ChaosGateTestBase
{
    [Xunit.Fact(DisplayName = "AssignedScopeComplete_production_reconciliation_preserves_legacy_success_when_absent")]
    public void ProductionReconciliationPreservesLegacySuccessWhenScopeIsAbsent()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            WorkerResultBlock("src/Feature.cs", "focused verification", "pass - focused verification passed"),
            string.Empty,
            mutateWorktree: worktree => CommitSourceFile(worktree, "src/Feature.cs", "// legacy candidate"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastVerification!.HasCommittedChanges);
        Assert.Null(task.LastVerification.AssignedScopeComplete);
        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.DoesNotContain("incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AssignedScopeComplete_production_reconciliation_routes_false_to_bounded_revision_and_preserves_commit")]
    public void ProductionReconciliationRoutesFalseToBoundedRevisionAndPreservesCommit()
    {
        var root = CreateSeededRepo();
        var output = WorkerResultBlock("src/Feature.cs", "focused verification", "pass - focused verification passed")
            .Replace("END_WORKER_RESULT", "assigned_scope_complete: false" + Environment.NewLine + "END_WORKER_RESULT", StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            output,
            string.Empty,
            mutateWorktree: worktree => CommitSourceFile(worktree, "src/Feature.cs", "// incomplete candidate"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.True(task.LastVerification!.HasCommittedChanges);
        Assert.False(task.LastVerification.AssignedScopeComplete);
        Assert.NotNull(task.LastDispatch!.ResultCommit);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.Contains("rule=incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);

        var retries = 0;
        RetryCause? cause = null;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ => root,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            runAcceptance: _ => true,
            retryTaskWithCause: (goalId, taskId, message, _, retryCause) =>
            {
                retries++;
                cause = retryCause;
                return kernel.RetryTask(goalId, taskId, message, retryCause: retryCause);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(1, retries);
        Assert.Equal(RetryCause.ContractClarification, cause);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.NotNull(task.LastDispatch.ResultCommit);
    }
}
