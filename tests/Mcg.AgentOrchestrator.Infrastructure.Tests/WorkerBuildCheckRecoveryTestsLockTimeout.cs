using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRecoveryTestsLockTimeout
{
    private const string LockTimeoutOutput =
        "worker_build_receipt=receipt-missing; build_evidence_producer=orchestrator;" +
        " FAIL build: 1 error(s) (Invoke-WorkerBuildCheck) projects=1 logs=unavailable\n" +
        @"error: build-check apparatus failure: Timed out waiting for build lease execution lock: C:\x\build-slots\build-1.lock";
    private const string CompilerError = "src/Feature.cs(12,4): error CS0103: missing [App.csproj]";

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void LockTimeoutWithoutLogsCheckpointsAndRetries(bool timeoutInStandardError)
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var originalHead = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        Assert.False(Directory.Exists(artifacts));
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        FailBuildCheck(kernel, goal, task, worktree,
            timeoutInStandardError ? string.Empty : LockTimeoutOutput,
            timeoutInStandardError ? LockTimeoutOutput : string.Empty);
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var dispatches = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts,
            () => dispatches++, retries, otherRetries);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var checkpoint = ReadGit(worktree, "rev-parse", "HEAD");
        Assert.NotEqual(originalHead, checkpoint);
        Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        Assert.Equal($"checkpoint: worker-build-check-failed recovery 1/2 for task {task.Id.Value}",
            ReadGit(worktree, "log", "-1", "--format=%s"));
        var feedback = Assert.Single(retries);
        Assert.Contains("lock", feedback, StringComparison.Ordinal);
        Assert.Contains("build check never ran", feedback, StringComparison.Ordinal);
        Assert.Contains(checkpoint, feedback, StringComparison.Ordinal);
        Assert.Contains("Rerun scripts/Invoke-WorkerBuildCheck.ps1", feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("worker build errors:", feedback, StringComparison.Ordinal);
        Assert.Equal(feedback, task.AcceptedRetryFeedback!.Message);
        Assert.Empty(otherRetries);
        Assert.Equal(1, task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, task.CriterionRetryCount);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, dispatches);
    }

    [Xunit.Fact]
    public void ThirdLockTimeoutCheckpointsAndEscalatesWithoutCompilerErrors()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts,
            () => { }, retries, otherRetries);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var originalHead = ReadGit(worktree, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(worktree, "seed.txt"), $"change {attempt}");
            FailBuildCheck(kernel, goal, task, worktree, string.Empty, LockTimeoutOutput);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var checkpoint = ReadGit(worktree, "rev-parse", "HEAD");
            Assert.NotEqual(originalHead, checkpoint);
            Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
            if (attempt < 3)
            {
                Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
                Assert.Equal(attempt, retries.Count);
                Assert.Equal(attempt, task.WorkerBuildCheckRecoveryCount);
                Assert.Contains(checkpoint, retries[^1], StringComparison.Ordinal);
                Assert.Contains("lock", retries[^1], StringComparison.Ordinal);
                Assert.DoesNotContain("worker build errors:", retries[^1], StringComparison.Ordinal);
            }
            else
            {
                var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
                Assert.Contains("lock", escalated.Reason, StringComparison.Ordinal);
                Assert.Contains(checkpoint, escalated.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("worker build errors:", escalated.Reason, StringComparison.Ordinal);
                Assert.Equal($"checkpoint: worker-build-check-failed exhausted 2/2 for task {task.Id.Value}",
                    ReadGit(worktree, "log", "-1", "--format=%s"));
            }
        }
        Assert.Equal(2, retries.Count);
        Assert.Empty(otherRetries);
        Assert.Equal(2, task.WorkerBuildCheckRecoveryCount);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void LockTimeoutWithCompilerDiagnosticEscalatesWithoutCheckpoint(bool errorInStandardError)
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var originalHead = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        var diagnostic = "  " + CompilerError + "\r\n";
        FailBuildCheck(kernel, goal, task, worktree,
            errorInStandardError ? string.Empty : diagnostic,
            LockTimeoutOutput + (errorInStandardError ? "\n" + diagnostic : string.Empty));
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts,
            () => { }, retries, otherRetries);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(originalHead, ReadGit(worktree, "rev-parse", "HEAD"));
        Assert.NotEqual(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        Assert.Empty(retries);
        Assert.Empty(otherRetries);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    private static ConductorDriver RecoveryDriver(
        AgentOrchestratorKernel kernel, string executionDirectory, string artifacts,
        Action dispatch, List<string> retries, List<string> otherRetries) =>
        MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            executionDirectory: executionDirectory,
            dispatchAndStart: _ => { dispatch(); return DispatchStartOutcome.Started(); },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                otherRetries.Add(message);
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: roundKind);
            },
            workerBuildRecoveryRetry: (goalId, taskId, message) =>
            {
                retries.Add(message);
                return kernel.RetryTaskAfterWorkerBuildCheckRecovery(goalId, taskId, message);
            },
            workerBuildArtifactsPath: _ => artifacts);

    private static void FailBuildCheck(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string worktree,
        string standardOutput, string standardError)
    {
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", worktree, 1, standardOutput,
                standardError + Environment.NewLine +
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed),
                DateTimeOffset.UtcNow));
    }

    private static string ReadGit(string worktree, params string[] arguments)
    {
        var result = GitCli.Run(worktree, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
