using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperDeferredNoChangeDispatchTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void RetryWithNamedDeferredClassCompletesOnUnchangedCandidate()
    {
        var (kernel, goal, task, process, clock, candidate) = Scenario();

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.True(task.Status == WorkTaskStatus.Completed,
            $"Actual status: {task.Status}; verification: {task.LastVerification?.StandardError}; " +
            $"last task event: {goal.Timeline.LastOrDefault(evt => evt.TaskId == task.Id)?.Message}");
        Xunit.Assert.Equal(0, task.LastVerification!.ExitCode);
        Xunit.Assert.Equal(candidate, task.LastDispatch!.ResultCommit);
        Xunit.Assert.True(DeferredNoChangeOutcome.TryParse(
            task.LastVerification.StandardError, out var outcome));
        Xunit.Assert.Equal(["DeferredAlphaTests"], outcome.TestClasses);
        Xunit.Assert.Contains(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Message.Contains("DEFERRED_NO_CHANGE_OUTCOME", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id &&
            evt.Message.Contains("DISPATCH_REJECTED", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-retry")]
    [Xunit.InlineData("no-rationale")]
    [Xunit.InlineData("no-classes")]
    [Xunit.InlineData("blocker")]
    [Xunit.InlineData("dirty")]
    public void NonQualifyingRoundKeepsExistingDiagnostic(string missing)
    {
        var (kernel, goal, task, _, clock, _) = Scenario(missing);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Xunit.Assert.Equal(1, task.LastVerification!.ExitCode);
        var failure = goal.Timeline.Last(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskFailed).Message;
        if (missing == "dirty")
        {
            Xunit.Assert.Contains("left the worktree dirty",
                task.LastVerification.StandardError, StringComparison.Ordinal);
            Xunit.Assert.Contains("worktree=dirty",
                task.LastVerification.StandardError, StringComparison.Ordinal);
        }
        else if (missing == "blocker")
            Xunit.Assert.Contains("WORKER_RESULT reported blocker: source work remains",
                failure, StringComparison.Ordinal);
        else
            Xunit.Assert.Contains("DISPATCH_REJECTED role=Developer", failure,
                StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("DEFERRED_NO_CHANGE_OUTCOME", failure,
            StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task,
        TaskProcessRecord Process, MutableClock Clock, string Candidate) Scenario(string? missing = null)
    {
        var root = CreateSeededDispatchRepository();
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddMinutes(2));
        var output = (missing == "no-rationale" ? string.Empty :
                "NO_CHANGE: the current candidate already contains the repair.\n") +
            WorkerResultBlock(missing == "no-rationale" ? "Alpha.cs" : "none", "none",
                missing == "no-classes" ? "deferred - conductor will verify" :
                    "deferred - DeferredAlphaTests",
                commit: "none", blockers: missing == "blocker" ? "source work remains" : "none");
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, output, string.Empty, clock,
            mutateWorktree: missing == "dirty"
                ? worktree => File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted")
                : null,
            sandboxLowIntegrity: missing == "dirty");
        var candidate = ReadGit(process.WorkingDirectory, ["rev-parse", "HEAD"]);
        if (missing != "no-retry")
        {
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
                process with { CompletedAt = clock.UtcNow, ExitCode = 0 }, null);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed,
                "Prior candidate completed.");
            kernel.RetryTask(goal.Id, task.Id, "Review the unchanged candidate after evidence.");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", process.Command, process.WorkingDirectory,
                clock.UtcNow.AddMilliseconds(1), BaseCommit: candidate,
                SandboxLowIntegrity: missing == "dirty"));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                process with { StartedAt = clock.UtcNow.AddSeconds(1) });
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        else kernel.RecordDispatchBaseCommit(goal.Id, task.Id, candidate);
        return (kernel, goal, task, process, clock, candidate);
    }
}
