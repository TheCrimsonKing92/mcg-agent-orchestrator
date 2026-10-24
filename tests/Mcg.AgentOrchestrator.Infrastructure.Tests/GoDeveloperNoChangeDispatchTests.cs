using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawningRemainderB")]
public sealed class GoDeveloperNoChangeDispatchTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void GoNoChangeRoundAfterOwnCommitCompletes()
    {
        var (kernel, goal, task, clock, candidate) = Scenario(go: true);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed,
            $"Actual status: {task.Status}; verification: {task.LastVerification?.StandardError}; " +
            $"last task event: {goal.Timeline.LastOrDefault(evt => evt.TaskId == task.Id)?.Message}");
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal("succeeded-dispatch-completion-evidence", task.LastVerification.CompletionVerdictRule);
        Xunit.Assert.Contains($"GO_DEVELOPER_NO_CHANGE_ACCEPTED prior_commit={candidate} candidate_sha={candidate} " +
            "tests=main.TestStdoutIdempotency,main.TestStdoutWithJSONOutput,main.TestStderrSeparation",
            task.LastVerification.StandardError, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id &&
            (evt.Message.Contains("DISPATCH_REJECTED", StringComparison.Ordinal) ||
             evt.Message.Contains("DEFERRED_NO_CHANGE_OUTCOME", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void WorktreeWithoutGoModKeepsTheRejection()
    {
        var (kernel, goal, task, clock, _) = Scenario(go: false);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var failure = goal.Timeline.Last(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed).Message;
        Xunit.Assert.Contains("DISPATCH_REJECTED role=Developer", failure, StringComparison.Ordinal);
        Xunit.Assert.Contains("reason=verification-pattern-unmatched", failure, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("GO_DEVELOPER_NO_CHANGE_ACCEPTED", task.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task,
        MutableClock Clock, string Candidate) Scenario(bool go)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddMinutes(2));
        var commitAt = clock.UtcNow.AddSeconds(-30);
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, "Earlier committing round.", string.Empty, clock,
            mutateWorktree: worktree =>
            {
                var file = go ? "go.mod" : "implementation.txt";
                File.WriteAllText(Path.Combine(worktree, file), go ? "module example.test/fixture\n" : "own work");
                RunGit(worktree, ["add", file], commitAt);
                RunGit(worktree, ["commit", "-m", "Fixture own implementation"], commitAt);
            });
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single(other => other.Id == task.Id);
        var candidate = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD"]);
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id,
            ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD^"]));
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, candidate);
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
            process with { CompletedAt = clock.UtcNow, ExitCode = 0 }, null);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Own implementation committed.");
        Xunit.Assert.NotEqual(task.LastDispatch!.BaseCommit, task.LastDispatch.ResultCommit);
        Xunit.Assert.Equal(candidate, task.LastDispatch.ResultCommit);
        Xunit.Assert.Equal(go, File.Exists(Path.Combine(process.WorkingDirectory, "go.mod")));
        if (go) Xunit.Assert.Contains("go.mod", ReadGit(process.WorkingDirectory, ["ls-tree", "--name-only", "HEAD"]));
        Xunit.Assert.Empty(ReadGit(process.WorkingDirectory, ["status", "--porcelain"]));

        clock.Advance(TimeSpan.FromSeconds(1));
        kernel.RetryTask(goal.Id, task.Id, "Recheck the same candidate after evidence.");
        var logs = Path.GetDirectoryName(process.StandardOutputPath)!;
        var next = process with
        {
            StandardOutputPath = Path.Combine(logs, "go-no-change.out.log"),
            StandardErrorPath = Path.Combine(logs, "go-no-change.err.log"),
            ExitCodePath = Path.Combine(logs, "go-no-change.exit.txt"),
            StartedAt = clock.UtcNow,
            CompletedAt = null,
            ExitCode = null
        };
        File.WriteAllText(next.StandardOutputPath, GoDeveloperNoChangeCompletionTests.Output);
        File.WriteAllText(next.StandardErrorPath, string.Empty);
        File.WriteAllText(next.ExitCodePath, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", process.Command, process.WorkingDirectory, clock.UtcNow, BaseCommit: candidate));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, next);
        clock.Advance(TimeSpan.FromSeconds(1));
        return (kernel, goal, task, clock, candidate);
    }
}
