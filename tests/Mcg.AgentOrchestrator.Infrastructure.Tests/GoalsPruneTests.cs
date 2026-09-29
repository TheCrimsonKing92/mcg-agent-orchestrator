using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalsPruneTests : HostCapacityBoundTestBase
{
    private static GoalWorktreeCleanupHooks CreateCleanupHooks(string repo) =>
        WorktreeCleanupContext.Load(
            attentionStoreDirectory: OrchestratorWorkspace.ForDirectory(repo).OrchestratorDirectory,
            buildStorageRoot: new DotnetBuildStorageRoot(Path.Combine(repo, ".orchestrator", "test-dotnet"))).Hooks;

    [Xunit.Fact(DisplayName = "GoalsPrune_classifies_merged_branchless_goal_as_reapable")]
    public void GoalsPruneClassifiesMergedBranchlessGoalAsReapable()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);

            // Create worktree/branch, commit work, merge into main, then remove worktree
            // without going through the acceptance/cancel flow (simulates a "ghost" goal).
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            WriteAndCommit(path, "work.txt", "goal output");
            GoalWorktrees.TryFastForwardMerge(repo, goal.Id);
            RunGit(repo, "worktree", "remove", path);

            var plan = GoalsPrunePlanner.Build(kernel, repo);

            Assert.Equal(1, plan.ReapableCount);
            Assert.True(plan.DryRun);
            Assert.Equal(GoalPruneDisposition.Reapable, plan.Items[0].Disposition);
            Assert.Equal(GoalStatus.Active, plan.Items[0].Status);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact(DisplayName = "GoalsPrune_confirm_cancels_goal_and_removes_merged_branch")]
    public void GoalsPruneConfirmCancelsGoalAndRemovesMergedBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);
            var branch = GoalWorktrees.BranchName(goal.Id);

            var path = GoalWorktrees.Ensure(repo, goal.Id);
            WriteAndCommit(path, "work.txt", "goal output");
            GoalWorktrees.TryFastForwardMerge(repo, goal.Id);
            RunGit(repo, "worktree", "remove", path);

            var cleanupHooks = CreateCleanupHooks(repo);
            var ownedArtifacts = DotnetBuildEnvironmentManager.GoalRoot(goal.Id, cleanupHooks.BuildStorageRoot);
            Directory.CreateDirectory(ownedArtifacts);
            File.WriteAllText(Path.Combine(ownedArtifacts, "owned-marker.txt"), "prune must remove its owned artifacts");
            var foreignRoot = new DotnetBuildStorageRoot(Path.Combine(repo, ".orchestrator", "foreign-dotnet"));
            var foreignArtifacts = DotnetBuildEnvironmentManager.GoalRoot(goal.Id, foreignRoot);
            Directory.CreateDirectory(foreignArtifacts);
            var foreignMarker = Path.Combine(foreignArtifacts, "foreign-marker.txt");
            File.WriteAllText(foreignMarker, "preserve this independent owner");

            var applied = GoalsPrunePlanner.Apply(kernel, repo, cleanupHooks);

            Assert.False(applied.DryRun);
            Assert.Equal(1, applied.PrunedCount);
            Assert.Equal(GoalPruneDisposition.Pruned, applied.Items[0].Disposition);
            Assert.Equal(GoalStatus.Cancelled, goal.Status);
            Assert.False(BranchExists(repo, branch));
            Assert.False(Directory.Exists(ownedArtifacts));
            Assert.Equal("preserve this independent owner", File.ReadAllText(foreignMarker));
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact(DisplayName = "GoalsPrune_skips_goal_with_unmerged_branch")]
    public void GoalsPruneSkipsGoalWithUnmergedBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);

            // Commit work on goal branch but do NOT merge into main.
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            WriteAndCommit(path, "unmerged.txt", "not merged yet");
            RunGit(repo, "worktree", "remove", path);

            var plan = GoalsPrunePlanner.Build(kernel, repo);

            Assert.Equal(0, plan.ReapableCount);
            Assert.Equal(GoalPruneDisposition.SkippedUnmergedBranch, plan.Items[0].Disposition);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact(DisplayName = "GoalsPrune_skips_goal_with_live_worktree")]
    public void GoalsPruneSkipsGoalWithLiveWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);

            // Merge the branch but leave the worktree registered.
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            WriteAndCommit(path, "work.txt", "done");
            GoalWorktrees.TryFastForwardMerge(repo, goal.Id);
            // Worktree still alive — must not prune.

            var plan = GoalsPrunePlanner.Build(kernel, repo);

            Assert.Equal(0, plan.ReapableCount);
            Assert.Equal(GoalPruneDisposition.SkippedLiveWorktree, plan.Items[0].Disposition);

            // Cleanup the worktree so the temp directory can be deleted.
            GoalWorktrees.Remove(repo, goal.Id, hooks: CreateCleanupHooks(repo));
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact(DisplayName = "GoalsPrune_dry_run_does_not_cancel_goal_or_delete_branch")]
    public void GoalsPruneDryRunDoesNotCancelGoalOrDeleteBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);
            var branch = GoalWorktrees.BranchName(goal.Id);

            var path = GoalWorktrees.Ensure(repo, goal.Id);
            WriteAndCommit(path, "work.txt", "done");
            GoalWorktrees.TryFastForwardMerge(repo, goal.Id);
            RunGit(repo, "worktree", "remove", path);

            var plan = GoalsPrunePlanner.Build(kernel, repo);

            // Dry-run: goal is identified as reapable but not touched.
            Assert.Equal(1, plan.ReapableCount);
            Assert.True(plan.DryRun);
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.True(BranchExists(repo, branch));
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact(DisplayName = "GoalsPrune_is_idempotent_second_apply_finds_nothing")]
    public void GoalsPruneIsIdempotentSecondApplyFindsNothing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);

            var path = GoalWorktrees.Ensure(repo, goal.Id);
            WriteAndCommit(path, "work.txt", "done");
            GoalWorktrees.TryFastForwardMerge(repo, goal.Id);
            RunGit(repo, "worktree", "remove", path);

            var first = GoalsPrunePlanner.Apply(kernel, repo, CreateCleanupHooks(repo));
            Assert.Equal(1, first.PrunedCount);

            // Second apply: goal is already Cancelled — nothing left to reap.
            var second = GoalsPrunePlanner.Apply(kernel, repo, CreateCleanupHooks(repo));
            Assert.Equal(0, second.PrunedCount);
            Assert.Equal(GoalPruneDisposition.SkippedTerminal, second.Items[0].Disposition);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Fact(DisplayName = "GoalsPrune_skips_already_terminal_goals")]
    public void GoalsPruneSkipsAlreadyTerminalGoals()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateActiveGoal(kernel);
            kernel.CancelGoal(goal.Id, "already done manually");

            var plan = GoalsPrunePlanner.Build(kernel, repo);

            Assert.Equal(0, plan.ReapableCount);
            Assert.Equal(GoalPruneDisposition.SkippedTerminal, plan.Items[0].Disposition);
        }
        finally { DeleteDirectory(repo); }
    }

    private static Goal CreateActiveGoal(AgentOrchestratorKernel kernel)
    {
        var goal = kernel.CreateGoal("Test goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return goal;
    }

    private static void WriteAndCommit(string worktreePath, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(worktreePath, fileName), content);
        RunGit(worktreePath, "add", "-A");
        RunGit(worktreePath, "commit", "-m", $"Add {fileName}");
    }

    private static string CreateSeededRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-prune-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "Prune Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private static bool BranchExists(string workingDirectory, string branch) =>
        RunGitExitCode(workingDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}") == 0;

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var exitCode = RunGitExitCode(workingDirectory, arguments, out var error);
        if (exitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private static int RunGitExitCode(string workingDirectory, params string[] arguments) =>
        RunGitExitCode(workingDirectory, arguments, out _);

    private static int RunGitExitCode(string workingDirectory, string[] arguments, out string error)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        error = result.StandardError;
        return result.ExitCode ?? throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit null: {error}; {result}");
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
