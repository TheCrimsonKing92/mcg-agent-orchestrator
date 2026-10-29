using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("EnvMutation")]
public sealed class DispatchClassificationGitContractTests : WorkerDispatchTestSupport
{
    private static void AssertExitCode(string path, int expected)
    {
        Assert.True(DispatchExitArtifacts.TryRead(path, out var artifact));
        Assert.Equal(expected, artifact.ExitCode);
    }

    [Xunit.Fact]
    public void PreparedCommit_ReusedWithoutGitLaunches_PreservesClassificationAndIsolation()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        var first = CreateCompletedGoalWorktreeDispatch(
            CreateSeededDispatchRepository(), AgentRole.Developer, string.Empty, string.Empty, clock,
            worktree => CopyPreparedDispatchCommit(worktree, "feature.txt", "feature"));
        var root = CreateSeededDispatchRepository();
        var worktree = GoalWorktrees.Ensure(root, GoalId.New());
        using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
        {
            CopyPreparedDispatchCommit(worktree, "feature.txt", "feature");
            Assert.Equal(0, launches.Count);
        }

        Assert.Equal(ReadGit(first.Process.WorkingDirectory, ["rev-parse", "HEAD"]),
            ReadGit(worktree, ["rev-parse", "HEAD"]));
        Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
        File.AppendAllText(Path.Combine(worktree, "feature.txt"), "private edit");
        Assert.Equal("feature", File.ReadAllText(Path.Combine(first.Process.WorkingDirectory, "feature.txt")));
        Assert.Equal(string.Empty, ReadGit(first.Process.WorkingDirectory, ["status", "--short"]));

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(first.Kernel, first.Goal.Id, first.Task.Id);

        Assert.Equal(WorkTaskStatus.Completed, first.Task.Status);
        Assert.Equal(0, first.Task.LastVerification!.ExitCode);
        Assert.True(first.Task.LastVerification.HasCommittedChanges);
        Assert.Equal(0, first.Task.EmptyOutputRetryCount);
        Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(first.Task.LastVerification));
        AssertExitCode(first.Process.ExitCodePath, 0);
    }

    [Xunit.Fact]
    public void CompletedGoalWorktreeDispatch_NoCommitToken_LaunchesNoHeadProbe()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();

        var (_, _, _, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, string.Empty, string.Empty, clock);

        Assert.Equal(0, launches.Count);
        Assert.Equal(string.Empty, File.ReadAllText(process.StandardOutputPath));
        Assert.Equal(string.Empty, File.ReadAllText(process.StandardErrorPath));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CompletedGoalWorktreeDispatch_RealGitCommit_PreservesClassification(bool includeCommitToken)
    {
        var root = CreateSeededDispatchRepository();
        Assert.True(Directory.Exists(Path.Combine(root, ".git")));
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) dispatch;
        using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
        {
            dispatch = CreateCompletedGoalWorktreeDispatch(
                root,
                AgentRole.Developer,
                includeCommitToken ? WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1") : string.Empty,
                includeCommitToken ? "Committed {commit}" : string.Empty,
                clock,
                worktree =>
                {
                    File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
                    RunGit(worktree, ["add", "-A"], clock.UtcNow.AddMinutes(1));
                    RunGit(worktree, ["commit", "-m", "Feature"], clock.UtcNow.AddMinutes(1));
                });
            // Only the real add and commit launch; populating the commit token adds no probe.
            Assert.Equal(2, launches.Count);
        }

        var (kernel, goal, task, process) = dispatch;
        Assert.True(InfrastructureTestSupport.TryReadGitHeadInProcess(process.WorkingDirectory, out var fullHead));
        Assert.NotNull(fullHead);
        var shortHead = ReadGit(process.WorkingDirectory, ["rev-parse", "--short", "HEAD"]);
        Assert.Equal(shortHead, fullHead[..7]);
        if (includeCommitToken)
        {
            Assert.Contains($"commit: {shortHead}", File.ReadAllText(process.StandardOutputPath), StringComparison.Ordinal);
            Assert.Equal($"Committed {shortHead}", File.ReadAllText(process.StandardErrorPath));
        }

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.True(task.LastVerification.HasCommittedChanges);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
        AssertExitCode(process.ExitCodePath, 0);
    }
}
