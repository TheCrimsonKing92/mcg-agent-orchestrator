using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRecoveryTests
{
    private const string FirstError = "src/Feature.cs(12,4): error CS0103: The name 'missing' does not exist [App.csproj]";
    private const string SecondError = "src/Feature.cs(19,8): error CS1628: Cannot use ref parameter [App.csproj]";

    [Xunit.Fact]
    public void FailedBuildCheckCommitsAndRetriesWithBothErrorsAndCheckpointSha()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var originalHead = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        WriteLog(artifacts, "20260924T100000000Z", FirstError, SecondError);
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "changed");
        FailBuildCheck(kernel, goal, task, worktree);
        var dispatches = 0;
        var automaticRetryMessages = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, () => dispatches++,
            onAutomaticRetry: automaticRetryMessages.Add);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var checkpoint = ReadGit(worktree, "rev-parse", "HEAD");
        Assert.NotEqual(originalHead, checkpoint);
        Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        Assert.Equal($"checkpoint: worker-build-check-failed recovery 1/2 for task {task.Id.Value}",
            ReadGit(worktree, "log", "-1", "--format=%s"));
        Assert.Equal(1, task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, task.CriterionRetryCount);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, dispatches);
        Assert.Empty(automaticRetryMessages);
        Assert.Contains(FirstError, task.AcceptedRetryFeedback!.Message, StringComparison.Ordinal);
        Assert.Contains(SecondError, task.AcceptedRetryFeedback.Message, StringComparison.Ordinal);
        Assert.Contains(checkpoint, task.AcceptedRetryFeedback.Message, StringComparison.Ordinal);
        Assert.Contains(checkpoint, task.CriterionRetryFeedback.Single(), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ThirdFailureCheckpointsAndEscalatesWithErrors()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        var retries = 0;
        var automaticRetryMessages = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, () => { }, automaticRetryMessages.Add,
            (goalId, taskId, message) =>
            {
                retries++;
                return kernel.RetryTaskAfterWorkerBuildCheckRecovery(goalId, taskId, message);
            });
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            File.WriteAllText(Path.Combine(worktree, "seed.txt"), $"change {attempt}");
            WriteLog(artifacts, $"20260924T10000{attempt}000Z", FirstError, SecondError);
            FailBuildCheck(kernel, goal, task, worktree);
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            if (attempt < 3)
                Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            else
            {
                var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
                Assert.Contains("worker build errors:", escalated.Reason, StringComparison.Ordinal);
                Assert.Contains(FirstError, escalated.Reason, StringComparison.Ordinal);
                Assert.Contains(SecondError, escalated.Reason, StringComparison.Ordinal);
                Assert.Contains(ReadGit(worktree, "rev-parse", "HEAD"), escalated.Reason, StringComparison.Ordinal);
                Assert.Equal($"checkpoint: worker-build-check-failed exhausted 2/2 for task {task.Id.Value}",
                    ReadGit(worktree, "log", "-1", "--format=%s"));
            }
        }
        Assert.Equal(2, retries);
        Assert.Empty(automaticRetryMessages);
        Assert.Equal(2, task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UnreadableOrErrorFreeNewestLogEscalatesWithoutCommit(bool errorFree)
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var head = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        if (errorFree)
        {
            WriteLog(artifacts, "20260924T100000000Z", FirstError);
            WriteLog(artifacts, "20260924T100001000Z", "src/Feature.cs(1,1): warning CS0168: unused [App.csproj]");
        }
        else
        {
            Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts, "worker-build-logs"), "blocked directory");
        }
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        FailBuildCheck(kernel, goal, task, worktree);
        var retries = 0;
        var automaticRetryMessages = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, () => retries++,
            onAutomaticRetry: automaticRetryMessages.Add);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.DoesNotContain("worker build errors:", escalated.Reason, StringComparison.Ordinal);
        Assert.Equal(head, ReadGit(worktree, "rev-parse", "HEAD"));
        Assert.NotEqual(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        Assert.Equal(0, retries);
        Assert.Empty(automaticRetryMessages);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    private static ConductorDriver RecoveryDriver(
        AgentOrchestratorKernel kernel, string executionDirectory, string artifacts,
        Action dispatch, Action<string> onAutomaticRetry,
        Func<GoalId, TaskId, string, TaskSpec>? retry = null) =>
        MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            executionDirectory: executionDirectory,
            dispatchAndStart: _ => { dispatch(); return DispatchStartOutcome.Started(); },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                onAutomaticRetry(message);
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind);
            },
            workerBuildRecoveryRetry: retry ?? kernel.RetryTaskAfterWorkerBuildCheckRecovery,
            workerBuildArtifactsPath: _ => artifacts);

    private static void FailBuildCheck(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string worktree)
    {
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", worktree, 1, "Worker build check failed; diagnostics are in the build log.",
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
                DateTimeOffset.UtcNow));
    }

    private static void WriteLog(string artifacts, string run, params string[] lines)
    {
        var path = Path.Combine(artifacts, "worker-build-logs", run);
        Directory.CreateDirectory(path);
        File.WriteAllLines(Path.Combine(path, "01-App.log"), lines);
    }

    private static string ReadGit(string worktree, params string[] arguments)
    {
        var result = GitCli.Run(worktree, arguments);
        Xunit.Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
