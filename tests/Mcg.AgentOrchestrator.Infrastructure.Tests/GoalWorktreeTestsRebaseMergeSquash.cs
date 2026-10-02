using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTestsRebaseMergeSquash : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void ResolvedMergeIsSquashedOntoMain()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = CreateResolvedMerge(repo, goalId);
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD");
            var tree = RunGitOutput(repo, "merge-tree", "--write-tree", mainHead, oldHead);

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal("squashed-merge-commits", result.Detail);
            var newHead = RunGitOutput(path, "rev-parse", "HEAD");
            Assert.NotEqual(oldHead, newHead);
            Assert.Equal($"{newHead} {mainHead}", RunGitOutput(path, "rev-list", "--parents", "-n", "1", "HEAD"));
            Assert.Equal(tree, RunGitOutput(path, "rev-parse", "HEAD^{tree}"));
            Assert.Equal(newHead, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(goalId)}"));
            Assert.False(GitCli.IsWorktreeDirty(path));
            Assert.Equal("resolved edit", File.ReadAllText(Path.Combine(path, "seed.txt")));
            Assert.Equal("later main", File.ReadAllText(Path.Combine(path, "later.txt")));
            Assert.Contains(oldHead, RunGitOutput(path, "log", "-1", "--format=%B"), StringComparison.Ordinal);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void MergeFreeConflictPreservesOriginalHeadAndConflictFile()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            CommitFile(path, "seed.txt", "goal edit");
            CommitFile(repo, "seed.txt", "main edit");
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");
            Assert.Equal("0", RunGitOutput(repo, "rev-list", "--merges", "--count", $"HEAD..{oldHead}"));

            AssertRestoredConflict(repo, goalId, path, oldHead);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void MergeBearingGenuineConflictFallsThroughAndPreservesOriginalHead()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = CreateResolvedMerge(repo, goalId);
            CommitFile(repo, "seed.txt", "main changed again");
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");
            Assert.Equal(1, RunGitExitCode(repo, "merge-tree", "--write-tree", "HEAD", oldHead));

            AssertRestoredConflict(repo, goalId, path, oldHead);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void FastForwardableMergeIsNotRewritten()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = CreateResolvedMerge(repo, goalId, advanceMain: false);
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.AlreadyFastForwardable, result.Status);
            Assert.Null(result.Detail);
            Assert.Equal(oldHead, RunGitOutput(path, "rev-parse", "HEAD"));
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void DirtyMergeBearingWorktreeIsNotRewritten()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = CreateResolvedMerge(repo, goalId);
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");
            File.WriteAllText(Path.Combine(path, "seed.txt"), "uncommitted edit");

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.DirtyWorktree, result.Status);
            Assert.Null(result.Detail);
            Assert.Equal(oldHead, RunGitOutput(path, "rev-parse", "HEAD"));
            Assert.Equal("uncommitted edit", File.ReadAllText(Path.Combine(path, "seed.txt")));
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact]
    public void MainIdenticalMergeTreeStillProducesSingleCommit()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = CreateResolvedMerge(repo, goalId);
            CommitFile(path, "seed.txt", "main edit");
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD");
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");
            var mainTree = RunGitOutput(repo, "rev-parse", "HEAD^{tree}");
            Assert.Equal(mainTree, RunGitOutput(repo, "merge-tree", "--write-tree", mainHead, oldHead));

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal("squashed-merge-commits", result.Detail);
            var newHead = RunGitOutput(path, "rev-parse", "HEAD");
            Assert.Equal($"{newHead} {mainHead}", RunGitOutput(path, "rev-list", "--parents", "-n", "1", "HEAD"));
            Assert.Equal(mainTree, RunGitOutput(path, "rev-parse", "HEAD^{tree}"));
            Assert.False(GitCli.IsWorktreeDirty(path));
        }
        finally { DeleteDirectory(repo); }
    }

    private static string CreateResolvedMerge(string repo, GoalId goalId, bool advanceMain = true)
    {
        RunGit(repo, "config", "core.autocrlf", "false");
        var baseBranch = RunGitOutput(repo, "branch", "--show-current");
        var path = GoalWorktrees.Ensure(repo, goalId);
        CommitFile(path, "seed.txt", "goal edit");
        CommitFile(repo, "seed.txt", "main edit");
        Assert.Equal(1, RunGitExitCode(path, "merge", "--no-edit", baseBranch));
        CommitFile(path, "seed.txt", "resolved edit");
        if (advanceMain) CommitFile(repo, "later.txt", "later main");
        return path;
    }

    private static void CommitFile(string path, string name, string content)
    {
        File.WriteAllText(Path.Combine(path, name), content);
        RunGit(path, "add", name);
        RunGit(path, "commit", "-m", $"Edit {name}");
    }

    private static void AssertRestoredConflict(string repo, GoalId goalId, string path, string oldHead)
    {
        var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);
        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Equal("seed.txt", Assert.Single(result.ConflictFiles));
        Assert.Null(result.Detail);
        Assert.Equal(oldHead, RunGitOutput(path, "rev-parse", "HEAD"));
        Assert.Equal(oldHead, RunGitOutput(repo, "rev-parse", $"refs/heads/{GoalWorktrees.BranchName(goalId)}"));
        Assert.False(GitCli.IsWorktreeDirty(path));
    }
}
