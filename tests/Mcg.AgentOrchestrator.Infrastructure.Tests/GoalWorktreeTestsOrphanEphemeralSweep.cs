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


public sealed class GoalWorktreeTestsOrphanEphemeralSweep : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_deletes_orphaned_worktree_directory")]
    public void GoalWorktreesSweepDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned1");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            CleanupHooks.SandboxAclHelper = acl;
            CleanupHooks.BuildServerShutdown = (_, _) => { };

            var result = SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_cleanup_status_contains_non_WAL_legacy_state_failure")]
    public void GoalWorktreesCleanupStatusContainsNonWalLegacyStateFailure()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(orchestratorDirectory);
            var statePath = Path.Combine(orchestratorDirectory, "state.db");
            using (var connection = new SqliteConnection(
                       $"Data Source={statePath};Mode=ReadWriteCreate;Pooling=False;"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                Assert.Equal("delete", command.ExecuteScalar()?.ToString(), ignoreCase: true);
            }

            var warnings = new List<GoalWorktreeCleanupWarning>();
            CleanupHooks.CleanupWarningSink = warnings.Add;

            Assert.Empty(GoalWorktrees.ListCleanupDebt(root, CleanupHooks.Build()));
            Assert.Contains(
                warnings,
                warning =>
                    warning.Operation == "cleanup-status:read" &&
                    warning.Exception is InvalidOperationException);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory")]
    public void GoalWorktreeOrphanSweepSchedulerSweepNowDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-scheduler");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            var scheduler = new GoalWorktreeOrphanSweepScheduler(new GoalWorktreeCleanupHooks
            {
                ResetSandboxAcl = acl.ResetSandboxAcl,
                BuildServerShutdown = (_, _) => { }
            });
            var result = scheduler.SweepNow(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Conductor_cleanup_records_cleanup_needed_without_deleting_on_critical_path")]
    public void ConductorCleanupRecordsCleanupNeededWithoutDeletingOnCriticalPath()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectory;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Conductor cleanup retry test", repo);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);

            GoalOperationJournal.Completed(repo, goal, "conductor:land", "landed");
            GoalOperationJournal.Completed(repo, goal, "conductor:record", "recorded");

            // Simulate a previous incomplete git worktree removal: git metadata is gone, but
            // the directory is still present until the lock releases.
            File.Delete(Path.Combine(worktreePath, ".git"));
            RunGit(repo, "worktree", "prune");

            var deleteAttempts = 0;
            var lockHolderProbes = 0;
            CleanupHooks.DeleteDirectory = path =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    return false;
                }

                Directory.Delete(path, recursive: true);
                return true;
            };
            CleanupHooks.SandboxAclHelper = new NoOpSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => { };
            CleanupHooks.FindLockHoldersForCleanup = _ => lockHolderProbes++ == 0
                ? [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "blocked cleanup test")]
                : [];
            var warnings = new List<GoalWorktreeCleanupWarning>();
            CleanupHooks.CleanupWarningSink = warnings.Add;
            var driver = new ConductorDriver(
                kernel,
                workspace,
                FakeAcceptanceVerifier.Passed(),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                providers: new InMemoryModelProviderRegistry([]),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory, CleanupHooks.Build()).Hooks);

            var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(first.Outcome);
            Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
            Assert.True(Directory.Exists(worktreePath));
            Assert.Equal(0, deleteAttempts);
            Assert.Equal(0, lockHolderProbes);
            Assert.Contains(warnings, warning => warning.Operation == "remove:cleanup-needed");
            Assert.NotNull(TryGetCleanupBackoff(repo, goal.Id));
        }
        finally
        {
            CleanupHooks.DeleteDirectory = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_clears_existing_orphan_and_retries_once")]
    public void GoalWorktreesEnsureClearsExistingOrphanAndRetriesOnce()
    {
        var repo = CreateSeededRepository();
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.WorktreePath(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(path, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            CleanupHooks.SandboxAclHelper = acl;
            CleanupHooks.BuildServerShutdown = (_, _) => { };

            var ensured = GoalWorktrees.Ensure(repo, goalId, CleanupHooks.Build());

            Assert.Equal(path, ensured);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete")]
    public void GoalWorktreesSweepResetsAclOnlyAfterAccessDeniedDelete()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-access-denied");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var deleteAttempts = 0;
            var acl = new RecordingSandboxAclHelper();
            CleanupHooks.DeleteDirectoryForCleanup = path =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    return GoalWorktreeDeleteResult.Failed(
                        GoalWorktreeDeleteFailureKind.AccessDenied,
                        "Access to the path is denied.");
                }

                Directory.Delete(path, recursive: true);
                return GoalWorktreeDeleteResult.Success;
            };
            CleanupHooks.SandboxAclHelper = acl;
            CleanupHooks.BuildServerShutdown = (_, _) => throw new InvalidOperationException("cheap orphan cleanup should not shut down build servers");

            var result = SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(2, deleteAttempts);
            Assert.True(acl.ResetPaths.SequenceEqual([orphanPath]));
            Assert.False(Directory.Exists(orphanPath));
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset")]
    public void GoalWorktreesSweepRecordsTimeoutBackoffAndSkipsRepeatAclReset()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalElapsed = CleanupHooks.CleanupElapsedMilliseconds;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-timeout");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            long elapsedMilliseconds = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            CleanupHooks.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.AccessDenied,
                "Access to the path is denied.");
            CleanupHooks.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            CleanupHooks.SandboxAclHelper = new TimeoutSandboxAclHelper(acl, () => elapsedMilliseconds = GitCli.DefaultTimeoutMilliseconds);
            CleanupHooks.CleanupWarningSink = warnings.Add;
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);

            var first = SweepOrphanedWorktrees(repo);
            var second = SweepOrphanedWorktrees(repo);

            Assert.Empty(first.LeftoverPaths.Where(path => !string.Equals(path, orphanPath, StringComparison.Ordinal)));
            Assert.Equal([orphanPath], second.LeftoverPaths);
            Assert.True(acl.ResetPaths.SequenceEqual([orphanPath]));
            Assert.Contains(warnings, warning => warning.Operation == "orphan-sweep:acl-reset" && warning.Exception is TimeoutException);
            Assert.Contains(warnings, warning =>
                warning.Operation == "orphan-sweep:backoff" &&
                warning.Exception.Message.Contains("Cleanup-needed record persisted in SQLite", StringComparison.Ordinal) &&
                warning.Exception.Message.Contains(orphanPath, StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, warning => warning.Operation == "orphan-sweep:skip-backoff");
            Assert.Equal(1, CleanupJournalSkipCount(repo, orphanPath));
            Assert.True(Directory.Exists(orphanPath));
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.CleanupElapsedMilliseconds = originalElapsed;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_quietly_journals_in_budget_backoff_skip")]
    public void GoalWorktreesSweepQuietlyJournalsInBudgetBackoffSkip()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-quiet-skip");
            Directory.CreateDirectory(orphanPath);
            File.WriteAllText(Path.Combine(orphanPath, "leftover.txt"), "residue");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            CleanupHooks.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.Transient,
                "The process cannot access the file because it is being used by another process.");
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            CleanupHooks.CleanupWarningSink = _ => { };
            _ = SweepOrphanedWorktrees(repo);
            CleanupHooks.CleanupWarningSink = originalWarnings;

            string stdout = string.Empty;
            var stderr = CaptureConsoleError(() =>
                stdout = CaptureConsole(() =>
                {
                    var second = SweepOrphanedWorktrees(repo);
                    Assert.Equal([orphanPath], second.LeftoverPaths);
                }));

            Assert.DoesNotContain("warning: worktree-cleanup", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("warning: worktree-cleanup", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, CleanupJournalSkipCount(repo, orphanPath));
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_escalates_consecutive_failures_and_auto_resolves_after_slow_retry")]
    public void GoalWorktreesSweepEscalatesConsecutiveFailuresAndAutoResolvesAfterSlowRetry()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        var originalOptions = CleanupHooks.CleanupOptions;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-escalates-once");
            Directory.CreateDirectory(orphanPath);
            File.WriteAllText(Path.Combine(orphanPath, "leftover.txt"), "residue");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            var warnings = new List<GoalWorktreeCleanupWarning>();
            CleanupHooks.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.Transient,
                "The process cannot access the file because it is being used by another process.");
            CleanupHooks.CleanupWarningSink = warnings.Add;
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            CleanupHooks.ConfigureCleanup(
                new GoalWorktreeCleanupOptions(TimeSpan.FromMinutes(5), 3, TimeSpan.FromDays(1)),
                Path.Combine(repo, ".orchestrator"));
            CleanupHooks.FindLockHoldersForCleanup = _ =>
            [
                new WorktreeLockHolder(1234, "dotnet", "dotnet test")
            ];

            _ = SweepOrphanedWorktrees(repo);
            now = now.AddMinutes(11);
            _ = SweepOrphanedWorktrees(repo);
            now = now.AddMinutes(11);
            _ = SweepOrphanedWorktrees(repo);

            var escalations = warnings
                .Where(warning => warning.Operation == "cleanup-debt-escalated")
                .ToList();
            var escalation = Assert.Single(escalations);
            Assert.Contains("dotnet[pid=1234]", escalation.Exception.Message, StringComparison.Ordinal);
            Assert.Contains("dotnet test", escalation.Exception.Message, StringComparison.Ordinal);
            Assert.Equal(3, CleanupJournalSkipCount(repo, orphanPath));
            var debt = Assert.Single(GoalWorktrees.ListCleanupDebt(repo, CleanupHooks.Build()));
            Assert.NotNull(debt.EscalatedAtUtc);
            Assert.Equal(TimeSpan.FromDays(1), debt.RemainingWait);
            var store = CollaborationItemStore.ForDirectory(Path.Combine(repo, ".orchestrator"));
            var attention = Assert.Single(store.GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Contains("Worktree cleanup escalated", attention.Subject, StringComparison.Ordinal);

            CleanupHooks.DeleteDirectoryForCleanup = _ =>
            {
                Directory.Delete(orphanPath, recursive: true);
                return GoalWorktreeDeleteResult.Success;
            };
            now = now.AddDays(1).AddMinutes(1);
            var recovered = SweepOrphanedWorktrees(repo);

            Assert.Equal(1, recovered.RemovedCount);
            Assert.Empty(GoalWorktrees.ListCleanupDebt(repo, CleanupHooks.Build()));
            Assert.Empty(store.GetAttentionQueueAsync().GetAwaiter().GetResult());
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            CleanupHooks.ConfigureCleanup(originalOptions);
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_cleanup_status_lists_pending_debt_and_empty_state")]
    public void CliCleanupStatusListsPendingDebtAndEmptyState()
    {
        var repo = CreateSeededRepository();
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        try
        {
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            var kernel = new AgentOrchestratorKernel();
            CleanupHooks.CleanupWarningSink = _ => { };
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null)
            {
                CleanupContext = new WorktreeCleanupContext(CleanupHooks.Build())
            };

            var empty = CaptureConsole(() => CliCommandHandlers.Execute(["cleanup-status"], context));
            Assert.Contains("Cleanup status: no pending cleanup debt.", empty, StringComparison.Ordinal);

            var goalId = GoalId.New();
            _ = GoalWorktrees.RecordGoalCleanupNeeded(
                repo,
                goalId,
                "remove:acceptance-deferred",
                CleanupHooks.Build());
            now = now.AddMinutes(5);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["cleanup-status"], context));

            Assert.Contains("Cleanup status:", output, StringComparison.Ordinal);
            Assert.Contains(GoalWorktrees.WorktreePath(repo, goalId), output, StringComparison.Ordinal);
            Assert.Contains("age=00:05:00", output, StringComparison.Ordinal);
            Assert.Contains("reason=remove:acceptance-deferred", output, StringComparison.Ordinal);
            Assert.Contains("remaining_wait=00:05:00", output, StringComparison.Ordinal);
        }
        finally
        {
            CleanupHooks.CleanupWarningSink = originalWarnings;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_backoff_for_transient_orphan_delete_failure")]
    public void GoalWorktreesSweepRecordsBackoffForTransientOrphanDeleteFailure()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-transient");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "transient residue");
            var warnings = new List<GoalWorktreeCleanupWarning>();
            CleanupHooks.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.Transient,
                "The process cannot access the file because it is being used by another process.");
            CleanupHooks.CleanupWarningSink = warnings.Add;

            var result = SweepOrphanedWorktrees(repo);

            Assert.Equal([orphanPath], result.LeftoverPaths);
            Assert.True(Directory.Exists(orphanPath));
            Assert.True(HasCleanupNeededRecord(repo, orphanPath, "orphan-sweep:delete-failed"));
            Assert.Contains(warnings, warning => warning.Path == orphanPath && warning.Operation == "orphan-sweep");
            Assert.Contains(warnings, warning => warning.Path == orphanPath && warning.Operation == "orphan-sweep:backoff");
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_backoff_when_second_delete_fails_after_acl_reset")]
    public void GoalWorktreesSweepRecordsBackoffWhenSecondDeleteFailsAfterAclReset()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-second-delete");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "acl residue");
            var deleteAttempts = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            CleanupHooks.DeleteDirectoryForCleanup = _ =>
            {
                deleteAttempts++;
                return GoalWorktreeDeleteResult.Failed(
                    deleteAttempts == 1
                        ? GoalWorktreeDeleteFailureKind.AccessDenied
                        : GoalWorktreeDeleteFailureKind.Unknown,
                    deleteAttempts == 1
                        ? "Access to the path is denied."
                        : "Directory deletion failed after ACL reset.");
            };
            CleanupHooks.SandboxAclHelper = acl;
            CleanupHooks.CleanupWarningSink = warnings.Add;

            var result = SweepOrphanedWorktrees(repo);

            Assert.Equal([orphanPath], result.LeftoverPaths);
            Assert.Equal(2, deleteAttempts);
            Assert.Equal([orphanPath], acl.ResetPaths);
            Assert.True(Directory.Exists(orphanPath));
            Assert.True(HasCleanupNeededRecord(repo, orphanPath, "orphan-sweep:post-acl-delete-failed"));
            Assert.Contains(warnings, warning => warning.Path == orphanPath && warning.Operation == "orphan-sweep");
            Assert.Contains(warnings, warning => warning.Path == orphanPath && warning.Operation == "orphan-sweep:backoff");
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_owned_ephemeral_sweep_removes_goal_context_temp_and_scratch_only")]
    public void GoalWorktreesOwnedEphemeralSweepRemovesGoalContextTempAndScratchOnly()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var prefix = goalId.Value[..8];
            var contextPath = Path.Combine(repo, ".orchestrator-context", goalId.Value);
            var tempPath = Path.Combine(repo, ".t", prefix + "-dispatch");
            var scratchPath = Path.Combine(repo, ".scratch", prefix);
            var unrelatedTempPath = Path.Combine(repo, ".t", "unrelated");
            Directory.CreateDirectory(contextPath);
            Directory.CreateDirectory(tempPath);
            Directory.CreateDirectory(scratchPath);
            Directory.CreateDirectory(unrelatedTempPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            File.WriteAllText(Path.Combine(tempPath, "prompt.md"), "prompt");
            File.WriteAllText(Path.Combine(scratchPath, "body.md"), "body");
            File.WriteAllText(Path.Combine(unrelatedTempPath, "keep.txt"), "keep");

            var result = SweepOwnedEphemeralDirectories(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.Equal(3, result.RemovedCount);
            Assert.False(Directory.Exists(contextPath));
            Assert.False(Directory.Exists(tempPath));
            Assert.False(Directory.Exists(scratchPath));
            Assert.True(Directory.Exists(unrelatedTempPath));
            Assert.True(Directory.Exists(Path.Combine(repo, ".t")));
            Assert.False(Directory.Exists(Path.Combine(repo, ".scratch")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_owned_ephemeral_sweep_persists_cleanup_needed_backoff")]
    public void GoalWorktreesOwnedEphemeralSweepPersistsCleanupNeededBackoff()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectoryForCleanup;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var contextPath = Path.Combine(repo, ".orchestrator-context", goalId.Value);
            Directory.CreateDirectory(contextPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var attempts = 0;

            CleanupHooks.DeleteDirectoryForCleanup = _ =>
            {
                attempts++;
                return GoalWorktreeDeleteResult.Failed(
                    GoalWorktreeDeleteFailureKind.Unknown,
                    "Directory deletion failed.");
            };
            CleanupHooks.CleanupWarningSink = warnings.Add;

            var first = SweepOwnedEphemeralDirectories(repo, goalId);
            var second = SweepOwnedEphemeralDirectories(repo, goalId);

            Assert.False(first.IsComplete);
            Assert.False(second.IsComplete);
            Assert.Equal([contextPath], second.LeftoverPaths);
            Assert.Equal(1, attempts);
            Assert.True(HasCleanupNeededRecord(repo, contextPath, "owned-ephemeral-sweep:delete-failed"));
            Assert.Contains(warnings, warning => warning.Operation == "owned-ephemeral-sweep:backoff");
            Assert.DoesNotContain(warnings, warning => warning.Operation == "owned-ephemeral-sweep:skip-backoff");
            Assert.Equal(1, CleanupJournalSkipCount(repo, contextPath));
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDelete;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_owned_ephemeral_cleanup_leftover")]
    public void GoalWorktreesRemoveReportsOwnedEphemeralCleanupLeftover()
    {
        var repo = CreateSeededRepository();
        var originalDeleteForCleanup = CleanupHooks.DeleteDirectoryForCleanup;
        var originalWarnings = CleanupHooks.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var worktreePath = GoalWorktrees.Ensure(repo, goalId);
            var contextPath = Path.Combine(repo, ".orchestrator-context", goalId.Value);
            Directory.CreateDirectory(contextPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            var warnings = new List<GoalWorktreeCleanupWarning>();

            CleanupHooks.DeleteDirectoryForCleanup = cleanupPath =>
                cleanupPath.Equals(contextPath, StringComparison.OrdinalIgnoreCase)
                    ? GoalWorktreeDeleteResult.Failed(
                        GoalWorktreeDeleteFailureKind.Unknown,
                        "Directory deletion failed.")
                    : originalDeleteForCleanup(cleanupPath);
            CleanupHooks.CleanupWarningSink = warnings.Add;

            var result = RemoveWorktree(repo, goalId);

            Assert.False(result.IsComplete);
            Assert.Equal(contextPath, result.LeftoverPath);
            Assert.False(Directory.Exists(worktreePath));
            Assert.True(Directory.Exists(contextPath));
            Assert.True(HasCleanupNeededRecord(repo, contextPath, "owned-ephemeral-sweep:delete-failed"));
            Assert.Contains("Owned ephemeral cleanup is incomplete", result.Message, StringComparison.Ordinal);
            Assert.Contains(warnings, warning => warning.Operation == "owned-ephemeral-sweep:backoff");
        }
        finally
        {
            CleanupHooks.DeleteDirectoryForCleanup = originalDeleteForCleanup;
            CleanupHooks.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_terminal_goal_cleans_owned_ephemeral_dirs_without_worker_start")]
    public void TerminalGoalSweepTerminalGoalCleansOwnedEphemeralDirsWithoutWorkerStart()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implemented elsewhere.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Clean terminal owned ephemerals", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals
                    .Select(candidate => candidate.Id == goal.Id.Value
                        ? candidate with { Status = GoalStatus.Completed }
                        : candidate)
                    .ToArray()
            });
            var contextPath = Path.Combine(repo, ".orchestrator-context", goal.Id.Value);
            var tempPath = Path.Combine(repo, ".t", goal.Id.Value[..8] + "-prompt");
            Directory.CreateDirectory(contextPath);
            Directory.CreateDirectory(tempPath);

            var result = TerminalGoalSweep.Run(kernel, repo, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, goal.Id);
            var repairedTask = kernel.GetGoal(goal.Id).Tasks.Single();

            Assert.Contains(result.Goals.Single().Repairs, repair => repair.Kind == "owned-ephemeral-cleanup");
            Assert.Empty(result.Blockers);
            Assert.False(Directory.Exists(contextPath));
            Assert.False(Directory.Exists(tempPath));
            Assert.Null(repairedTask.LastProcess);
            Assert.Null(repairedTask.LastDispatch);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
