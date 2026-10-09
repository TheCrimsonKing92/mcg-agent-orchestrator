using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real git commands use a unique repository/worktree, with no shared hooks or environment changes.
public sealed class WorkerGitContextConfiguredTrunkTests
{
    [Fact]
    public void MasterOnlyReviewerPreflightsReturnGoalChangeAndTrunkIdentity()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        var worktree = Path.Combine(root, "wt");
        try
        {
            Git(root, "init", "-b", "master");
            Git(root, "config", "user.email", "reviewer-tests@example.invalid");
            Git(root, "config", "user.name", "Reviewer Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed\n");
            Git(root, "add", "seed.txt");
            Git(root, "commit", "-m", "Seed");
            var trunk = Git(root, "rev-parse", "master").Output.Trim();
            Assert.NotEqual(0, GitCli.Run(root, "rev-parse", "--verify", "--quiet", "refs/heads/main").ExitCode);
            Git(root, "worktree", "add", worktree, "-b", $"goal/{Guid.NewGuid():N}", "master");
            File.WriteAllText(Path.Combine(worktree, "change.txt"), "goal change\n");
            Git(worktree, "add", "change.txt");
            Git(worktree, "commit", "-m", "Goal change");

            var context = new WorkerGitContext();
            var scope = context.ReadReviewerChangedFileScope(worktree, "master");
            Assert.Equal(trunk, scope.MergeBase);
            Assert.Equal("change.txt", Assert.Single(scope.ChangedFiles));
            Assert.Equal(1, scope.TotalChangedFileCount);
            var mergeTree = context.ReadReviewerMergeTreeStatus(worktree, "master");
            Assert.True(mergeTree.IsClean);
            Assert.Empty(mergeTree.ConflictPaths);
            Assert.Equal(trunk, WorkerProfileDispatcher.ReadCurrentMainIdentityForRetry(worktree, "master"));

            var missingScope = Assert.Throws<ReviewerChangedFileScopeException>(() =>
                context.ReadReviewerChangedFileScope(worktree, "main"));
            Assert.Equal(WorkerGitContext.ReviewerScopeUnavailableErrorCode, missingScope.ErrorCode);
            var missingMerge = Assert.Throws<ReviewerMergeTreeStatusException>(() =>
                context.ReadReviewerMergeTreeStatus(worktree, "main"));
            Assert.Equal(WorkerGitContext.ReviewerMergeTreeUnavailableErrorCode, missingMerge.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(worktree)) GitCli.Run(root, "worktree", "remove", "--force", worktree);
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static GitCli.GitResult Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, 10_000, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result;
    }
}
