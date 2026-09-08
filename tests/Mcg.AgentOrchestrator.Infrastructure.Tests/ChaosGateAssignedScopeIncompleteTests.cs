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
            RetainedPartialOutput(),
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
        var output = RetainedPartialOutput()
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

    [Xunit.Fact(DisplayName = "AssignedScopeComplete_production_reconciliation_escalates_false_when_the_bounded_retry_budget_is_exhausted")]
    public void ProductionReconciliationEscalatesFalseWhenBoundedRetryBudgetIsExhausted()
    {
        var root = CreateSeededRepo();
        var output = RetainedPartialOutput()
            .Replace("END_WORKER_RESULT", "assigned_scope_complete: false" + Environment.NewLine + "END_WORKER_RESULT", StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            output,
            string.Empty,
            mutateWorktree: worktree => CommitSourceFile(worktree, "src/Feature.cs", "// incomplete candidate"));
        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);
        var retryCalled = false;
        string? escalation = null;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithCause: (_, _, _, _, _) =>
            {
                retryCalled = true;
                throw new InvalidOperationException("The exhausted incomplete-scope round must not retry.");
            },
            writeEscalation: (_, _, reason) => escalation = reason);

        var result = driver.AdvanceOnce(
            goal,
            ConductorAutonomyPolicy.Permissive with { MaxCriterionRetries = 0 });

        Assert.False(retryCalled);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("exhausted bounded real-failure retries (0/0)", escalation!, StringComparison.Ordinal);
        Assert.NotNull(task.LastDispatch!.ResultCommit);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
    }

    [Xunit.Fact(DisplayName = "AssignedScopeComplete_true_still_requires_downstream_acceptance")]
    public void ScopeCompleteTrueStillRequiresDownstreamAcceptance()
    {
        var root = CreateSeededRepo();
        var output = RetainedPartialOutput()
            .Replace("END_WORKER_RESULT", "assigned_scope_complete: true" + Environment.NewLine + "END_WORKER_RESULT", StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            output,
            string.Empty,
            mutateWorktree: worktree => CommitSourceFile(worktree, "src/Feature.cs", "// complete candidate"));
        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);
        var acceptanceCalled = false;
        var landingCalled = false;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ =>
            {
                acceptanceCalled = true;
                return false;
            },
            land: _ =>
            {
                landingCalled = true;
                throw new InvalidOperationException("Acceptance failure must prevent landing.");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(acceptanceCalled);
        Assert.False(landingCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed or ConductorAdvanceOutcome.Escalated);
        Assert.True(task.LastVerification!.AssignedScopeComplete);
    }

    [Xunit.Fact(DisplayName = "AssignedScopeComplete_production_reconciliation_commits_dirty_incomplete_work_before_bounded_revision")]
    public void ProductionReconciliationCommitsDirtyIncompleteWorkBeforeBoundedRevision()
    {
        var root = CreateSeededRepo();
        var output = RetainedPartialOutput()
            .Replace("END_WORKER_RESULT", "assigned_scope_complete: false" + Environment.NewLine + "END_WORKER_RESULT", StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            output,
            string.Empty,
            mutateWorktree: worktree =>
            {
                var path = Path.Combine(worktree, "src", "Feature.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "// preserved dirty candidate");
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.True(task.LastVerification!.HasCommittedChanges);
        Assert.NotNull(task.LastDispatch!.ResultCommit);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Contains("rule=incomplete-scope-declaration", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AssignedScopeComplete_production_reconciliation_rejects_malformed_scope_without_coercing_absence")]
    public void ProductionReconciliationRejectsMalformedScopeWithoutCoercingAbsence()
    {
        var root = CreateSeededRepo();
        var output = RetainedPartialOutput()
            .Replace("END_WORKER_RESULT", "assigned_scope_complete: maybe" + Environment.NewLine + "END_WORKER_RESULT", StringComparison.Ordinal);
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            output,
            string.Empty,
            mutateWorktree: worktree => CommitSourceFile(worktree, "src/Feature.cs", "// malformed scope candidate"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Null(task.LastVerification!.AssignedScopeComplete);
        Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Assert.Contains("assigned_scope_complete requires true or false", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AssignedScopeComplete_production_reconciliation_does_not_trust_bounded_preview_when_full_stdout_is_unavailable")]
    public void ProductionReconciliationDoesNotTrustBoundedPreviewWhenFullStdoutIsUnavailable()
    {
        var root = CreateSeededRepo();
        var output = RetainedPartialOutput()
            .Replace("END_WORKER_RESULT", "assigned_scope_complete: false" + Environment.NewLine + "END_WORKER_RESULT", StringComparison.Ordinal);
        var (kernel, goal, task, process) = CreateChaosDispatch(
            root,
            AgentRole.Developer,
            output,
            string.Empty,
            mutateWorktree: worktree => CommitSourceFile(worktree, "src/Feature.cs", "// candidate with unavailable full stdout"));

        Stream OpenAndRemoveStdout(string path)
        {
            var content = File.ReadAllBytes(path);
            if (string.Equals(path, process.StandardOutputPath, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
            return new MemoryStream(content, writable: false);
        }

        new BackgroundDispatchRunner(
            isStillRunning: _ => false,
            openLogReadStream: OpenAndRemoveStdout)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal("missing", task.LastVerification!.FullStandardOutputUnavailableReason);
        Assert.Null(task.LastVerification.AssignedScopeComplete);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
    }

    private static string RetainedPartialOutput() => string.Join(
        Environment.NewLine,
        "The sequence/versioned retry and legacy repair remain outside this implementation slice.",
        WorkerResultBlock("src/Feature.cs", "focused verification", "pass - focused verification passed"));
}
