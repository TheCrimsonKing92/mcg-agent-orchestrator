using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Security.Cryptography;

public sealed class ChaosGateDispatchDirtyWorktreeTests : ChaosGateTestBase
{
    // Gate 4: Dirty worktree at completion
    [Xunit.Fact(DisplayName = "ChaosGate4_dirty_worktree_at_completion_is_rejected")]
    public void Gate4_DirtyWorktreeAtCompletion_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, process) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: wt => File.WriteAllText(Path.Combine(wt, "Dirty.cs"), "// uncommitted"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            DispatchFailureDiagnosticMarker.Prefix,
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Equal("0", File.ReadAllText(process.ExitCodePath).Trim());

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification);
        Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable, outcome.Kind);
        Assert.Contains("rule=dirty-dispatch-recovery", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CancelledRetryDirtyWorktreeFailsClosedWithPathReceipt()
    {
        var root = CreateSeededRepo();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Cancel a dirty Developer retry.",
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
        kernel.RetryTask(goal.Id, task.Id, "Inspect candidate.");
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
        File.WriteAllText(Path.Combine(worktree, "DirtyAfterDispatch.cs"), "// dirty");

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .CancelLatestProcess(kernel, goal.Id, task.Id);

        Assert.Null(task.LastDispatch!.ResultCommit);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("kind=Dirty", StringComparison.Ordinal) &&
            evt.Message.Contains("DirtyAfterDispatch.cs", StringComparison.Ordinal));
    }
}
