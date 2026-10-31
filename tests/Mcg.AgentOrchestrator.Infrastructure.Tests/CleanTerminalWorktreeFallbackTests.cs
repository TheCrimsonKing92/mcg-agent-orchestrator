using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its real git repository and immutable cleanup hooks.
public sealed class CleanTerminalWorktreeFallbackTests : CliCommandTestBase
{
    [Fact]
    public void CleanCompletedWorktree_RemoveFailureUsesDirectDeletion() => Check("clean", GoalStatus.Completed, true);

    [Fact]
    public void UntrackedFile_RemoveFailureDefers() => Check("untracked", GoalStatus.Completed, false);

    [Fact]
    public void UntrackedFile_HiddenByConfig_RemoveFailureDefers() =>
        Check("untracked", GoalStatus.Completed, false, hideUntracked: true);

    [Fact]
    public void ModifiedTrackedFile_RemoveFailureDefers() => Check("modified", GoalStatus.Completed, false);

    [Fact]
    public void CleanActiveWorktree_RemoveFailureDefers() => Check("clean", GoalStatus.Active, false);

    private void Check(string contents, GoalStatus status, bool complete, bool hideUntracked = false)
    {
        var root = CreateAcceptanceRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            // Cleanup records deferrals in the migrated state store, as it does in a real workspace.
            SeededStateDatabaseTemplate.CopyTo(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goalId = kernel.CreateGoal("Cleanup fallback", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]).Id;
            kernel = WithGoalStatus(kernel, goalId, status);
            var path = CommitGoalWork(root, goalId, "tracked.txt", "original");
            var tip = RunGitOutput(path, "rev-parse", "HEAD").Trim();
            if (contents == "untracked") File.WriteAllText(Path.Combine(path, "real-change.txt"), "keep me");
            if (contents == "modified") File.WriteAllText(Path.Combine(path, "tracked.txt"), "modified");
            if (hideUntracked)
            {
                RunGitOutput(path, "config", "--local", "status.showUntrackedFiles", "no");
                Assert.Empty(RunGitOutput(path, "status", "--porcelain").Trim());
                Assert.Contains("?? real-change.txt", RunGitOutput(path, "status", "--porcelain", "--untracked-files=all"));
            }
            Assert.Contains(path.Replace('\\', '/'), RunGitOutput(root, "worktree", "list", "--porcelain").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            var attempts = 0;
            var hooks = CreateIsolatedCleanupContext(workspace, new GoalWorktreeCleanupHooks
            {
                ResetSandboxAcl = (_, _) => { },
                FindLockHoldersForCleanup = _ => [],
                ProcessCommandLineSnapshot = _ => new ProcessCommandLineSnapshot(new Dictionary<int, string>()),
                RunWorktreeRemove = (_, _, actualPath, force) =>
                {
                    Assert.Equal(path, actualPath);
                    Assert.False(force);
                    attempts++;
                    return new(255, "", "Filename too long");
                }
            }).Hooks;

            var result = status == GoalStatus.Completed
                ? GoalWorktrees.RemoveSupersededTerminal(root, goalId, kernel, tip, true, true, hooks)
                : GoalWorktrees.Remove(root, goalId, kernel, true, true, hooks);

            Assert.Equal(1, attempts);
            Assert.Equal(complete, result.IsComplete);
            Assert.Equal(!complete, Directory.Exists(path));
            var registered = RunGitOutput(root, "worktree", "list", "--porcelain").Replace('\\', '/');
            if (complete)
            {
                Assert.DoesNotContain(path.Replace('\\', '/'), registered, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(0, GitCli.Run(root, "show-ref", "--verify", $"refs/heads/{GoalWorktrees.BranchName(goalId)}").ExitCode);
            }
            else
            {
                Assert.Equal("remove:worktree-remove-failed", result.CleanupBackoff?.Reason);
                Assert.Contains(path.Replace('\\', '/'), registered, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(tip, RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(goalId)).Trim());
                if (contents == "untracked") Assert.Equal("keep me", File.ReadAllText(Path.Combine(path, "real-change.txt")));
                if (contents == "modified") Assert.Equal("modified", File.ReadAllText(Path.Combine(path, "tracked.txt")));
            }
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }
}
