using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRecoveryTestsEvidenceMissingLockTimeout
{
    private const string Incident =
        "FAIL build: 1 error(s) (Invoke-WorkerBuildCheck) projects=1 warnings=0 captured_chars=0 elapsed_ms=300412 logs=unavailable\n" +
        @"error: build-check apparatus failure: Timed out waiting for build lease execution lock: C:\x\build-slots\build-1.lock";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void EvidenceMissing_LockTimeout_OnlyRecoversWithoutCompilerError(bool compilerError)
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        Assert.Equal(AgentRole.Developer, task.RequiredRole);
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var originalHead = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        Assert.False(Directory.Exists(artifacts));
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", worktree, 1, string.Empty,
                Incident + (compilerError ? "\nsrc/Feature.cs(12,4): error CS0103: missing [App.csproj]" : string.Empty) +
                Environment.NewLine + DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
                DateTimeOffset.UtcNow));
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, retries, otherRetries);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        if (compilerError)
        {
            Assert.Empty(retries);
            Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
        }
        else
        {
            Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            var checkpoint = ReadGit(worktree, "rev-parse", "HEAD");
            Assert.NotEqual(originalHead, checkpoint);
            Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
            Assert.Equal($"checkpoint: worker-build-check-failed recovery 1/2 for task {task.Id.Value}",
                ReadGit(worktree, "log", "-1", "--format=%s"));
            Assert.Contains("build check never ran", Assert.Single(retries), StringComparison.Ordinal);
            Assert.Equal(1, task.WorkerBuildCheckRecoveryCount);
            Assert.Empty(otherRetries);
        }
    }

    private static ConductorDriver RecoveryDriver(
        AgentOrchestratorKernel kernel, string executionDirectory, string artifacts,
        List<string> retries, List<string> otherRetries) =>
        MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            executionDirectory: executionDirectory,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
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

    private static string ReadGit(string worktree, params string[] arguments)
    {
        var result = GitCli.Run(worktree, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
