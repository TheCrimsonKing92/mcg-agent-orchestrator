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

public sealed class GoalWorktreeTestsRemoveCleanup : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void BuiltCleanupHooksKeepTheirCapturedConfigurationAfterFurtherArrangement()
    {
        var firstAcl = new RecordingSandboxAclHelper();
        var laterAcl = new RecordingSandboxAclHelper();
        var firstOptions = new GoalWorktreeCleanupOptions(TimeSpan.FromMinutes(2), 2, TimeSpan.FromHours(1));
        var firstDirectory = Path.Combine(Path.GetTempPath(), "first-cleanup-owner");
        var builder = new GoalWorktreeCleanupHooksBuilder
        {
            SandboxAclHelper = firstAcl,
            CleanupElapsedMilliseconds = () => 17,
            CleanupBackoffDuration = TimeSpan.FromMinutes(3),
            CleanupBudgetExhaustedBackoffDuration = TimeSpan.FromSeconds(4)
        };
        builder.ConfigureCleanup(firstOptions, firstDirectory);
        var first = builder.Build();

        builder.SandboxAclHelper = laterAcl;
        builder.CleanupElapsedMilliseconds = () => 29;
        builder.CleanupBackoffDuration = TimeSpan.FromMinutes(8);
        builder.CleanupBudgetExhaustedBackoffDuration = TimeSpan.FromSeconds(9);
        builder.ConfigureCleanup(GoalWorktreeCleanupOptions.Default, Path.Combine(Path.GetTempPath(), "later-cleanup-owner"));
        var later = builder.Build();

        first.ResetSandboxAcl(firstDirectory, 100);
        Assert.Equal(firstDirectory, Assert.Single(firstAcl.ResetPaths));
        Assert.Empty(laterAcl.ResetPaths);
        Assert.Equal(17, first.CleanupElapsedMilliseconds()!());
        Assert.Equal(TimeSpan.FromMinutes(3), first.CleanupBackoffDuration());
        Assert.Equal(TimeSpan.FromSeconds(4), first.CleanupBudgetExhaustedBackoffDuration());
        Assert.Equal(firstOptions, first.CleanupOptions());
        Assert.Equal(Path.GetFullPath(firstDirectory), first.CleanupAttentionStoreDirectory());
        Assert.Equal(29, later.CleanupElapsedMilliseconds()!());
        Assert.NotEqual(first.CleanupAttentionStoreDirectory(), later.CleanupAttentionStoreDirectory());
    }

    [Xunit.Fact]
    public void FindLockHolders_UsesOneCandidateSnapshotAndPreservesUnavailableBuildServer()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-lock-holder-snapshot");
        var snapshotCalls = 0;
        var hooks = new GoalWorktreeCleanupHooks
        {
            ProcessCommandLineSnapshot = names =>
            {
                snapshotCalls++;
                Assert.Equal(
                    new[] { "claude", "codex", "dotnet", "MSBuild", "node", "powershell", "pwsh", "VBCSCompiler" },
                    names.Order(StringComparer.OrdinalIgnoreCase));
                return new ProcessCommandLineSnapshot(
                    new Dictionary<int, ProcessInspectionRecord>
                    {
                        [101] = new(101, 1, "dotnet", null, null, $"dotnet test --artifacts-path \"{path}\"", ProcessInspectionStatus.Available),
                        [202] = new(202, 1, "VBCSCompiler", null, null, null, ProcessInspectionStatus.AccessDenied),
                        [303] = new(303, 1, "codex", null, null, "codex unrelated", ProcessInspectionStatus.Available)
                    });
            }
        };

        var holders = hooks.FindLockHoldersForCleanup(path);

        Assert.Equal(1, snapshotCalls);
        Assert.Collection(
            holders.OrderBy(holder => holder.ProcessId),
            holder => Assert.Equal(101, holder.ProcessId),
            holder =>
            {
                Assert.Equal(202, holder.ProcessId);
                Assert.Null(holder.CommandLine);
            });
    }

    [Xunit.Fact]
    public void FindLockHolders_EnumerationFailure_ReturnsConservativeTypedHolder()
    {
        var failure = new ProcessInspectionFailure(
            ProcessInspectionStatus.NativeFailure,
            24,
            "CreateToolhelp32Snapshot");
        var hooks = new GoalWorktreeCleanupHooks
        {
            ProcessCommandLineSnapshot = _ => new ProcessCommandLineSnapshot(
                new Dictionary<int, ProcessInspectionRecord>(),
                failure)
        };

        var holder = Assert.Single(hooks.FindLockHoldersForCleanup("C:\\repo\\goal"));

        Assert.Equal(0, holder.ProcessId);
        Assert.Equal("process-inspection-unavailable", holder.ProcessName);
        Assert.Equal(
            "status=NativeFailure nativeError=24 operation=CreateToolhelp32Snapshot",
            holder.CommandLine);
    }

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

            Assert.Equal([firstPath], firstInvocations);
            Assert.Equal([secondPath], secondInvocations);
            Assert.DoesNotContain(secondPath, firstInvocations);
            Assert.DoesNotContain(firstPath, secondInvocations);
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryWithRetry(firstRoot);
            GoalWorktrees.DeleteDirectoryWithRetry(secondRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory")]
    public void GoalWorktreesRemoveResumesAfterUnregisteredWorktreeLeavesDirectory()
    {
        var repo = CreateSeededRepository();
        try
        {
            var shutdownRequests = CaptureBuildServerShutdownRequests();
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);
            File.WriteAllText(Path.Combine(path, "leftover.log"), "held by prior test process");

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            Assert.True(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.True(BranchExists(repo, branch));

            var removeResult = RemoveWorktree(repo, goalId);
            Assert.Equal("Removed workspace and merged branch " + branch + ".", removeResult.Message);
            Assert.True(removeResult.IsComplete);

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
            AssertBuildServerShutdownRequests(shutdownRequests, path);
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

            var removeResult = RemoveWorktree(repo, goalId);

            Assert.False(removeResult.IsComplete);
            Assert.Contains("kept because it has unmerged commits at deletion time", removeResult.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(BranchExists(repo, branch));
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

            var removeResult = RemoveWorktree(repo, goalId);

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

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_aborts_branch_delete_when_branch_unmerged_at_deletion_time")]
    public void GoalWorktreesRemoveAbortsBranchDeleteWhenBranchUnmergedAtDeletionTime()
    {
        var repo = CreateSeededRepository();
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        try
        {
            var warnings = new List<GoalWorktreeCleanupWarning>();
            CleanupHooks.CleanupWarningSink = warnings.Add;
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

            var first = RemoveWorktree(repo, goalId);

            Assert.False(first.IsComplete);
            Assert.Equal(path, first.LeftoverPath);
            Assert.Null(first.CleanupBackoff);
            Assert.Contains("kept because it has unmerged commits at deletion time", first.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(BranchExists(repo, branch));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:branch-delete-failed"));

            var second = RemoveWorktree(repo, goalId);

            Assert.False(second.IsComplete);
            Assert.Equal(path, second.LeftoverPath);
            Assert.Null(second.CleanupBackoff);
            Assert.Contains("kept because it has unmerged commits at deletion time", second.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(BranchExists(repo, branch));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:branch-delete-failed"));
            Assert.Empty(warnings);

            RunGit(repo, "branch", "-D", branch);
            var final = RemoveWorktree(repo, goalId);

            Assert.True(final.IsComplete);
            Assert.False(BranchExists(repo, branch));
            Assert.False(HasCleanupNeededRecord(repo, path, "remove:branch-delete-failed"));
        }
        finally
        {
            CleanupHooks.CleanupWarningSink = originalWarnings;
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

            var first = RemoveWorktree(repo, goalId);
            Assert.True(first.IsComplete);

            // Second call with nothing left must return success, not throw.
            var second = RemoveWorktree(repo, goalId);
            Assert.True(second.IsComplete);
            Assert.True(second.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase));
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
        var originalShutdown = CleanupHooks.BuildServerShutdown;
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

            CleanupHooks.BuildServerShutdown = (worktreePath, _) =>
            {
                shutdownCalled = true;
                shutdownPath = worktreePath;
                directoryExistedAtShutdown = Directory.Exists(worktreePath);
            };

            var result = RemoveWorktree(repo, goalId);

            Assert.True(shutdownCalled);
            Assert.Equal(path, shutdownPath);
            Assert.True(directoryExistedAtShutdown);
            Assert.True(result.IsComplete);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            CleanupHooks.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete")]
    public void GoalWorktreesRemoveResetsSandboxAclBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var acl = new RecordingSandboxAclHelper();
            CleanupHooks.SandboxAclHelper = acl;
            CleanupHooks.BuildServerShutdown = (_, _) => { };

            var result = RemoveWorktree(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.True(acl.ResetPaths.SequenceEqual([path]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup")]
    public void GoalWorktreesRemoveThreadsRemainingBudgetIntoNestedCleanup()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalElapsed = CleanupHooks.CleanupElapsedMilliseconds;
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
            CleanupHooks.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            CleanupHooks.BuildServerShutdown = (_, timeoutMilliseconds) =>
            {
                buildServerTimeout = timeoutMilliseconds;
                elapsedMilliseconds = 9_700;
            };
            CleanupHooks.SandboxAclHelper = acl;

            var result = RemoveWorktree(repo, goalId, null, 10_000);

            Assert.True(result.IsComplete);
            Assert.Equal(7_500, buildServerTimeout);
            Assert.True(acl.TimeoutMilliseconds.SequenceEqual([300]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupElapsedMilliseconds = originalElapsed;
            DeleteDirectory(repo);
        }
    }
}
