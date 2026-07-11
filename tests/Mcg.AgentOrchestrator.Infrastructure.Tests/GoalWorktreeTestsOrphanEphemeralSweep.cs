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
public sealed class GoalWorktreeTestsOrphanEphemeralSweep : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_deletes_orphaned_worktree_directory")]
    public void GoalWorktreesSweepDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned1");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory")]
    public void GoalWorktreeOrphanSweepSchedulerSweepNowDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-scheduler");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktreeOrphanSweepScheduler.SweepNow(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Conductor_cleanup_records_cleanup_needed_without_deleting_on_critical_path")]
    public void ConductorCleanupRecordsCleanupNeededWithoutDeletingOnCriticalPath()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalLockHolders = GoalWorktrees.FindLockHoldersForCleanup;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Conductor cleanup retry test", repo);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);

            GoalOperationJournal.Completed(repo, goal, "conductor:land", "landed");
            GoalOperationJournal.Completed(repo, goal, "conductor:record", "recorded");

            var driver = new ConductorDriver(
                kernel,
                workspace,
                FakeAcceptanceVerifier.Passed(),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                providers: new InMemoryModelProviderRegistry([]));

            // Simulate a previous incomplete git worktree removal: git metadata is gone, but
            // the directory is still present until the lock releases.
            File.Delete(Path.Combine(worktreePath, ".git"));
            RunGit(repo, "worktree", "prune");

            var deleteAttempts = 0;
            var lockHolderProbes = 0;
            GoalWorktrees.DeleteDirectory = path =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    return false;
                }

                Directory.Delete(path, recursive: true);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new NoOpSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            GoalWorktrees.FindLockHoldersForCleanup = _ => lockHolderProbes++ == 0
                ? [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "blocked cleanup test")]
                : [];

            var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(first.Outcome);
            Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
            Assert.True(Directory.Exists(worktreePath));
            Assert.Equal(0, deleteAttempts);
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id));
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_clears_existing_orphan_and_retries_once")]
    public void GoalWorktreesEnsureClearsExistingOrphanAndRetriesOnce()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.WorktreePath(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(path, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var ensured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, ensured);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete")]
    public void GoalWorktreesSweepResetsAclOnlyAfterAccessDeniedDelete()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-access-denied");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var deleteAttempts = 0;
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.DeleteDirectoryForCleanup = path =>
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
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => throw new InvalidOperationException("cheap orphan cleanup should not shut down build servers");

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(2, deleteAttempts);
            Assert.True(acl.ResetPaths.SequenceEqual([orphanPath]));
            Assert.False(Directory.Exists(orphanPath));
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset")]
    public void GoalWorktreesSweepRecordsTimeoutBackoffAndSkipsRepeatAclReset()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalElapsed = GoalWorktrees.CleanupElapsedMilliseconds;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-timeout");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            long elapsedMilliseconds = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.AccessDenied,
                "Access to the path is denied.");
            GoalWorktrees.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            GoalWorktrees.SandboxAclHelper = new TimeoutSandboxAclHelper(acl, () => elapsedMilliseconds = GitCli.DefaultTimeoutMilliseconds);
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(10);

            var first = GoalWorktrees.SweepOrphanedWorktrees(repo);
            var second = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Empty(first.LeftoverPaths.Where(path => !string.Equals(path, orphanPath, StringComparison.Ordinal)));
            Assert.Equal([orphanPath], second.LeftoverPaths);
            Assert.True(acl.ResetPaths.SequenceEqual([orphanPath]));
            Assert.Contains(warnings, warning => warning.Operation == "orphan-sweep:acl-reset" && warning.Exception is TimeoutException);
            Assert.Contains(warnings, warning =>
                warning.Operation == "orphan-sweep:backoff" &&
                warning.Exception.Message.Contains("Cleanup-needed record persisted in SQLite", StringComparison.Ordinal) &&
                warning.Exception.Message.Contains(orphanPath, StringComparison.Ordinal));
            Assert.Contains(warnings, warning => warning.Operation == "orphan-sweep:skip-backoff");
            Assert.True(Directory.Exists(orphanPath));
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.CleanupElapsedMilliseconds = originalElapsed;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_backoff_for_transient_orphan_delete_failure")]
    public void GoalWorktreesSweepRecordsBackoffForTransientOrphanDeleteFailure()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-transient");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "transient residue");
            var warnings = new List<GoalWorktreeCleanupWarning>();
            GoalWorktrees.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.Transient,
                "The process cannot access the file because it is being used by another process.");
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Equal([orphanPath], result.LeftoverPaths);
            Assert.True(Directory.Exists(orphanPath));
            Assert.True(HasCleanupNeededRecord(repo, orphanPath, "orphan-sweep:delete-failed"));
            Assert.Contains(warnings, warning => warning.Path == orphanPath && warning.Operation == "orphan-sweep");
            Assert.Contains(warnings, warning => warning.Path == orphanPath && warning.Operation == "orphan-sweep:backoff");
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_backoff_when_second_delete_fails_after_acl_reset")]
    public void GoalWorktreesSweepRecordsBackoffWhenSecondDeleteFailsAfterAclReset()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-second-delete");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "acl residue");
            var deleteAttempts = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.DeleteDirectoryForCleanup = _ =>
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
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

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
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
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

            var result = GoalWorktrees.SweepOwnedEphemeralDirectories(repo, goalId);

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
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var contextPath = Path.Combine(repo, ".orchestrator-context", goalId.Value);
            Directory.CreateDirectory(contextPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var attempts = 0;

            GoalWorktrees.DeleteDirectoryForCleanup = _ =>
            {
                attempts++;
                return GoalWorktreeDeleteResult.Failed(
                    GoalWorktreeDeleteFailureKind.Unknown,
                    "Directory deletion failed.");
            };
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var first = GoalWorktrees.SweepOwnedEphemeralDirectories(repo, goalId);
            var second = GoalWorktrees.SweepOwnedEphemeralDirectories(repo, goalId);

            Assert.False(first.IsComplete);
            Assert.False(second.IsComplete);
            Assert.Equal([contextPath], second.LeftoverPaths);
            Assert.Equal(1, attempts);
            Assert.True(HasCleanupNeededRecord(repo, contextPath, "owned-ephemeral-sweep:delete-failed"));
            Assert.Contains(warnings, warning => warning.Operation == "owned-ephemeral-sweep:backoff");
            Assert.Contains(warnings, warning => warning.Operation == "owned-ephemeral-sweep:skip-backoff");
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_owned_ephemeral_cleanup_leftover")]
    public void GoalWorktreesRemoveReportsOwnedEphemeralCleanupLeftover()
    {
        var repo = CreateSeededRepository();
        var originalDeleteForCleanup = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var worktreePath = GoalWorktrees.Ensure(repo, goalId);
            var contextPath = Path.Combine(repo, ".orchestrator-context", goalId.Value);
            Directory.CreateDirectory(contextPath);
            File.WriteAllText(Path.Combine(contextPath, "digest.md"), "digest");
            var warnings = new List<GoalWorktreeCleanupWarning>();

            GoalWorktrees.DeleteDirectoryForCleanup = cleanupPath =>
                cleanupPath.Equals(contextPath, StringComparison.OrdinalIgnoreCase)
                    ? GoalWorktreeDeleteResult.Failed(
                        GoalWorktreeDeleteFailureKind.Unknown,
                        "Directory deletion failed.")
                    : originalDeleteForCleanup(cleanupPath);
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.Remove(repo, goalId);

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
            GoalWorktrees.DeleteDirectoryForCleanup = originalDeleteForCleanup;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
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

            var result = TerminalGoalSweep.Run(kernel, repo, goal.Id);
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
