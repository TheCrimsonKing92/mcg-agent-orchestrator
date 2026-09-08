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

public sealed class GoalWorktreeTestsRemoveCleanupDebt : GoalWorktreeTestBase
{

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_failure_records_cleanup_needed_without_throwing")]
    public void GoalWorktreesRemoveFailureRecordsCleanupNeededWithoutThrowing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.WriteAllText(Path.Combine(path, "real-change.txt"), "not orchestrator-owned");

            var removeResult = RemoveWorktree(repo, goalId);

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

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released")]
    public void GoalWorktreesRemoveReportsLeftoverPathAndResumesWhenLockReleased()
    {
        var repo = CreateSeededRepository();
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
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
            CleanupHooks.FindLockHoldersForCleanup = _ => lockHolderProbes++ == 0
                ? [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "held open")]
                : [];

            // Hold the file open exclusively so Directory.Delete fails.
            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = RemoveWorktree(repo, goalId);
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
                var final = RemoveWorktree(repo, goalId);
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
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
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

                result = RemoveWorktree(repo, goalId);

                Assert.True(result.IsComplete);
            }
            else
            {
                // POSIX: open handles do not prevent deletion, so removal completes immediately.
                result = RemoveWorktree(repo, goalId);
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
                partial = RemoveWorktree(repo, goalId);
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

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_defers_when_acl_reset_is_access_denied")]
    public void GoalWorktreesRemoveDefersWhenAclResetIsAccessDenied()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var warnings = new List<GoalWorktreeCleanupWarning>();
            CleanupHooks.SandboxAclHelper = new AccessDeniedSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => { };
            CleanupHooks.CleanupWarningSink = warnings.Add;
            CleanupHooks.FindLockHoldersForCleanup = _ => [];

            var result = RemoveWorktree(repo, goalId);

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
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget")]
    public void GoalWorktreesRemoveDefersWhenBuildServerCleanupExhaustsBudget()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalElapsed = CleanupHooks.CleanupElapsedMilliseconds;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBudgetBackoff = CleanupHooks.CleanupBudgetExhaustedBackoffDuration;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
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
            CleanupHooks.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            CleanupHooks.BuildServerShutdown = (_, _) => elapsedMilliseconds = 10_000;
            CleanupHooks.SandboxAclHelper = acl;
            CleanupHooks.CleanupWarningSink = warnings.Add;
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBudgetExhaustedBackoffDuration = TimeSpan.FromMinutes(1);
            CleanupHooks.FindLockHoldersForCleanup = _ => [];

            var result = RemoveWorktree(repo, goalId, null, 10_000);

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
            CleanupHooks.BuildServerShutdown = (_, _) => { };
            var retry = RemoveWorktree(repo, goalId, null, 10_000);

            Assert.True(retry.IsComplete);
            Assert.False(Directory.Exists(path));
            Assert.DoesNotContain(warnings, warning => warning.Operation == "remove:skip-backoff");
        }
        finally
        {
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupElapsedMilliseconds = originalElapsed;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBudgetExhaustedBackoffDuration = originalBudgetBackoff;
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_unregistered_leftover_directory_as_incomplete")]
    public void GoalWorktreesRemoveReportsUnregisteredLeftoverDirectoryAsIncomplete()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectory;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            CleanupHooks.DeleteDirectory = _ => false;
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);

            var result = RemoveWorktree(repo, goalId);

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
            CleanupHooks.DeleteDirectory = originalDelete;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_cleanup_failure_logs_warning_and_reports_resumable_leftover")]
    public void GoalWorktreesCleanupFailureLogsWarningAndReportsResumableLeftover()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectory;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var warnings = new List<GoalWorktreeCleanupWarning>();

            CleanupHooks.DeleteDirectory = _ => false;
            CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => { };
            CleanupHooks.CleanupWarningSink = warnings.Add;

            var result = RemoveWorktree(repo, goalId);

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
            CleanupHooks.DeleteDirectory = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_honors_cleanup_needed_backoff_when_no_lock_holder_was_recorded")]
    public void GoalWorktreesRemoveHonorsCleanupNeededBackoffWhenNoLockHolderWasRecorded()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectory;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
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

            CleanupHooks.DeleteDirectory = _ =>
            {
                deleteAttempts++;
                return false;
            };
            CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => shutdownCalls++;
            CleanupHooks.CleanupWarningSink = warnings.Add;
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            CleanupHooks.FindLockHoldersForCleanup = _ => [];

            var first = RemoveWorktree(repo, goalId);
            var firstBackoff = TryGetCleanupBackoff(repo, goalId);
            var second = RemoveWorktree(repo, goalId);
            var secondBackoff = TryGetCleanupBackoff(repo, goalId);

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
            CleanupHooks.DeleteDirectory = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_retries_cleanup_needed_immediately_after_lock_release")]
    public void GoalWorktreesRemoveRetriesCleanupNeededImmediatelyAfterLockRelease()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectory;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
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

            CleanupHooks.DeleteDirectory = cleanupPath =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    return false;
                }

                Directory.Delete(cleanupPath, recursive: true);
                return true;
            };
            CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => shutdownCalls++;
            CleanupHooks.CleanupWarningSink = warnings.Add;
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            CleanupHooks.FindLockHoldersForCleanup = _ => lockHolderProbes++ == 0
                ? [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "blocked cleanup test")]
                : [];

            var first = RemoveWorktree(repo, goalId);
            var second = RemoveWorktree(repo, goalId);

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
            CleanupHooks.DeleteDirectory = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
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
