using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRecoveryTestsCapacityBeforeStart
{
    private const string CapacityLine =
        "worker_build_invocation=0123456789abcdef0123456789abcdef; phase=Completed; outcome=CapacityBeforeStart; consumed=True";

    [Xunit.Fact]
    public void CapacityOutcomeRecoversWithCapacityFeedback()
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
        FailBuildEvidence(kernel, goal, task, worktree, CapacityLine);
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, retries, otherRetries);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var checkpoint = ReadGit(worktree, "rev-parse", "HEAD");
        Assert.NotEqual(originalHead, checkpoint);
        Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        Assert.Equal($"checkpoint: worker-build-check-failed recovery 1/2 for task {task.Id.Value}",
            ReadGit(worktree, "log", "-1", "--format=%s"));
        var feedback = Assert.Single(retries);
        Assert.Contains(WorkerBuildCheckRecoveryText.SlotCapacityBlock, feedback, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerBuildCheckRecoveryText.BuildSlotLockTimeoutBlock, feedback, StringComparison.Ordinal);
        Assert.Equal(1, task.WorkerBuildCheckRecoveryCount);
        Assert.Empty(otherRetries);
    }

    [Xunit.Fact]
    public void CapacityOutcomeIgnoresEarlierBuildLogErrors()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        WriteLog(artifacts, "20261023T050000000Z", "src/Old.cs(3,1): error CS0117: stale");
        Assert.True(File.Exists(Path.Combine(artifacts, "worker-build-logs", "20261023T050000000Z", "01-App.log")));
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        FailBuildEvidence(kernel, goal, task, worktree, CapacityLine);
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, retries, otherRetries);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var feedback = Assert.Single(retries);
        Assert.Contains(WorkerBuildCheckRecoveryText.SlotCapacityBlock, feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0117", feedback, StringComparison.Ordinal);
        Assert.Equal(1, task.WorkerBuildCheckRecoveryCount);
        Assert.Empty(otherRetries);
    }

    [Xunit.Fact]
    public void CompileTimeoutAfterStartIsNotRecovered()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        Assert.False(Directory.Exists(artifacts));
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        FailBuildEvidence(kernel, goal, task, worktree,
            CapacityLine.Replace("outcome=CapacityBeforeStart", "outcome=TimeoutAfterStart", StringComparison.Ordinal));
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, retries, otherRetries);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Empty(retries);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    [Xunit.Fact]
    public void CapacityOutcomeWithCompilerErrorIsNotRecovered()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        Assert.False(Directory.Exists(artifacts));
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        FailBuildEvidence(kernel, goal, task, worktree,
            CapacityLine + Environment.NewLine + "src/Feature.cs(12,4): error CS0103: missing [App.csproj]");
        var retries = new List<string>();
        var otherRetries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, retries, otherRetries);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Empty(retries);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    private static void FailBuildEvidence(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string worktree, string output)
    {
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", worktree, 1, string.Empty,
                output + Environment.NewLine +
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
                DateTimeOffset.UtcNow));
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

    private static void WriteLog(string artifacts, string run, params string[] lines)
    {
        var path = Path.Combine(artifacts, "worker-build-logs", run);
        Directory.CreateDirectory(path);
        File.WriteAllLines(Path.Combine(path, "01-App.log"), lines);
    }

    private static string ReadGit(string worktree, params string[] arguments)
    {
        var result = GitCli.Run(worktree, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
