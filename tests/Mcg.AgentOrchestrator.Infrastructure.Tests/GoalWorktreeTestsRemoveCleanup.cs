using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalWorktreeTestsRemoveCleanup : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "GoalWorktrees_cleanup_contexts_are_isolated_through_private_helpers")]
    public async Task GoalWorktreesCleanupContextsAreIsolatedThroughPrivateHelpers()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "mcg-cleanup-context-a-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "mcg-cleanup-context-b-" + Guid.NewGuid().ToString("N"));
        var firstGoal = GoalId.New();
        var secondGoal = GoalId.New();
        var firstPath = Path.Combine(firstRoot, ".orchestrator-context", firstGoal.Value);
        var secondPath = Path.Combine(secondRoot, ".orchestrator-context", secondGoal.Value);
        var firstInvocations = new ConcurrentBag<string>();
        var secondInvocations = new ConcurrentBag<string>();
        using var rendezvous = new Barrier(2);

        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        try
        {
            GoalWorktreeDeleteResult DeleteWithRendezvous(string path, ConcurrentBag<string> collector)
            {
                if (!rendezvous.SignalAndWait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("Concurrent cleanup hook rendezvous was not reached.");
                }

                collector.Add(path);
                return GoalWorktreeDeleteResult.Success;
            }

            var firstHooks = new GoalWorktreeCleanupHooks
            {
                DeleteDirectoryForCleanup = path => DeleteWithRendezvous(path, firstInvocations),
                CleanupWarningSink = _ => { }
            };
            var secondHooks = new GoalWorktreeCleanupHooks
            {
                DeleteDirectoryForCleanup = path => DeleteWithRendezvous(path, secondInvocations),
                CleanupWarningSink = _ => { }
            };

            var firstCleanup = Task.Run(() => GoalWorktrees.SweepOwnedEphemeralDirectories(
                firstRoot,
                firstGoal,
                hooks: firstHooks));
            var secondCleanup = Task.Run(() => GoalWorktrees.SweepOwnedEphemeralDirectories(
                secondRoot,
                secondGoal,
                hooks: secondHooks));

            await Task.WhenAll(firstCleanup, secondCleanup).WaitAsync(TimeSpan.FromSeconds(30));

            Xunit.Assert.Equal([firstPath], firstInvocations);
            Xunit.Assert.Equal([secondPath], secondInvocations);
            Xunit.Assert.DoesNotContain(secondPath, firstInvocations);
            Xunit.Assert.DoesNotContain(firstPath, secondInvocations);
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryWithRetry(firstRoot);
            GoalWorktrees.DeleteDirectoryWithRetry(secondRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_terminal_remove_deletes_long_path_and_prunes_registration")]
    public void GoalWorktreesTerminalRemoveDeletesLongPathAndPrunesRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scratchRoot = Path.Combine(FindCurrentSourceRoot(), ".scratch", "mcg-long-wt", Guid.NewGuid().ToString("n"));
        var repo = Path.Combine(scratchRoot, "repo");
        var originalWorktreeRemove = GoalWorktrees.RunWorktreeRemove;
        var originalWorktreePrune = GoalWorktrees.RunWorktreePrune;

        try
        {
            Directory.CreateDirectory(repo);
            RunGit(repo, "init");
            RunGit(repo, "config", "user.email", "tests@example.com");
            RunGit(repo, "config", "user.name", "Worktree Tests");
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Seed");

            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Exercise long-path cleanup.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Long-path cleanup", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var deepPath = Path.Combine(worktree, "long-path-segment-0123456789");
            while (deepPath.Length <= 260)
            {
                deepPath = Path.Combine(deepPath, "long-path-segment-0123456789");
            }

            var extendedDeepPath = @"\\?\" + deepPath;
            Directory.CreateDirectory(extendedDeepPath);
            Assert.True(Directory.Exists(extendedDeepPath));
            kernel.CancelGoal(goal.Id, "Test terminal cleanup.");
            string? removalPath = null;
            var pruneCalls = 0;
            GoalWorktrees.RunWorktreeRemove = (_, _, path, force) =>
            {
                removalPath = path;
                Assert.True(force);
                return new GitCli.GitResult(1, string.Empty, "simulated git long-path refusal");
            };
            GoalWorktrees.RunWorktreePrune = (executionDirectory, timeout, expireNow) =>
            {
                pruneCalls++;
                Assert.True(expireNow);
                return GitCli.Run(executionDirectory, timeout, "worktree", "prune", "--expire", "now");
            };

            var result = GoalWorktrees.RemoveTerminal(repo, goal.Id, kernel);

            Assert.True(result.IsComplete, result.Message);
            Assert.True(removalPath?.StartsWith(@"\\?\", StringComparison.Ordinal) is true, removalPath);
            Assert.Equal(1, pruneCalls);
            Assert.False(Directory.Exists(worktree));
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.DoesNotContain(
                NormalizePath(worktree),
                RunGitOutput(repo, "worktree", "list", "--porcelain"),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            GoalWorktrees.RunWorktreeRemove = originalWorktreeRemove;
            GoalWorktrees.RunWorktreePrune = originalWorktreePrune;
            _ = GoalWorktrees.DeleteDirectory(scratchRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_terminal_remove_reports_unsafe_prefix_collision_without_throwing")]
    public void GoalWorktreesTerminalRemoveReportsUnsafePrefixCollisionWithoutThrowing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var terminalId = new GoalId("12345678aaaaaaaaaaaaaaaaaaaaaaaa");
            var activeId = new GoalId("12345678bbbbbbbbbbbbbbbbbbbbbbbb");
            var terminal = kernel.CreateGoal(
                terminalId,
                "Terminal collision",
                [new TaskSpec(TaskId.New(), "Terminal work", AgentRole.Developer)]);
            var active = kernel.CreateGoal(
                activeId,
                "Active collision",
                [new TaskSpec(TaskId.New(), "Active work", AgentRole.Developer)]);
            kernel.ActivateGoal(terminal.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, terminal.Id);
            kernel.CancelGoal(terminal.Id, "Terminal collision test.");

            var result = GoalWorktrees.RemoveTerminal(repo, terminal.Id, kernel);

            Assert.False(result.IsComplete);
            Assert.Contains("refused unsafe target", result.Message, StringComparison.Ordinal);
            Assert.Contains($"non-terminal goal {active.Id.Value[..8]} (Active)", result.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_force_terminal_cleanup_bypasses_escalated_backoff")]
    public void CliWorkspaceRemoveForceTerminalCleanupBypassesEscalatedBackoff()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        var originalOptions = GoalWorktrees.CleanupOptions;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Forced terminal cleanup",
                [new TaskSpec(TaskId.New(), "Leave terminal residue.", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            kernel.CancelGoal(goal.Id, "Force cleanup test.");
            GoalWorktrees.DeleteDirectory = _ => false;
            GoalWorktrees.SandboxAclHelper = new NoOpSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            GoalWorktrees.FindLockHoldersForCleanup = _ => [];
            GoalWorktrees.ConfigureCleanup(
                new GoalWorktreeCleanupOptions(TimeSpan.FromMinutes(5), 1, TimeSpan.FromDays(1)),
                Path.Combine(repo, ".orchestrator"));

            var deferred = GoalWorktrees.RemoveTerminal(repo, goal.Id, kernel);
            Assert.False(deferred.IsComplete);
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
            var attentionStore = CollaborationItemStore.ForDirectory(Path.Combine(repo, ".orchestrator"));
            var attention = Assert.Single(attentionStore.GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Contains(
                $"workspace remove {goal.Id.Value[..8]} --force-terminal-cleanup",
                attention.Body,
                StringComparison.Ordinal);

            GoalWorktrees.DeleteDirectory = originalDelete;
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal);

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["workspace", "remove", goal.Id.Value[..8], "--force-terminal-cleanup"],
                context));

            Assert.False(Directory.Exists(path));
            Assert.Null(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
            Assert.Empty(attentionStore.GetAttentionQueueAsync().GetAwaiter().GetResult());
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            GoalWorktrees.ConfigureCleanup(originalOptions);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAbandon_removes_terminal_worktree_in_one_cleanup_cycle")]
    public void GoalAbandonRemovesTerminalWorktreeInOneCleanupCycle()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Abandon clean worktree.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Abandon cleanup", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            _ = GoalWorktrees.Ensure(repo, goal.Id);

            var result = GoalAbandonPlanner.Apply(
                kernel,
                goal,
                OrchestratorWorkspace.ForDirectory(repo),
                "No longer required.");

            Assert.True(result.CanApply);
            Assert.Equal(GoalStatus.Cancelled, kernel.GetGoal(goal.Id).Status);
            var worktreesRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
            Assert.Empty(Directory.Exists(worktreesRoot)
                ? Directory.EnumerateDirectories(worktreesRoot)
                : []);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory")]
    public void GoalWorktreesRemoveResumesAfterUnregisteredWorktreeLeavesDirectory()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);
            File.WriteAllText(Path.Combine(path, "leftover.log"), "held by prior test process");

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            Assert.True(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.True(BranchExists(repo, branch));

            var removeResult = GoalWorktrees.Remove(repo, goalId);
            Assert.Equal("Removed workspace and merged branch " + branch + ".", removeResult.Message);
            Assert.True(removeResult.IsComplete);

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_rechecks_branch_ancestry_before_delete")]
    public void GoalWorktreesRemoveRechecksBranchAncestryBeforeDelete()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);
            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");
            RunGit(repo, "merge", "--ff-only", branch);
            RunGit(repo, "reset", "--hard", "HEAD~1");

            var removeResult = GoalWorktrees.Remove(repo, goalId);

            Assert.False(removeResult.IsComplete);
            Assert.Contains("kept because it has unmerged commits at deletion time", removeResult.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void RemoveSupersededTerminal_ChangedTip_KeepsBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Changed superseded branch", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", repo, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel.CompleteGoal(goal.Id, "Completed before superseded cleanup.");
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            var branch = GoalWorktrees.BranchName(goal.Id);
            File.WriteAllText(Path.Combine(path, "first.txt"), "first");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "First commit");
            var checkedTip = RunGitOutput(path, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(path, "second.txt"), "second");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Changed after equivalence check");
            var changedTip = RunGitOutput(path, "rev-parse", "HEAD").Trim();

            var result = GoalWorktrees.RemoveSupersededTerminal(
                repo,
                goal.Id,
                kernel,
                checkedTip,
                hasRegisteredWorktree: true,
                hasBranch: true);

            Assert.False(result.IsComplete);
            Assert.Contains("branch deletion failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(path));
            Assert.True(BranchExists(repo, branch));
            Assert.Equal(changedTip, RunGitOutput(repo, "rev-parse", branch).Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_deletes_receipt_only_dirty_worktree")]
    public void GoalWorktreesRemoveDeletesReceiptOnlyDirtyWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.WriteAllText(Path.Combine(path, WorkerSandboxPreparer.ReceiptFileName), "{}");

            Assert.Equal($"?? {WorkerSandboxPreparer.ReceiptFileName}", RunGitOutput(path, "status", "--short").Trim());

            var removeResult = GoalWorktrees.Remove(repo, goalId);

            Assert.True(removeResult.IsComplete);
            Assert.False(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:worktree-remove-failed"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_failure_records_cleanup_needed_without_throwing")]
    public void GoalWorktreesRemoveFailureRecordsCleanupNeededWithoutThrowing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.WriteAllText(Path.Combine(path, "real-change.txt"), "not orchestrator-owned");

            var removeResult = GoalWorktrees.Remove(repo, goalId);

            Assert.False(removeResult.IsComplete);
            Assert.Equal(path, removeResult.LeftoverPath);
            Assert.Equal("remove:worktree-remove-failed", removeResult.CleanupBackoff?.Reason);
            Assert.True(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is not null);
            Assert.True(HasCleanupNeededRecord(repo, path, "remove:worktree-remove-failed"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_aborts_branch_delete_when_branch_unmerged_at_deletion_time")]
    public void GoalWorktreesRemoveAbortsBranchDeleteWhenBranchUnmergedAtDeletionTime()
    {
        var repo = CreateSeededRepository();
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var warnings = new List<GoalWorktreeCleanupWarning>();
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);
            File.WriteAllText(Path.Combine(path, "unmerged.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Unmerged goal work");

            DeleteDirectory(path);
            RunGit(repo, "worktree", "prune");

            Assert.False(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.True(BranchExists(repo, branch));

            var first = GoalWorktrees.Remove(repo, goalId);

            Assert.False(first.IsComplete);
            Assert.Equal(path, first.LeftoverPath);
            Assert.Null(first.CleanupBackoff);
            Assert.Contains("kept because it has unmerged commits at deletion time", first.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(BranchExists(repo, branch));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:branch-delete-failed"));

            var second = GoalWorktrees.Remove(repo, goalId);

            Assert.False(second.IsComplete);
            Assert.Equal(path, second.LeftoverPath);
            Assert.Null(second.CleanupBackoff);
            Assert.Contains("kept because it has unmerged commits at deletion time", second.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(BranchExists(repo, branch));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:branch-delete-failed"));
            Assert.Empty(warnings);

            RunGit(repo, "branch", "-D", branch);
            var final = GoalWorktrees.Remove(repo, goalId);

            Assert.True(final.IsComplete);
            Assert.False(BranchExists(repo, branch));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:branch-delete-failed"));
        }
        finally
        {
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released")]
    public void GoalWorktreesRemoveReportsLeftoverPathAndResumesWhenLockReleased()
    {
        var repo = CreateSeededRepository();
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            // Deregister the worktree manually to isolate directory-deletion behavior.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "leftover.log");
            File.WriteAllText(lockedFile, "held open");
            var lockHolderProbes = 0;
            GoalWorktrees.FindLockHoldersForCleanup = _ => lockHolderProbes++ == 0
                ? [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "held open")]
                : [];

            // Hold the file open exclusively so Directory.Delete fails.
            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows holds the file exclusively, so the registered worktree and branch are
                // cleaned up while the leftover directory is reported as resumable cleanup.
                Assert.False(partial.IsComplete);
                Assert.Equal(path, partial.LeftoverPath);
                Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", partial.ResumeCommand);
                Assert.True(partial.Message.Contains("leftover directory cleanup is incomplete", StringComparison.OrdinalIgnoreCase));
                Assert.True(Directory.Exists(path));
                Assert.True(HasCleanupNeededRecord(repo, path, "remove:leftover-directory:lock-held"));

                // Lock released; resume call deletes the directory and cleans up the branch.
                var final = GoalWorktrees.Remove(repo, goalId);
                Assert.True(final.IsComplete);
                Assert.False(HasCleanupNeededRecord(repo, path, "remove:leftover-directory:lock-held"));
            }
            else
            {
                // POSIX allows unlinking files with open handles, so removal completes immediately.
                Assert.True(partial.IsComplete);
            }

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_is_idempotent_when_already_clean")]
    public void GoalWorktreesRemoveIsIdempotentWhenAlreadyClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            GoalWorktrees.Ensure(repo, goalId);

            var first = GoalWorktrees.Remove(repo, goalId);
            Assert.True(first.IsComplete);

            // Second call with nothing left must return success, not throw.
            var second = GoalWorktrees.Remove(repo, goalId);
            Assert.True(second.IsComplete);
            Assert.True(second.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_persists_cleanup_needed_when_goal_artifacts_delete_fails")]
    public void GoalWorktreesRemovePersistsCleanupNeededWhenGoalArtifactsDeleteFails()
    {
        var repo = CreateSeededRepository();
        var originalIsolatedRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        var isolatedRoot = Path.Combine(repo, "isolated-dotnet");
        try
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, isolatedRoot);
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var buildEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "cleanup-needed");
            var lockedFile = Path.Combine(buildEnvironment.RootPath, "held-open.log");
            File.WriteAllText(lockedFile, "held");
            var lockReleased = false;
            GoalWorktrees.FindLockHoldersForCleanup = _ => lockReleased
                ? []
                : [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "held artifact root")];

            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }
            lockReleased = true;

            if (OperatingSystem.IsWindows())
            {
                Assert.False(partial.IsComplete);
                Assert.Equal(buildEnvironment.RootPath, partial.LeftoverPath);
                Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", partial.ResumeCommand);
                Assert.False(Directory.Exists(path));
                Assert.True(Directory.Exists(buildEnvironment.RootPath));
                Assert.True(HasCleanupNeededRecord(repo, buildEnvironment.RootPath, "remove:goal-artifacts:lock-held"));

                var retry = GoalWorktrees.Remove(repo, goalId);

                Assert.True(retry.IsComplete);
                Assert.False(Directory.Exists(buildEnvironment.RootPath));
                Assert.False(HasCleanupNeededRecord(repo, buildEnvironment.RootPath, "remove:goal-artifacts:lock-held"));
            }
            else
            {
                Assert.True(partial.IsComplete);
                Assert.False(Directory.Exists(buildEnvironment.RootPath));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, originalIsolatedRoot);
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases")]
    public void GoalWorktreesRemoveRetriesAndSucceedsWhenTransientLockReleases()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            // Simulate the half-removed state: worktree already unregistered, directory lingers.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "transient-hold.log");
            File.WriteAllText(lockedFile, "held");

            GoalWorktreeRemoveResult result;
            if (OperatingSystem.IsWindows())
            {
                // Hold the file exclusively then release it partway through the retry window so
                // that a single Remove() call succeeds without requiring a second invocation.
                var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
                _ = Task.Delay(150).ContinueWith(_ => fs.Dispose());

                result = GoalWorktrees.Remove(repo, goalId);

                Assert.True(result.IsComplete);
            }
            else
            {
                // POSIX: open handles do not prevent deletion, so removal completes immediately.
                result = GoalWorktrees.Remove(repo, goalId);
                Assert.True(result.IsComplete);
            }

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_partial_result_identifies_branch_state")]
    public void GoalWorktreesRemovePartialResultIdentifiesBranchState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "hold.txt");
            File.WriteAllText(lockedFile, "lock");

            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }

            if (OperatingSystem.IsWindows())
            {
                Assert.False(partial.IsComplete);
                Assert.True(partial.Message.Contains(branch, StringComparison.Ordinal));
                Assert.True(partial.Message.Contains("leftover directory cleanup is incomplete", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(path, partial.LeftoverPath);
                Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", partial.ResumeCommand);
            }
            else
            {
                // POSIX: the held handle does not block removal, so it completes.
                Assert.True(partial.IsComplete);
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_command_creates_and_removes_goal_worktree")]
    public void CliWorkspaceCommandCreatesAndRemovesGoalWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            CliCommandDispatcher.ExecuteCommand(["workspace", "create"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            var path = GoalWorktrees.TryResolve(repo, goal.Id);
            Assert.True(path is not null);
            Assert.Equal(path, workspace.ResolveExecutionDirectory(goal.Id));

            CliCommandDispatcher.ExecuteCommand(["workspace", "remove"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.Equal(workspace.ExecutionDirectory, workspace.ResolveExecutionDirectory(goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_repairs_landed_cleaned_stale_acceptance_failure")]
    public void CliWorkspaceRemoveRepairsLandedCleanedStaleAcceptanceFailure()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove recovery", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["acceptance evidence blocked"]);
            GoalOperationJournal.Completed(repo, goal, "acceptance", "Acceptance passed and merge completed.");
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var order = new List<string>();
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                EventWriter = new RecordingGoalLifecycleEventWriter(order)
            };

            CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.Null(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.Contains("remove-worktree", order);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove" &&
                entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_persists_provider_session_retirement")]
    public async Task CliWorkspaceRemovePersistsProviderSessionRetirement()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Workspace remove session retirement", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "session-retirement.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Session retirement goal");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                worktreePath,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                ProviderSessionId: "workspace-session",
                WorktreeHeadSha: "abc123",
                DirtyStateHash: "dirty-hash"));
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            GoalOperationJournal.Completed(repo, goal, "acceptance", "Acceptance passed and merge completed.");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var changed = false;
            CaptureConsole(() =>
            {
                changed = CliPersistentStateRunner.ExecuteCommand(
                    ["workspace", "remove", goal.Id.Value[..8]],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            Assert.True(changed);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            var reloadedTask = (await stateRepository.LoadAsync()).GetTask(goal.Id, task.Id);
            Assert.Equal("workspace-session", reloadedTask.LastDispatch!.ProviderSessionId);
            Assert.NotNull(reloadedTask.LastDispatch.ProviderSessionRetiredAt);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_keeps_stale_acceptance_failure_without_landing_evidence")]
    public void CliWorkspaceRemoveKeepsStaleAcceptanceFailureWithoutLandingEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove no landing recovery", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["acceptance evidence blocked"]);
            _ = GoalWorktrees.Ensure(repo, goal.Id);
            var context = CreateAcceptanceContext(kernel, repo, goal);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.DoesNotContain("Acceptance repaired:", output);
            Assert.NotNull(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove" &&
                entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_does_not_complete_verified_goal_without_landing_evidence")]
    public void CliWorkspaceRemoveDoesNotCompleteVerifiedGoalWithoutLandingEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove no terminal evidence", repo);
            var order = new List<string>();
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                EventWriter = new RecordingGoalLifecycleEventWriter(order)
            };

            CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.DoesNotContain("remove-worktree", order);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove" &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.DoesNotContain(journal.LatestByOperation, entry =>
                entry.Operation == "acceptance" &&
                entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_prints_cleanup_backoff_skip_until")]
    public void CliWorkspaceRemovePrintsCleanupBackoffSkipUntil()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove backoff", repo);
            var path = Path.Combine(repo, GoalWorktrees.DirectoryName, goal.Id.Value[..8]);
            var skipUntil = DateTimeOffset.Parse("2026-07-02T05:01:00Z");
            var worktrees = new CapturingGoalWorktreeService
            {
                RemoveOverride = (_, _, _, _) => new GoalWorktreeRemoveResult(
                    "Workspace cleanup deferred by cleanup-needed backoff.",
                    path,
                    [],
                    $"conduct {goal.Id.Value[..8]} --loop",
                    CleanupBackoff: new GoalWorktreeCleanupBackoff(
                        "remove:cleanup-budget-exhausted",
                        skipUntil,
                        TimeSpan.FromMinutes(1)))
            };
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                Worktrees = worktrees
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.Contains("Cleanup backoff:", output);
            Assert.Contains("reason=remove:cleanup-budget-exhausted", output);
            Assert.Contains("skip_until_utc=2026-07-02T05:01:00.0000000+00:00", output);
            Assert.Contains("remaining_wait=00:01:00", output);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_prints_cleanup_backoff_skip_until")]
    public void CliGoalRecoveryPrintsCleanupBackoffSkipUntil()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover cleanup backoff", [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            GoalWorktrees.DeleteDirectory = _ => false;
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            GoalWorktrees.FindLockHoldersForCleanup = _ => [];
            _ = GoalWorktrees.Remove(repo, goal.Id);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.Contains("Cleanup backoff:", output);
            Assert.Contains("reason=remove:leftover-directory", output);
            Assert.Contains("skip_until_utc=2026-07-02T05:10:00.0000000+00:00", output);
            Assert.Contains("remaining_wait=00:10:00", output);
            Assert.Contains($"workspace remove {goal.Id.Value[..8]}", output);
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_can_target_non_current_goal")]
    public void CliWorkspaceRemoveCanTargetNonCurrentGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var olderGoal = kernel.CreateGoal("Older workspace goal", [new TaskSpec(TaskId.New(), "Do older work", AgentRole.Developer)]);
            var latestGoal = kernel.CreateGoal("Latest workspace goal", [new TaskSpec(TaskId.New(), "Do latest work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = latestGoal;
            var olderPath = GoalWorktrees.Ensure(repo, olderGoal.Id);
            var latestPath = GoalWorktrees.Ensure(repo, latestGoal.Id);
            var olderGoalPrefix = olderGoal.Id.Value[..8];

            CliCommandDispatcher.ExecuteCommand(["workspace", "remove", olderGoalPrefix], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            Assert.True(GoalWorktrees.TryResolve(repo, olderGoal.Id) is null);
            Assert.False(Directory.Exists(olderPath));
            Assert.Equal(latestPath, GoalWorktrees.TryResolve(repo, latestGoal.Id));
            Assert.True(Directory.Exists(latestPath));
            Assert.Equal(olderGoal.Id, currentGoal!.Id);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_safe_auto_blocks_cleanup")]
    public void CliWorkspaceRemoveSafeAutoBlocksCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace cleanup policy", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            var path = GoalWorktrees.Ensure(repo, goal.Id);

            var ex = Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["workspace", "remove", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(ex.Message.Contains("policy 'safe-auto' blocks workspace remove", StringComparison.Ordinal));
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked workspace remove", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_repair_clears_stale_failure_only_with_landing_and_cleanup_evidence")]
    public void CliAcceptanceRepairClearsStaleFailureOnlyWithLandingAndCleanupEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var blockedGoal = CreateCompletedGoal(kernel, "Repair blocked goal", repo);
            kernel.RecordAcceptanceFailure(blockedGoal.Id, ["acceptance evidence blocked"]);
            var blockedContext = CreateAcceptanceContext(kernel, repo, blockedGoal);

            var blocked = Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-repair", blockedGoal.Id.Value[..8], "--confirm-acceptance-repair"],
                blockedContext));
            Assert.Contains("no completed acceptance or conductor landing evidence", blocked.Message);
            Assert.NotNull(kernel.GetGoal(blockedGoal.Id).LatestAcceptanceFailure);

            var repairGoal = CreateCompletedGoal(kernel, "Repair landed cleaned goal", repo);
            kernel.RecordAcceptanceFailure(repairGoal.Id, ["acceptance evidence blocked"]);
            GoalOperationJournal.Completed(repo, repairGoal, "conductor:land", "landed");
            GoalOperationJournal.Completed(repo, repairGoal, "conductor:cleanup", "cleaned");
            var repairContext = CreateAcceptanceContext(kernel, repo, repairGoal);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance-repair", repairGoal.Id.Value[..8], "--confirm-acceptance-repair"],
                repairContext));

            Assert.Contains("Acceptance repaired:", output);
            Assert.Null(kernel.GetGoal(repairGoal.Id).LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_cleanup_failure_reports_cleanup_blocker_after_landing_stays_accepted")]
    public void CliAcceptanceCleanupFailureReportsCleanupBlockerAfterLandingStaysAccepted()
    {
        var repo = CreateSeededRepository();
        var originalDeleteDirectory = GoalWorktrees.DeleteDirectory;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance cleanup leftover test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(output.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
            Assert.True(acceptance.IsAccepted);

            GoalWorktrees.DeleteDirectory = cleanupPath =>
                cleanupPath.Equals(worktreePath, StringComparison.OrdinalIgnoreCase)
                    ? false
                    : originalDeleteDirectory(cleanupPath);

            var sweep = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            var blocker = Assert.Single(sweep.Blockers);
            Assert.Equal("completed-worktree-cleanup-needed", blocker.Kind);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(kernel.BuildGoalAcceptanceSummary(goal.Id).IsAccepted);
            Assert.True(Directory.Exists(worktreePath));
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDeleteDirectory;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing")]
    public async Task CliAcceptanceQueueApplyPersistsCleanupAfterOutsideTransactionRouting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Queued acceptance persistence", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["previous verifier failure"]);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "queue-persist.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Queued persistence goal");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var changed = false;
            var output = CaptureConsole(() =>
            {
                changed = CliPersistentStateRunner.ExecuteCommand(
                    ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    acceptanceVerifier: FakeAcceptanceVerifier.Passed());
            });

            Assert.True(changed);
            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal), output);
            Assert.True(File.Exists(Path.Combine(repo, "queue-persist.txt")));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(HasAnyCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, goal.Id)));

            var reloaded = await stateRepository.LoadAsync();
            var reloadedGoal = reloaded.GetGoal(goal.Id);
            Assert.Null(reloadedGoal.LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup")]
    public void CliLifecycleSimpleGoalRunsAcceptsAndDefersWorkspaceCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));

            Assert.Equal(goal.Id, context.CurrentGoal!.Id);
            Assert.Equal(1, kernel.Goals.Count);
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_runs_five_role_goal_accepts_and_defers_workspace_cleanup")]
    public void CliLifecycleGoalRunsFiveRoleGoalAcceptsAndDefersWorkspaceCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = EchoAgents();
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-goal", "Ship a five-role echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(5, goal.Tasks.Count);
            Assert.True(goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage goal: created and activated.", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_records_landed_and_defers_workspace_cleanup")]
    public void CliGoalMarkLandedRecordsLandedAndDefersWorkspaceCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "landed.txt"), "landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            RunGit(repo, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var outputLines = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("Goal landed cleanup:", outputLines);
            Assert.Contains("cleanup: goal marked landed; cleanup-needed recorded", outputLines);
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
            var facts = new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: false);
            Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(goal, facts));

            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove")]
    public void CliGoalMarkLandedPassesRemainingCleanupBudgetToWorktreeRemove()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed budget goal", repo);
            RunGit(repo, "branch", GoalWorktrees.BranchName(goal.Id));
            var worktrees = new CapturingGoalWorktreeService
            {
                RemoveOverride = (_, _, _, _) => new GoalWorktreeRemoveResult(
                    "Workspace already clean; nothing to remove.",
                    null,
                    [],
                    null)
            };
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            kernel.SetEventWriter(eventWriter);
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                EventWriter = eventWriter,
                Worktrees = worktrees,
                GoalMarkLandedElapsedMilliseconds = () => 9_000
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                context));

            Assert.True(output.Contains("cleanup: goal marked landed; cleanup-needed recorded", StringComparison.Ordinal));
            Assert.Null(worktrees.RemoveTimeoutMilliseconds);
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_records_landed_state_before_deferred_cleanup")]
    public void CliGoalMarkLandedRecordsLandedStateBeforeDeferredCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed deferred cleanup goal", repo);
            RunGit(repo, "branch", GoalWorktrees.BranchName(goal.Id));
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            kernel.SetEventWriter(eventWriter);
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                EventWriter = eventWriter,
                GoalMarkLandedElapsedMilliseconds = () => CliCommandHandlers.GoalMarkLandedPromptTimeoutMilliseconds
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                context));

            Assert.Contains("cleanup: goal marked landed; cleanup-needed recorded", output);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, e =>
                e.Operation == "conductor:land" && e.Status == GoalOperationStatus.Completed);
            Assert.Contains(journal.LatestByOperation, e =>
                e.Operation == "conductor:record" && e.Status == GoalOperationStatus.Completed);
            Assert.Contains(journal.LatestByOperation, e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Failed);
            Assert.DoesNotContain(journal.LatestByOperation, e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Completed);

            var cleanupBackoff = GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id);
            Assert.NotNull(cleanupBackoff);
            Assert.StartsWith("remove:", cleanupBackoff!.Reason, StringComparison.Ordinal);
            var recovery = GoalRecoveryPlanner.Build(kernel, goal, repo);
            Assert.NotNull(recovery.CleanupBackoff);
            Assert.Contains(recovery.RecommendedActions, action =>
                action.Contains("workspace remove", StringComparison.Ordinal));
            var facts = new GoalLifecycleFacts(IsMerged: true, IsRecorded: true);
            Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(goal, facts));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove")]
    public void CliGoalMarkLandedForceDeletesBranchKeptBySafeWorktreeRemove()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed force cleanup goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "force-landed.txt"), "force landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work not merged to main");

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(output.Contains("cleanup: goal marked landed; cleanup-needed recorded", StringComparison.Ordinal));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(Directory.Exists(worktreePath));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete")]
    public void GoalWorktreesRemoveInvokesBuildServerShutdownBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // Simulate the unregistered-but-directory-remains half-state so the
            // test exercises BuildServerShutdown → DeleteDirectoryWithRetry directly.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var shutdownCalled = false;
            string? shutdownPath = null;
            var directoryExistedAtShutdown = false;

            GoalWorktrees.BuildServerShutdown = (worktreePath, _) =>
            {
                shutdownCalled = true;
                shutdownPath = worktreePath;
                directoryExistedAtShutdown = Directory.Exists(worktreePath);
            };

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(shutdownCalled);
            Assert.Equal(path, shutdownPath);
            Assert.True(directoryExistedAtShutdown);
            Assert.True(result.IsComplete);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete")]
    public void GoalWorktreesRemoveResetsSandboxAclBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.True(acl.ResetPaths.SequenceEqual([path]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup")]
    public void GoalWorktreesRemoveThreadsRemainingBudgetIntoNestedCleanup()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalElapsed = GoalWorktrees.CleanupElapsedMilliseconds;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            long elapsedMilliseconds = 2_500;
            int? buildServerTimeout = null;
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            GoalWorktrees.BuildServerShutdown = (_, timeoutMilliseconds) =>
            {
                buildServerTimeout = timeoutMilliseconds;
                elapsedMilliseconds = 9_700;
            };
            GoalWorktrees.SandboxAclHelper = acl;

            var result = GoalWorktrees.Remove(repo, goalId, null, 10_000);

            Assert.True(result.IsComplete);
            Assert.Equal(7_500, buildServerTimeout);
            Assert.True(acl.TimeoutMilliseconds.SequenceEqual([300]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupElapsedMilliseconds = originalElapsed;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_defers_when_acl_reset_is_access_denied")]
    public void GoalWorktreesRemoveDefersWhenAclResetIsAccessDenied()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var warnings = new List<GoalWorktreeCleanupWarning>();
            GoalWorktrees.SandboxAclHelper = new AccessDeniedSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            GoalWorktrees.FindLockHoldersForCleanup = _ => [];

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.False(result.IsComplete);
            Assert.Equal(path, result.LeftoverPath);
            Assert.Equal("remove:cleanup-budget-exhausted", result.CleanupBackoff?.Reason);
            Assert.True(Directory.Exists(path));
            Assert.True(HasCleanupNeededRecord(repo, path, "remove:cleanup-budget-exhausted"));
            Assert.Contains(warnings, warning =>
                warning.Path == path &&
                warning.Operation == "remove:acl-reset" &&
                warning.Exception is System.ComponentModel.Win32Exception);
            Assert.Contains(warnings, warning => warning.Operation == "remove:cleanup-needed");
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget")]
    public void GoalWorktreesRemoveDefersWhenBuildServerCleanupExhaustsBudget()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalElapsed = GoalWorktrees.CleanupElapsedMilliseconds;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBudgetBackoff = GoalWorktrees.CleanupBudgetExhaustedBackoffDuration;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            long elapsedMilliseconds = 2_500;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            GoalWorktrees.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            GoalWorktrees.BuildServerShutdown = (_, _) => elapsedMilliseconds = 10_000;
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBudgetExhaustedBackoffDuration = TimeSpan.FromMinutes(1);
            GoalWorktrees.FindLockHoldersForCleanup = _ => [];

            var result = GoalWorktrees.Remove(repo, goalId, null, 10_000);

            Assert.False(result.IsComplete);
            Assert.Equal(path, result.LeftoverPath);
            Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", result.ResumeCommand);
            if (result.CleanupBackoff is not { } cleanupBackoff)
            {
                throw new InvalidOperationException("Expected cleanup backoff details.");
            }

            Assert.Equal("remove:cleanup-budget-exhausted", cleanupBackoff.Reason);
            Assert.Equal(now.AddMinutes(1), cleanupBackoff.SkipUntilUtc);
            Assert.True(result.Message.Contains("skip_until_utc=", StringComparison.Ordinal));
            Assert.Empty(acl.ResetPaths);
            Assert.True(Directory.Exists(path));
            Assert.True(HasCleanupNeededRecord(repo, path, "remove:cleanup-budget-exhausted"));
            Assert.Contains(warnings, warning =>
                warning.Operation == "remove:build-server-shutdown" &&
                warning.Exception is TimeoutException);
            Assert.Contains(warnings, warning => warning.Operation == "remove:cleanup-needed");

            elapsedMilliseconds = 0;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            var retry = GoalWorktrees.Remove(repo, goalId, null, 10_000);

            Assert.True(retry.IsComplete);
            Assert.False(Directory.Exists(path));
            Assert.DoesNotContain(warnings, warning => warning.Operation == "remove:skip-backoff");
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupElapsedMilliseconds = originalElapsed;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBudgetExhaustedBackoffDuration = originalBudgetBackoff;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_unregistered_leftover_directory_as_incomplete")]
    public void GoalWorktreesRemoveReportsUnregisteredLeftoverDirectoryAsIncomplete()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            GoalWorktrees.DeleteDirectory = _ => false;
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(10);

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.False(result.IsComplete);
            Assert.Equal(path, result.LeftoverPath);
            Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", result.ResumeCommand);
            Assert.True(result.Message.Contains("leftover directory cleanup is incomplete", StringComparison.OrdinalIgnoreCase));
            if (result.CleanupBackoff is not { } cleanupBackoff)
            {
                throw new InvalidOperationException("Expected first cleanup backoff details.");
            }

            Assert.Equal("remove:leftover-directory", cleanupBackoff.Reason);
            Assert.Equal(now.AddMinutes(10), cleanupBackoff.SkipUntilUtc);
            Assert.Equal(TimeSpan.FromMinutes(10), cleanupBackoff.RemainingWait);
            Assert.True(result.Message.Contains("skip_until_utc=2026-07-02T05:10:00.0000000+00:00", StringComparison.Ordinal));
            Assert.True(result.Message.Contains("remaining_wait=00:10:00", StringComparison.Ordinal));
            Assert.True(Directory.Exists(path));
            Assert.True(HasCleanupNeededRecord(repo, path, "remove:leftover-directory"));
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_cleanup_failure_logs_warning_and_reports_resumable_leftover")]
    public void GoalWorktreesCleanupFailureLogsWarningAndReportsResumableLeftover()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var warnings = new List<GoalWorktreeCleanupWarning>();

            GoalWorktrees.DeleteDirectory = _ => false;
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.False(result.IsComplete);
            Assert.Equal(path, result.LeftoverPath);
            Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", result.ResumeCommand);
            Assert.True(result.Message.Contains("leftover directory cleanup is incomplete", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(path));
            Assert.True(HasCleanupNeededRecord(repo, path, "remove:leftover-directory"));
            Assert.Contains(warnings, warning => warning.Path == path && warning.Operation == "remove");
            Assert.Contains(warnings, warning => warning.Operation == "remove:cleanup-needed");
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_honors_cleanup_needed_backoff_when_no_lock_holder_was_recorded")]
    public void GoalWorktreesRemoveHonorsCleanupNeededBackoffWhenNoLockHolderWasRecorded()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            var deleteAttempts = 0;
            var shutdownCalls = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();

            GoalWorktrees.DeleteDirectory = _ =>
            {
                deleteAttempts++;
                return false;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => shutdownCalls++;
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            GoalWorktrees.FindLockHoldersForCleanup = _ => [];

            var first = GoalWorktrees.Remove(repo, goalId);
            var firstBackoff = GoalWorktrees.TryGetCleanupBackoff(repo, goalId);
            var second = GoalWorktrees.Remove(repo, goalId);
            var secondBackoff = GoalWorktrees.TryGetCleanupBackoff(repo, goalId);

            Assert.False(first.IsComplete);
            Assert.False(second.IsComplete);
            Assert.Equal(path, second.LeftoverPath);
            if (firstBackoff is not { } firstBackoffDetail)
            {
                throw new InvalidOperationException("Expected first cleanup backoff details.");
            }

            if (secondBackoff is not { } secondBackoffDetail)
            {
                throw new InvalidOperationException("Expected second cleanup backoff details.");
            }

            Assert.Equal(firstBackoffDetail.SkipUntilUtc, secondBackoffDetail.SkipUntilUtc);
            Assert.NotNull(second.CleanupBackoff);
            Assert.True(second.Message.Contains("skip_until_utc=", StringComparison.Ordinal));
            Assert.Equal(1, deleteAttempts);
            Assert.Equal(1, shutdownCalls);
            Assert.True(HasCleanupNeededRecord(repo, path, "remove:leftover-directory"));
            Assert.Contains(warnings, warning => warning.Operation == "remove:cleanup-needed");
            Assert.Contains(warnings, warning => warning.Operation == "remove:skip-backoff");
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_retries_cleanup_needed_immediately_after_lock_release")]
    public void GoalWorktreesRemoveRetriesCleanupNeededImmediatelyAfterLockRelease()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            var deleteAttempts = 0;
            var shutdownCalls = 0;
            var lockHolderProbes = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();

            GoalWorktrees.DeleteDirectory = cleanupPath =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    return false;
                }

                Directory.Delete(cleanupPath, recursive: true);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => shutdownCalls++;
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            GoalWorktrees.FindLockHoldersForCleanup = _ => lockHolderProbes++ == 0
                ? [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "blocked cleanup test")]
                : [];

            var first = GoalWorktrees.Remove(repo, goalId);
            var second = GoalWorktrees.Remove(repo, goalId);

            Assert.False(first.IsComplete);
            Assert.True(second.IsComplete);
            Assert.Null(second.LeftoverPath);
            Assert.Equal(2, deleteAttempts);
            Assert.Equal(2, shutdownCalls);
            Assert.False(Directory.Exists(path));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:leftover-directory:lock-held"));
            Assert.Contains(warnings, warning => warning.Operation == "remove:cleanup-needed");
            Assert.DoesNotContain(warnings, warning => warning.Operation == "remove:skip-backoff");
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete")]
    public void GoalWorktreesRemoveReapsRecordedWorkerProcessesBeforeDelete()
    {
        var repo = CreateSeededRepository();
        var originalKill = GoalWorktrees.TryKillRecordedProcess;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Reap worker processes", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var startedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(111, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [111, 222]));

            var killed = new List<int>();
            GoalWorktrees.TryKillRecordedProcess = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([111, 222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.TryKillRecordedProcess = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_skips_protected_recorded_worker_process")]
    public void GoalWorktreesRemoveSkipsProtectedRecordedWorkerProcess()
    {
        var repo = CreateSeededRepository();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            var protectedPid = 111;
            Environment.SetEnvironmentVariable(
                CliProtectedProcessEnvironment.ProtectedPidVariable,
                protectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Remove protected worker process", [
                new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var task = goal.Tasks[0];
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(path);
            var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(protectedPid, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [protectedPid]));

            var killed = new List<int>();
            WorkerProcessJobs.TryKillPidTree = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.Empty(killed);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_kills_unprotected_recorded_worker_process")]
    public void GoalWorktreesRemoveKillsUnprotectedRecordedWorkerProcess()
    {
        var repo = CreateSeededRepository();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, "111");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Remove unprotected worker process", [
                new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var task = goal.Tasks[0];
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(path);
            var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(222, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [222]));

            var killed = new List<int>();
            WorkerProcessJobs.TryKillPidTree = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
            DeleteDirectory(repo);
        }
    }

    private sealed class AccessDeniedSandboxAclHelper : ISandboxAclHelper
    {
        public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
        {
            throw new System.ComponentModel.Win32Exception(5, "Access is denied.");
        }
    }
}
