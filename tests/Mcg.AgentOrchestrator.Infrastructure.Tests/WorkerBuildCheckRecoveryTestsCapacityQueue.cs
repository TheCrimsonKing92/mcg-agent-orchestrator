using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static WorkerBuildCapacityGateTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckRecoveryTestsCapacityQueue
{
    [Fact]
    public void CapacityFailure_HoldsThenRetries_AsEnvironmentRound()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var root = new DotnetBuildStorageRoot(Path.Combine(repository.WorkingDirectory, "host-builds"));
        var clock = new RecordingTimeProvider();
        var held = Enumerable.Range(0, 2).Select(index =>
            DotnetBuildEnvironmentManager.BuildSlotExecutionLockPath(index, root)).ToHashSet(StringComparer.Ordinal);
        var retries = new List<string>();
        var dispatches = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, retries, () => dispatches++);
        var reads = 0;
        driver.UseWorkerBuildCapacityReader(() =>
        {
            reads++;
            return WorkerBuildCapacityGate.ReadHostSnapshot(root, clock, held.Contains);
        });
        var originalHead = Git(worktree, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "waiting change");
        FailBuild(kernel, goal, task, worktree);

        var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var heldOutcome = Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
        Assert.Equal("build-capacity-held", heldOutcome.Decision!.DiscriminatingEvidence);
        Assert.Equal(117, heldOutcome.Decision.Rung);
        Assert.Equal("worker build capacity: waiting for a host build permit; unheld=0/2; waiting=0; capacity rounds=1/3",
            heldOutcome.Reason);
        Assert.Empty(retries);
        Assert.Equal(0, dispatches);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(originalHead, Git(worktree, "rev-parse", "HEAD"));

        held.Clear();
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, reads);
        Assert.Equal(1, dispatches);
        Assert.NotEqual(originalHead, Git(worktree, "rev-parse", "HEAD"));
        Assert.Equal($"checkpoint: worker-build-capacity retry 1/3 for task {task.Id.Value}",
            Git(worktree, "log", "-1", "--format=%s"));
        var feedback = Assert.Single(retries);
        Assert.StartsWith("worker-build-capacity automatic retry 1/3:", feedback, StringComparison.Ordinal);
        Assert.Contains(WorkerBuildCheckRecoveryText.SlotCapacityBlock, feedback, StringComparison.Ordinal);
        Assert.Contains("Rerun scripts/Invoke-WorkerBuildCheck.ps1 after the last edit.", feedback, StringComparison.Ordinal);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        var secondRound = Assert.Single(WorkerRoundLedger.FromGoal(goal).Where(round => round.RoundIndex == 2));
        Assert.Equal(ReworkCauseFamily.Environment, secondRound.ReworkCause);
    }

    [Fact]
    public void CapacityRetries_ThenCompilerFailure_PreservesCompilerBudget()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var retries = new List<string>();
        var dispatches = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, retries, () => dispatches++);
        UseFreeCapacity(driver, repository.WorkingDirectory);
        for (var round = 1; round <= 2; round++)
        {
            File.WriteAllText(Path.Combine(worktree, "seed.txt"), "capacity " + round);
            FailBuild(kernel, goal, task, worktree);
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            Assert.Equal(round, retries.Count);
            Assert.StartsWith($"worker-build-capacity automatic retry {round}/3:", retries[^1], StringComparison.Ordinal);
            Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
        }
        WriteCompilerLog(repository.WorkingDirectory);
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "compiler change");
        FailBuild(kernel, goal, task, worktree, WorkerBuildOutcome.CompilerFailure);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(3, retries.Count);
        Assert.Equal(3, dispatches);
        Assert.Equal(2, WorkerBuildCapacityEvidence.CapacityDispatchCount(task));
        Assert.Equal(1, task.WorkerBuildCheckRecoveryCount);
        Assert.StartsWith("worker-build-check-failed automatic recovery 1/2:", retries[^1], StringComparison.Ordinal);
        Assert.Equal($"checkpoint: worker-build-check-failed recovery 1/2 for task {task.Id.Value}",
            Git(worktree, "log", "-1", "--format=%s"));
    }

    [Fact]
    public void FourthCapacityDispatch_CheckpointsAndEscalates_WithoutCompilerCharge()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        var retries = new List<string>();
        var dispatches = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, retries, () => dispatches++);
        UseFreeCapacity(driver, repository.WorkingDirectory);
        for (var round = 1; round <= 4; round++)
        {
            var before = Git(worktree, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(worktree, "seed.txt"), "capacity " + round);
            FailBuild(kernel, goal, task, worktree);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.NotEqual(before, Git(worktree, "rev-parse", "HEAD"));
            Assert.Equal(string.Empty, Git(worktree, "status", "--porcelain"));
            Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
            Assert.Equal(round, WorkerBuildCapacityEvidence.CapacityDispatchCount(task));
            if (round <= 3)
            {
                Assert.Equal(round, dispatches);
                Assert.Equal(round, retries.Count);
                Assert.StartsWith($"worker-build-capacity automatic retry {round}/3:", retries[^1], StringComparison.Ordinal);
            }
            else
            {
                var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
                Assert.Equal("build-capacity-retries-exhausted", escalated.Decision!.DiscriminatingEvidence);
                Assert.Equal(118, escalated.Decision.Rung);
                Assert.Contains(WorkerBuildCheckRecoveryText.SlotCapacityBlock, escalated.Reason, StringComparison.Ordinal);
                Assert.Contains($"checkpoint commit: {Git(worktree, "rev-parse", "HEAD")}", escalated.Reason, StringComparison.Ordinal);
                Assert.Equal($"checkpoint: worker-build-capacity exhausted 3/3 for task {task.Id.Value}",
                    Git(worktree, "log", "-1", "--format=%s"));
            }
        }
        Assert.Equal(3, retries.Count);
        Assert.Equal(3, dispatches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CapacityDispatch_PreexistingCompilerCount_IsPreservedAndBypassed(int count)
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        for (var index = 0; index < count; index++)
        {
            FailBuild(kernel, goal, task, worktree, WorkerBuildOutcome.CompilerFailure);
            kernel.RetryTaskAfterWorkerBuildCheckRecovery(goal.Id, task.Id, "compiler budget setup");
        }
        Assert.Equal(count, task.WorkerBuildCheckRecoveryCount);
        var retries = new List<string>();
        var dispatches = 0;
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, retries, () => dispatches++);
        UseFreeCapacity(driver, repository.WorkingDirectory);
        File.WriteAllText(Path.Combine(worktree, "seed.txt"), "capacity change");
        FailBuild(kernel, goal, task, worktree);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(count, task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(1, dispatches);
        Assert.StartsWith("worker-build-capacity automatic retry 1/3:", Assert.Single(retries), StringComparison.Ordinal);
        Assert.Equal($"checkpoint: worker-build-capacity retry 1/3 for task {task.Id.Value}",
            Git(worktree, "log", "-1", "--format=%s"));
    }

    [Fact]
    public void TextOnlyCapacity_HeldReader_IsNeverConsultedAndKeepsLegacyRecovery()
    {
        using var repository = CreateSeededGitRepository();
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(repository.WorkingDirectory, goal.Id);
        FailBuild(kernel, goal, task, worktree, typed: false);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var retries = new List<string>();
        var driver = RecoveryDriver(kernel, repository.WorkingDirectory, retries, () => { });
        var calls = 0;
        driver.UseWorkerBuildCapacityReader(() => { calls++; return new(0, 2, 0, true); });
        var actual = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var count = task.WorkerBuildCheckRecoveryCount;
        var feedback = Assert.Single(retries);

        var controlKernel = new AgentOrchestratorKernel();
        controlKernel.ReplaceGoalWithSnapshot(snapshot);
        var controlGoal = controlKernel.GetGoal(goal.Id);
        var controlRetries = new List<string>();
        var controlDriver = RecoveryDriver(controlKernel, repository.WorkingDirectory, controlRetries, () => { });
        controlDriver.UseWorkerBuildCapacityReader(() => throw new InvalidOperationException("Reader must not be called."));
        var control = controlDriver.AdvanceOnce(controlGoal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, calls);
        Assert.Equal(1, count);
        Assert.Equal(count, controlGoal.Tasks.Single().WorkerBuildCheckRecoveryCount);
        Assert.Equal(Assert.Single(controlRetries), feedback);
        Assert.StartsWith("worker-build-check-failed automatic recovery 1/2:", feedback, StringComparison.Ordinal);
        var actualOutcome = Assert.IsType<ConductorAdvanceOutcome.Executed>(actual.Outcome);
        var controlOutcome = Assert.IsType<ConductorAdvanceOutcome.Executed>(control.Outcome);
        var actualDecision = Assert.IsType<PolicyDecisionRecord>(actualOutcome.Decision);
        var controlDecision = Assert.IsType<PolicyDecisionRecord>(controlOutcome.Decision);
        Assert.Equal(controlDecision.Facts.ToArray(), actualDecision.Facts.ToArray());
        Assert.Equal(controlDecision with { Facts = actualDecision.Facts }, actualDecision);
        Assert.Equal(controlOutcome with { Decision = actualDecision }, actualOutcome);
    }

    private static void FailBuild(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string worktree,
        WorkerBuildOutcome outcome = WorkerBuildOutcome.CapacityBeforeStart, bool typed = true)
    {
        DispatchTask(kernel, goal, task, workingDirectory: worktree);
        if (typed) RecordInvocation(kernel, goal, task, outcome);
        var marker = outcome == WorkerBuildOutcome.CompilerFailure
            ? DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed : DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing;
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord("test.exe", worktree, 1,
            string.Empty, $"outcome={outcome}" + Environment.NewLine + DispatchFailureDiagnosticMarker.Format(marker), DateTimeOffset.UtcNow));
    }

    private static ConductorDriver RecoveryDriver(AgentOrchestratorKernel kernel, string directory,
        List<string> retries, Action dispatch) => MakeDriver(
        getFacts: _ => GoalLifecycleFacts.None, executionDirectory: directory,
        dispatchAndStart: _ => { dispatch(); return DispatchStartOutcome.Started(); },
        retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Unexpected non-build retry."),
        workerBuildRecoveryRetry: (goalId, taskId, message) =>
        {
            retries.Add(message);
            return kernel.RetryTaskAfterWorkerBuildCheckRecovery(goalId, taskId, message);
        }, workerBuildArtifactsPath: _ => Path.Combine(directory, "build-artifacts"));

    private static void UseFreeCapacity(ConductorDriver driver, string directory)
    {
        var root = new DotnetBuildStorageRoot(Path.Combine(directory, "host-builds"));
        var clock = new RecordingTimeProvider();
        driver.UseWorkerBuildCapacityReader(() => WorkerBuildCapacityGate.ReadHostSnapshot(root, clock, _ => false));
    }

    private static void WriteCompilerLog(string directory)
    {
        var path = Path.Combine(directory, "build-artifacts", "worker-build-logs", "20261025T050000000Z");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "01-App.log"), "src/App.cs(3,1): error CS0117: broken");
    }

    private static string Git(string worktree, params string[] arguments)
    {
        var result = GitCli.Run(worktree, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }
}
