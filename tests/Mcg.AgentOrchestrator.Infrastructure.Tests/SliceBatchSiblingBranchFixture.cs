using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns a unique real repository and uses the existing seeded repository/cleanup contract.
internal sealed class SliceBatchSiblingBranchFixture : GoalWorktreeTestBase, IDisposable
{
    internal string Repository { get; } = CreateSeededRepository();

    internal SliceBatchSiblingBranchFixture()
    {
        RunGit(Repository, "config", "commit.gpgSign", "false");
        RunGit(Repository, "config", "core.autocrlf", "false");
    }

    internal string Commit(GoalId goalId, string path, string contents)
    {
        var worktree = GoalWorktrees.Ensure(Repository, goalId);
        var fullPath = Path.Combine(worktree, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        RunGit(worktree, "add", "-f", "--", path);
        RunGit(worktree, "-c", "commit.gpgSign=false", "commit", "-m", "Stream changes");
        return RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
    }

    internal void AssertContains(GoalId consumerId, string producerTip) =>
        Assert.Equal(0, RunGitExitCode(Repository, "merge-base", "--is-ancestor",
            producerTip, GoalWorktrees.BranchName(consumerId)));

    internal void AssertRestored(GoalId consumerId, string tip)
    {
        var worktree = GoalWorktrees.TryResolve(Repository, consumerId);
        Assert.NotNull(worktree);
        Assert.Equal(tip, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal("", RunGitOutput(worktree, "status", "--porcelain=v1"));
        Assert.Equal(1, RunGitExitCode(worktree, "rev-parse", "--verify", "--quiet", "MERGE_HEAD"));
    }

    public void Dispose() => DeleteDirectory(Repository);
}
