using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateHeadAdvancedLeniencyTests : ChaosGateTestBase
{
    // Leniency: commit is ancestor (HEAD advanced post-commit)
    [Xunit.Fact(DisplayName = "Leniency_HeadAdvancedPostCommit_passes_when_reported_commit_is_ancestor")]
    public void Leniency_HeadAdvancedPostCommit_PassesWhenReportedCommitIsAncestor()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            string.Empty,  // stdout filled in below after we know the worker commit
            string.Empty,
            mutateWorktree: null);

        // Commit a source file (this is the "worker commit")
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        CommitSourceFile(worktree, relPath, "// feature");
        var workerCommit = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);

        // Simulate HEAD advancing past the worker commit (a subsequent merge/commit).
        // Use a .log file so it's excluded from relevant-path checking and doesn't
        // need to appear in the WORKER_RESULT files field.
        File.WriteAllText(Path.Combine(worktree, "post-worker.log"), "advance");
        RunGit(worktree, ["add", "-A"], CommittedAt.AddSeconds(30));
        RunGit(worktree, ["commit", "-m", "Post-worker advance"], CommittedAt.AddSeconds(30));

        // HEAD is now ahead of the worker commit.
        var currentHead = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);
        Assert.False(string.Equals(workerCommit, currentHead, StringComparison.Ordinal));

        // Write stdout containing WORKER_RESULT with the OLD (worker) commit SHA.
        var logs = Path.Combine(root, "logs");
        File.WriteAllText(
            Path.Combine(logs, $"{AgentRole.Developer}.out.log"),
            WorkerResultBlock(relPath, "dotnet build", "Passed", commit: workerCommit));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        // Should PASS: worker commit is reachable from HEAD even though HEAD moved.
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
    }
}
