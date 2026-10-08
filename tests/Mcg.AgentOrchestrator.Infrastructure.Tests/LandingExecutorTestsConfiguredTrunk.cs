using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static LandingExecutorTests;

[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsConfiguredTrunk
{
    [Theory]
    [InlineData("master", true)]
    [InlineData("main", false)]
    public void Execute_ConfiguredOrDefaultTrunk_AdvancesOnlyThatRef(string trunk, bool configured)
    {
        var repo = CreateGitRepository();
        try
        {
            if (configured) ReadGit(repo, "branch", "-M", trunk);
            var workspace = configured
                ? OrchestratorWorkspace.ForProject("alpha", repo, integrationBranch: trunk)
                : OrchestratorWorkspace.ForDirectory(repo);
            Assert.Equal(trunk, workspace.IntegrationBranch);
            var before = ReadGit(repo, "rev-parse", $"refs/heads/{trunk}");
            if (configured) AssertMissingMain(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var branch = GoalWorktrees.BranchName(goal.Id);
            ReadGit(repo, "checkout", "-b", branch);
            AppendCommit(repo, "src/configured-trunk.txt", "landed content");
            var candidate = ReadGit(repo, "rev-parse", "HEAD");
            ReadGit(repo, "checkout", trunk);
            GoalOperationJournal.AcceptancePassed(repo, goal, "conductor:acceptance",
                candidate, before, "passed candidate", DateTimeOffset.UtcNow.AddMinutes(-1));

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            Assert.NotEqual(before, result.MergeCommitSha);
            Assert.Equal(result.MergeCommitSha, ReadGit(repo, "rev-parse", $"refs/heads/{trunk}"));
            Assert.Equal("landed content", File.ReadAllText(Path.Combine(repo, "src", "configured-trunk.txt")).Trim());
            if (configured) AssertMissingMain(repo);

            GoalOperationJournal.Completed(repo, goal, "conductor:land", result.Message);
            var repeated = LandingExecutor.Execute(kernel, goal, workspace);
            Assert.False(repeated.MainAdvanced);
            Assert.Equal(result.MergeCommitSha, ReadGit(repo, "rev-parse", trunk));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Worktrees_DetachedMasterRoot_UsesConfiguredFallbackAndDiff()
    {
        var repo = CreateGitRepository();
        try
        {
            ReadGit(repo, "branch", "-M", "master");
            AssertMissingMain(repo);
            var (_, goal) = CreateVerifiedGoal(repo);
            var worktree = GoalWorktrees.Ensure(repo, goal.Id, trunkBranch: "master");
            AppendCommit(repo, "base.txt", "advanced trunk");
            var master = ReadGit(repo, "rev-parse", "master");
            ReadGit(repo, "checkout", "--detach", "master");

            Assert.Equal(worktree, GoalWorktrees.Ensure(repo, goal.Id, trunkBranch: "master"));
            Assert.Equal(master, ReadGit(worktree, "rev-parse", "HEAD"));
            AppendCommit(worktree, "goal.txt", "goal change");
            Assert.True(GoalWorktrees.HasChangesAgainstMain(repo, goal.Id, "master"));
            Assert.Contains("goal change", GoalWorktrees.TryGetBranchDiff(repo, goal.Id, "master"));

            ReadGit(repo, "checkout", "master");
            AppendCommit(repo, "later.txt", "later trunk change");
            master = ReadGit(repo, "rev-parse", "master");
            ReadGit(repo, "checkout", "--detach", "master");
            var rebased = GoalWorktrees.TryRebaseOntoMain(repo, goal.Id, trunkBranch: "master");
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, rebased.Status);
            Assert.Equal(master, ReadGit(worktree, "rev-parse", "HEAD^"));
            AssertMissingMain(repo);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    private static void AssertMissingMain(string repo) =>
        Assert.NotEqual(0, GitCli.Run(repo, "rev-parse", "--verify", "--quiet", "refs/heads/main").ExitCode);
}
