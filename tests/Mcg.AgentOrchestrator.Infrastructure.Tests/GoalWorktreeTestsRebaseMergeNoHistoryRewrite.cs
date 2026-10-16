using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: each case owns its seeded repository, worktree and any kernel or event log.
public sealed class GoalWorktreeTestsRebaseMergeNoHistoryRewrite : GoalWorktreeTestBase
{
    [Fact]
    public void MergeIntegratedHistory_PreservesEarlierGoalCommitsAndExactTree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var (goalId, worktree, firstGoal, oldHead, mainHead) = CreateMergeHistory(repo);
            var expectedTree = RunGitOutput(repo, "merge-tree", "--write-tree", mainHead, oldHead);

            var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.True(result.UpdatedBranch);
            Assert.Equal("merged-main", result.Detail);
            var newHead = RunGitOutput(repo, "rev-parse", GoalWorktrees.BranchName(goalId));
            Assert.Equal(0, RunGitExitCode(repo, "merge-base", "--is-ancestor", firstGoal, newHead));
            Assert.Equal(0, RunGitExitCode(repo, "merge-base", "--is-ancestor", oldHead, newHead));
            Assert.Equal($"{newHead} {oldHead} {mainHead}", RunGitOutput(worktree, "rev-list", "--parents", "-n", "1", "HEAD"));
            Assert.Equal(expectedTree, RunGitOutput(worktree, "rev-parse", "HEAD^{tree}"));
            Assert.False(GitCli.IsWorktreeDirty(worktree));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void ExistingSquashPath_NegativeControlDropsReviewedHeadFromAncestry()
    {
        var repo = CreateSeededRepository();
        try
        {
            var (goalId, worktree, _, oldHead, _) = CreateMergeHistory(repo);

            var result = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
            Assert.Equal("squashed-merge-commits", result.Detail);
            var newHead = RunGitOutput(worktree, "rev-parse", "HEAD");
            Assert.Equal(1, RunGitExitCode(repo, "merge-base", "--is-ancestor", oldHead, newHead));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void OverlappingConflict_AbortsAndLeavesBranchAndCheckoutUnchanged()
    {
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, "base\n", "main\n", "goal\n")]);

        var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(fixture.Repository, fixture.GoalId);

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Equal(new[] { WidgetPath }, result.ConflictFiles);
        Assert.Null(result.Detail);
        fixture.AssertRestored();
    }

    [Fact]
    public void AlreadyContainsMain_LeavesBranchUnchanged()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var worktree = GoalWorktrees.Ensure(repo, goalId);
            CommitFile(worktree, "goal.txt", "goal", "Goal change");
            var oldHead = RunGitOutput(worktree, "rev-parse", "HEAD");

            var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.AlreadyFastForwardable, result.Status);
            Assert.Equal(oldHead, RunGitOutput(repo, "rev-parse", GoalWorktrees.BranchName(goalId)));
            Assert.False(GitCli.IsWorktreeDirty(worktree));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void DirtyWorktree_PreservesBranchAndUncommittedContent()
    {
        using var fixture = new AdditiveConflictGitFixture();
        File.WriteAllText(Path.Combine(fixture.Worktree, WidgetPath), "uncommitted\n");

        var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(fixture.Repository, fixture.GoalId);

        Assert.Equal(GoalWorktreeRebaseStatus.DirtyWorktree, result.Status);
        Assert.Equal(fixture.OriginalHead, Git(fixture.Repository, "rev-parse", GoalWorktrees.BranchName(fixture.GoalId)).Trim());
        Assert.Equal(fixture.OriginalHead, Git(fixture.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal("uncommitted\n", fixture.Read());
    }

    [Fact]
    public void AdditiveConflict_MergesWithOriginalHeadAsFirstParent()
    {
        var (kernel, goal) = ConductorDriverTests.SimpleGoal();
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var logPath = Path.Combine(fixture.Repository, ".orchestrator", "logs", "conduct-events.log");

        var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(fixture.Repository, goal.Id,
            ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, goal, logPath));

        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
        Assert.Equal("additive-conflict-auto-merge", result.Detail);
        Assert.Equal(MergedText, fixture.Read());
        var head = Git(fixture.Worktree, "rev-parse", "HEAD").Trim();
        Assert.Equal($"{head} {fixture.OriginalHead} {fixture.MainHead}", Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim());
        Assert.Equal(0, RunGitExitCode(fixture.Repository, "merge-base", "--is-ancestor", fixture.OriginalHead, head));
        Assert.False(GitCli.IsWorktreeDirty(fixture.Worktree));
    }

    [Fact]
    public void MissingBranch_ReturnsRecoveryWithoutCreatingBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repo, goalId);
            Assert.Equal(GoalWorktreeRebaseStatus.MissingBranch, result.Status);
            Assert.False(GoalWorktrees.HasBranch(repo, goalId));
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void MissingWorktree_LeavesExistingBranchUnchanged()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var branch = GoalWorktrees.BranchName(goalId);
            var oldHead = RunGitOutput(repo, "rev-parse", "HEAD");
            RunGit(repo, "branch", branch);

            var result = GoalBranchMainMerge.TryMergeMainIntoGoalBranch(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.MissingWorktree, result.Status);
            Assert.Equal(oldHead, RunGitOutput(repo, "rev-parse", branch));
        }
        finally { DeleteDirectory(repo); }
    }

    private static (GoalId GoalId, string Worktree, string FirstGoal, string OldHead, string MainHead) CreateMergeHistory(string repo)
    {
        var goalId = GoalId.New();
        var baseBranch = RunGitOutput(repo, "branch", "--show-current");
        var worktree = GoalWorktrees.Ensure(repo, goalId);
        CommitFile(worktree, "one.txt", "first goal change", "Goal A");
        var firstGoal = RunGitOutput(worktree, "rev-parse", "HEAD");
        CommitFile(repo, "main-a.txt", "first main change", "Main A");
        RunGit(worktree, "merge", "--no-edit", baseBranch);
        CommitFile(worktree, "two.txt", "second goal change", "Goal B");
        var oldHead = RunGitOutput(worktree, "rev-parse", "HEAD");
        CommitFile(repo, "main-b.txt", "later main change", "Main B");
        return (goalId, worktree, firstGoal, oldHead, RunGitOutput(repo, "rev-parse", "HEAD"));
    }

    private static void CommitFile(string directory, string path, string text, string message)
    {
        File.WriteAllText(Path.Combine(directory, path), text);
        RunGit(directory, "add", path);
        RunGit(directory, "commit", "-m", message);
    }
}
