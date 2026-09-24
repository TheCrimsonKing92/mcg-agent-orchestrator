using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePrecheckTests
{
    [Xunit.Fact]
    public void VerifiedNoChangeDeveloperRoundPassesAcceptancePrecheck()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Accept a verified no-change Developer round",
            [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Test", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        const string candidate = "48422231916172e8d172a0cc0428d13d222c071c";
        var evidenceHash = new string('a', 64);
        var receipt = new WorkerContextPackageReceipt(
            "ctxpkg-acceptance-precheck",
            [new WorkerContextSectionReceipt(
                $"goal/review-finding-receipts/{evidenceHash}.json",
                1,
                1,
                evidenceHash,
                ContextDeliveryMode.OnDemandFile,
                ContextContractVersion.V1.Value,
                [AgentRole.Developer])],
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            ProviderUsageValue.Unknown("test"),
            EarlyConvergenceEligible: true,
            EarlyConvergenceCandidateSha: candidate,
            EarlyConvergenceReceiptHashes: [evidenceHash]);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\work", DateTimeOffset.UtcNow,
            ContextPackageReceipt: receipt));
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, candidate);
        kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, ["Re-run verification against the current candidate."]);
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "codex exec",
            "C:\\work",
            1,
            "WORKER_RESULT:\nfiles: none\ntests: pass - focused verification completed\nblockers: none\nEND_WORKER_RESULT",
            DispatchRejectionDiagnosticMarker.Format(true, 0, "none") + Environment.NewLine +
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing),
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        foreach (var task in goal.Tasks.Where(task => task.Id != developer.Id))
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker finished.");
            kernel.RecordTaskVerification(goal.Id, task.Id, Verification(0));
        }

        Assert.All(goal.Tasks, task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
        Assert.Equal(1, developer.LastVerification!.ExitCode);
        Assert.False(developer.LastVerification.Succeeded);
        Assert.True(developer.LastVerification.CompletionVerdictVerifiedSuccess);
        Assert.Equal("verified-no-change-round", developer.LastVerification.CompletionVerdictRule);
        Assert.True(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, false, null)]
    [Xunit.InlineData(1, true, "orchestrator failure")]
    [Xunit.InlineData(0, false, "orchestrator failure")]
    public void OtherFailedVerificationsBlockAcceptancePrecheck(
        int exitCode, bool verifiedSuccess, string? failureReason)
    {
        var (kernel, goal, task) = CompletedDeveloperGoal();
        kernel.RecordTaskVerification(goal.Id, task.Id,
            Verification(exitCode, verifiedSuccess, failureReason));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Keep the failed verification on a completed task.");

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(exitCode, task.LastVerification!.ExitCode);
        Assert.Equal(verifiedSuccess, task.LastVerification.CompletionVerdictVerifiedSuccess);
        Assert.Equal(failureReason, task.LastVerification.OrchestratorFailureReason);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    [Xunit.Fact]
    public void MissingVerificationBlocksAcceptancePrecheck()
    {
        var (_, goal, task) = CompletedDeveloperGoal();

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Null(task.LastVerification);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    [Xunit.Fact]
    public void NonCompletedTaskBlocksAcceptanceEvenWithSuccessVerdict()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reject a failed task", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Worker failed.");
        kernel.RecordTaskVerification(goal.Id, task.Id, Verification(1, verifiedSuccess: true));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Task did not complete.");

        Assert.NotEqual(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastVerification!.CompletionVerdictVerifiedSuccess);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    [Xunit.Fact]
    public void PassedVerificationPassesAcceptancePrecheck()
    {
        var (kernel, goal, task) = CompletedDeveloperGoal();
        kernel.RecordTaskVerification(goal.Id, task.Id, Verification(0));

        Assert.True(task.LastVerification!.Succeeded);
        Assert.True(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) CompletedDeveloperGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Verify task", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Worker finished.");
        return (kernel, goal, task);
    }

    private static TaskVerificationRecord Verification(
        int exitCode, bool verifiedSuccess = false, string? failureReason = null) =>
        new("codex exec", "C:\\work", exitCode, "", "", DateTimeOffset.UtcNow,
            OrchestratorFailureReason: failureReason,
            CompletionVerdictVerifiedSuccess: verifiedSuccess);
}
