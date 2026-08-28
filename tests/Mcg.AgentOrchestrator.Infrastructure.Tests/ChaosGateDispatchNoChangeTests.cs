using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Security.Cryptography;

public sealed class ChaosGateDispatchNoChangeTests : ChaosGateTestBase
{
    // Gate 1: False-positive completion rejection
    [Xunit.Fact(DisplayName = "ChaosGate1_worker_exits_0_with_no_file_change_is_rejected")]
    public void Gate1_WorkerExitsZeroWithNoFileChange_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, process) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: null);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(
            "did not produce required relevant file-change evidence",
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing),
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Equal("0", File.ReadAllText(process.ExitCodePath).Trim());

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CancelledRetryCleanUnchangedPinsCandidateFromPostReapGitEvidence()
    {
        var root = CreateSeededRepo();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Cancel a clean unchanged Developer retry.",
            [new TaskSpec(TaskId.New(), "Inspect candidate.", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id,
            [new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))]);
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        var head = ReadGit(worktree, ["rev-parse", "HEAD"]);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("codex-cli", "prior", worktree, DispatchedAt, BaseCommit: head, ResultCommit: head));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Prior candidate completed.");
        kernel.RetryTask(goal.Id, task.Id, "Inspect unchanged candidate.");
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "retry",
                worktree,
                dispatchedAt,
                BaseCommit: head,
                WorktreeHeadSha: head,
                DirtyStateHash: Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant()));
        var process = new TaskProcessRecord(
            999999, "retry", worktree, "out.log", "err.log", "exit.txt", dispatchedAt, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .CancelLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(head, task.LastDispatch!.ResultCommit);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("kind=ConfirmedUnchanged", StringComparison.Ordinal) &&
            evt.Message.Contains($"head={head}", StringComparison.Ordinal) &&
            evt.Message.Contains("commits_after_dispatch=0", StringComparison.Ordinal));
    }
}
