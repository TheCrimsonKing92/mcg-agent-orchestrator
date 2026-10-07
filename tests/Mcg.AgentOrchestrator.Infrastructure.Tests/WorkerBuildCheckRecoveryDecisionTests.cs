using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Each case owns its temporary repository and artifacts; the fixture supplies the isolated dotnet root.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRecoveryDecisionTests
{
    private const string FirstError = "src/Feature.cs(12,4): error CS0103: The name 'missing' does not exist [App.csproj]";
    private const string SecondError = "src/Feature.cs(19,8): error CS1628: Cannot use ref parameter [App.csproj]";
    // Literal terminal reason at main b3d3a30b9 for these worker-build-check-failed arrangements.
    private const string MainPolicyReason =
        "Goal is in Failed state; TaskFailed: Dispatch failed: rule=worker-build-check-failed: test.exe; operator action required";
    private const string LockTimeoutOutput =
        "worker_build_receipt=receipt-missing; build_evidence_producer=orchestrator;" +
        " FAIL build: 1 error(s) (Invoke-WorkerBuildCheck) projects=1 logs=unavailable\n" +
        @"error: build-check apparatus failure: Timed out waiting for build lease execution lock: C:\x\build-slots\build-1.lock";

    [Xunit.Fact]
    public void NoBuildLogEscalationPreservesReasonAndNamesMissingEvidence()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var head = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "worker-build-logs"), "blocked directory");
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "uncommitted");
        FailBuildCheck(kernel, goal, task, worktree);
        var writes = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, writes);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(MainPolicyReason, escalated.Reason);
        Assert.Equal(escalated.Reason, Assert.Single(writes));
        AssertDecision(escalated.Decision, goal, "Escalate", "build-log-unavailable", 105, escalated.Reason);
        Assert.Equal(head, ReadGit(worktree, "rev-parse", "HEAD"));
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    [Xunit.Fact]
    public void CheckpointCommitFailurePreservesDiagnosticAndNamesFailure()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var head = ReadGit(worktree, "rev-parse", "HEAD");
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        WriteLog(artifacts, "20260924T100000000Z", FirstError);
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "seed   ");
        Assert.NotEqual(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        FailBuildCheck(kernel, goal, task, worktree);
        var writes = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, writes);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var expected = MainPolicyReason + Environment.NewLine +
            "checkpoint commit failed: Orchestrator commit-on-behalf found no substantive staged changes after ignoring whitespace." +
            Environment.NewLine + "worker build errors:" + Environment.NewLine + FirstError;
        Assert.Equal(expected, escalated.Reason);
        Assert.Equal(expected, Assert.Single(writes));
        AssertDecision(escalated.Decision, goal, "Escalate", "checkpoint-commit-failed", 108, expected);
        Assert.Equal(head, ReadGit(worktree, "rev-parse", "HEAD"));
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
    }

    [Xunit.Fact]
    public void RecoveryExhaustedPreservesCheckpointReasonAndNamesExhaustion()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        var writes = new List<string>();
        var retries = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, writes,
            retry: (goalId, taskId, message) =>
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
                var expected = MainPolicyReason + Environment.NewLine +
                    $"checkpoint commit: {ReadGit(worktree, "rev-parse", "HEAD")}" + Environment.NewLine +
                    "worker build errors:" + Environment.NewLine + FirstError + Environment.NewLine + SecondError;
                Assert.Equal(expected, escalated.Reason);
                Assert.Equal(expected, Assert.Single(writes));
                AssertDecision(escalated.Decision, goal, "Escalate", "build-check-recovery-exhausted", 110, expected);
                Assert.Equal($"checkpoint: worker-build-check-failed exhausted 2/2 for task {task.Id.Value}",
                    ReadGit(worktree, "log", "-1", "--format=%s"));
            }
        }
        Assert.Equal(2, retries);
        Assert.Equal(2, task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RetryAppliedHoldPreservesReasonAndNamesAppliedRetry(bool lockTimeout)
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var artifacts = Path.Combine(repository.WorkingDirectory, "build-artifacts");
        if (!lockTimeout) WriteLog(artifacts, "20260924T100000000Z", FirstError);
        else Assert.False(Directory.Exists(artifacts));
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "changed");
        FailBuildCheck(kernel, goal, task, worktree, lockTimeout ? LockTimeoutOutput : null);
        var writes = new List<string>();
        var feedback = new List<string>();
        var dispatches = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, artifacts, writes,
            retry: (_, _, message) => { feedback.Add(message); return task; }, dispatch: () => dispatches++);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        const string expected = "Worker build check retry was applied; dispatch start awaits a fresh lifecycle observation.";
        Assert.Equal(expected, held.Reason);
        AssertDecision(held.Decision, goal, "Hold", "build-check-retry-applied", 112, expected);
        var checkpoint = ReadGit(worktree, "rev-parse", "HEAD");
        var expectedFeedback = "worker-build-check-failed automatic recovery 1/2:" + Environment.NewLine +
            $"checkpoint commit {checkpoint}" + Environment.NewLine + (lockTimeout
                ? "build-slot lock timeout: the build check never ran because the build-slot lock timed out." +
                    Environment.NewLine + "Rerun scripts/Invoke-WorkerBuildCheck.ps1 after the last edit."
                : "Fix only what the build reports below, and run scripts/Invoke-WorkerBuildCheck.ps1 after the last edit." +
                    Environment.NewLine + "worker build errors:" + Environment.NewLine + FirstError);
        Assert.Equal(expectedFeedback, Assert.Single(feedback));
        Assert.Equal($"checkpoint: worker-build-check-failed recovery 1/2 for task {task.Id.Value}",
            ReadGit(worktree, "log", "-1", "--format=%s"));
        Assert.Equal(string.Empty, ReadGit(worktree, "status", "--porcelain"));
        Assert.Empty(writes);
        Assert.Equal(0, dispatches);
    }

    private static void AssertDecision(
        PolicyDecisionRecord? decision, Goal goal, string action, string evidence, int rung, string reason)
    {
        var record = Assert.IsType<PolicyDecisionRecord>(decision);
        Assert.Equal("failed-goal-recovery", record.Stage);
        Assert.Equal(action, record.Action);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(goal.Id.Value, Assert.Single(record.Facts, fact => fact.Name == "goalId").Value);
    }

    private static ConductorDriver RecoveryDriver(
        AgentOrchestratorKernel kernel, string executionDirectory, string artifacts, List<string> writes,
        Func<GoalId, TaskId, string, TaskSpec>? retry = null, Action? dispatch = null) =>
        MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            executionDirectory: executionDirectory,
            dispatchAndStart: _ => { dispatch?.Invoke(); return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, reason) => writes.Add(reason),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Unexpected generic retry."),
            workerBuildRecoveryRetry: retry ?? kernel.RetryTaskAfterWorkerBuildCheckRecovery,
            workerBuildArtifactsPath: _ => artifacts);

    private static void FailBuildCheck(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string worktree, string? lockTimeoutOutput = null)
    {
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", worktree, 1,
                lockTimeoutOutput is null ? "Worker build check failed; diagnostics are in the build log." : string.Empty,
                (lockTimeoutOutput is null ? string.Empty : lockTimeoutOutput + Environment.NewLine) +
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
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
