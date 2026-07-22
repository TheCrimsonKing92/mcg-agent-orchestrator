using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    public static GoalWorktreeRemoveResult Remove(string executionDirectory, GoalId goalId, AgentOrchestratorKernel? kernel = null) =>
        Remove(executionDirectory, goalId, kernel, GitCli.DefaultTimeoutMilliseconds);

    public static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        bool hasRegisteredWorktree,
        bool hasBranch) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            hasRegisteredWorktree,
            hasBranch);

    public static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        int gitTimeoutMilliseconds) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            gitTimeoutMilliseconds,
            precomputedHasRegisteredWorktree: null,
            precomputedHasBranch: null);

    private static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        int gitTimeoutMilliseconds,
        bool? precomputedHasRegisteredWorktree,
        bool? precomputedHasBranch)
    {
        var cleanupBudget = GoalWorktreeCleanupBudget.Start(gitTimeoutMilliseconds, CleanupElapsedMilliseconds);
        var path = WorktreePath(executionDirectory, goalId);
        if (!IsGitWorkTree(executionDirectory, cleanupBudget.RemainingMilliseconds))
        {
            if (!Directory.Exists(path))
            {
                return new GoalWorktreeRemoveResult("Workspace already clean; nothing to remove.", null, [], null);
            }

            RequireGitWorkTree(executionDirectory, cleanupBudget.RemainingMilliseconds);
        }

        var hasRegisteredWorktree = precomputedHasRegisteredWorktree ??
            IsRegisteredWorktree(executionDirectory, path, cleanupBudget.RemainingMilliseconds);
        var hasLeftoverDirectory = Directory.Exists(path);
        var branch = BranchName(goalId);
        var hasBranch = precomputedHasBranch ??
            BranchExists(executionDirectory, branch, cleanupBudget.RemainingMilliseconds);

        if (!hasRegisteredWorktree && !hasLeftoverDirectory && !hasBranch)
        {
            var earlyOwnedEphemeralCleanup = SweepOwnedEphemeralDirectories(executionDirectory, goalId, kernel);
            earlyOwnedEphemeralCleanup = SweepGoalBuildArtifacts(executionDirectory, goalId, earlyOwnedEphemeralCleanup);
            return CompleteOrDeferredRemoveResult(
                path,
                goalId,
                "Workspace already clean; nothing to remove.",
                "Workspace already clean, but leftover cleanup is incomplete.",
                earlyOwnedEphemeralCleanup);
        }

        var wasAlreadyUnregistered = !hasRegisteredWorktree;

        if (hasLeftoverDirectory && IsCleanupBackedOff(path, "remove", out var backoff))
        {
            var lockHolders = FindLockHoldersForCleanup(path);
            if ((!IsLockHeldCleanupNeededReason(backoff.Reason) &&
                    !IsBudgetExhaustedCleanupNeededReason(backoff.Reason)) ||
                lockHolders.Count > 0)
            {
                var detail = ToCleanupBackoff(backoff);
                WarnCleanupFailure(
                    path,
                    "remove:skip-backoff",
                    new IOException(BuildCleanupRetryMessage(path, backoff.Reason, detail)));
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred by cleanup-needed backoff for {path}. {FormatCleanupBackoff(detail)}",
                    path,
                    lockHolders,
                    ConductorRetryCommand(goalId),
                    CleanupBackoff: detail);
            }
        }
        else if (!hasLeftoverDirectory &&
            hasBranch &&
            IsCleanupBackedOff(path, "remove", out var branchBackoff))
        {
            var detail = ToCleanupBackoff(branchBackoff);
            WarnCleanupFailure(
                path,
                "remove:skip-backoff",
                new IOException(BuildCleanupRetryMessage(path, branchBackoff.Reason, detail)));
            return new GoalWorktreeRemoveResult(
                $"Workspace cleanup deferred by cleanup-needed backoff for branch {branch}. {FormatCleanupBackoff(detail)}",
                path,
                [],
                ConductorRetryCommand(goalId),
                CleanupBackoff: detail);
        }

        if (hasRegisteredWorktree)
        {
            DeleteUntrackedOrchestratorInternalArtifacts(path);
            var removal = GitCli.Run(executionDirectory, cleanupBudget.RemainingMilliseconds, "worktree", "remove", path);
            if (removal.ExitCode != 0 && IsRegisteredWorktree(executionDirectory, path, cleanupBudget.RemainingMilliseconds))
            {
                RecordCleanupNeeded(path, "remove:worktree-remove-failed");
                var detail = TryGetCleanupBackoff(path);
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred because git worktree remove failed for {path}: {removal.Error.Trim()} Commit, discard, or recover its changes; conductor cleanup will retry after the worktree is clean." +
                        (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}"),
                    path,
                    FindLockHoldersForCleanup(path),
                    ConductorRetryCommand(goalId),
                    CleanupBackoff: detail);
            }
        }
        else
        {
            // Worktree already unregistered; prune any stale tracking entries left by a prior
            // partial removal so git's internal state is consistent before we finish cleanup.
            GitCli.Run(executionDirectory, cleanupBudget.RemainingMilliseconds, "worktree", "prune");
        }

        GitCli.GitResult? branchRemoval = null;
        if (hasBranch)
        {
            if (!IsBranchAncestorOfHead(executionDirectory, branch, cleanupBudget.RemainingMilliseconds))
            {
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup aborted; branch {branch} kept because it has unmerged commits at deletion time.",
                    path,
                    Directory.Exists(path) ? FindLockHoldersForCleanup(path) : [],
                    ConductorRetryCommand(goalId));
            }

            branchRemoval = GitCli.Run(executionDirectory, cleanupBudget.RemainingMilliseconds, "branch", "-d", branch);
        }

        if (Directory.Exists(path))
        {
            ReapRecordedWorkerProcesses(kernel, path);
            if (!RunBoundedCleanupStep(path, "remove:build-server-shutdown", cleanupBudget, timeout => BuildServerShutdown(path, timeout)) ||
                !RunBoundedCleanupStep(path, "remove:acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, "remove", timeout)))
            {
                RecordCleanupNeeded(path, "remove:cleanup-budget-exhausted");
                var detail = TryGetCleanupBackoff(path);
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred because cleanup budget was exhausted before deleting {path}." +
                        (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}"),
                    path,
                    FindLockHoldersForCleanup(path),
                    ConductorRetryCommand(goalId),
                    CleanupBackoff: detail);
            }
        }

        if (Directory.Exists(path) && !DeleteDirectory(path))
        {
            WarnCleanupFailure(path, "remove", new IOException("Directory deletion failed after ACL reset."));
        }

        if (!hasBranch || branchRemoval is { ExitCode: 0 })
        {
            var completeMessage = branchRemoval is { ExitCode: 0 }
                ? $"Removed workspace and merged branch {branch}."
                : "Removed workspace.";
            var incompleteMessage = branchRemoval is { ExitCode: 0 }
                ? $"Removed workspace and merged branch {branch}, but leftover directory cleanup is incomplete."
                : "Removed workspace, but leftover directory cleanup is incomplete.";
            var ephemeralCleanup = SweepOwnedEphemeralDirectories(executionDirectory, goalId, kernel);
            ephemeralCleanup = SweepGoalBuildArtifacts(executionDirectory, goalId, ephemeralCleanup);
            return CompleteOrDeferredRemoveResult(
                path,
                goalId,
                completeMessage,
                incompleteMessage,
                ephemeralCleanup);
        }

        var ownedEphemeralCleanup = SweepOwnedEphemeralDirectories(executionDirectory, goalId, kernel);
        ownedEphemeralCleanup = SweepGoalBuildArtifacts(executionDirectory, goalId, ownedEphemeralCleanup);
        RecordCleanupNeeded(path, "remove:branch-delete-failed");
        var branchCleanupBackoff = TryGetCleanupBackoff(path);
        return new GoalWorktreeRemoveResult(
            $"Removed workspace; branch {branch} kept because branch deletion failed. Conductor retry: {ConductorRetryCommand(goalId)}" +
                (branchCleanupBackoff is null ? string.Empty : $" {FormatCleanupBackoff(branchCleanupBackoff)}"),
            path,
            [],
            ConductorRetryCommand(goalId),
            ownedEphemeralCleanup,
            branchCleanupBackoff);
    }

    public static GoalWorktreeSweepResult SweepOrphanedWorktrees(string executionDirectory, AgentOrchestratorKernel? kernel = null)
    {
        if (!IsGitWorkTree(executionDirectory))
        {
            return new GoalWorktreeSweepResult(0, []);
        }

        var worktreesRoot = Path.Combine(Path.GetFullPath(executionDirectory), DirectoryName);
        if (!Directory.Exists(worktreesRoot))
        {
            return new GoalWorktreeSweepResult(0, []);
        }

        var registered = RegisteredWorktreePaths(executionDirectory);
        var removed = 0;
        var leftovers = new List<string>();

        foreach (var directory in Directory.EnumerateDirectories(worktreesRoot))
        {
            var normalized = NormalizePath(directory);
            if (registered.Contains(normalized))
            {
                continue;
            }

            if (ClearOrphanDirectory(directory, kernel, "orphan-sweep"))
            {
                removed++;
            }
            else if (Directory.Exists(directory))
            {
                leftovers.Add(directory);
            }
        }

        return new GoalWorktreeSweepResult(removed, leftovers);
    }

    public static GoalOwnedEphemeralSweepResult SweepOwnedEphemeralDirectories(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel = null)
    {
        var root = Path.GetFullPath(executionDirectory);
        var removed = 0;
        var leftovers = new List<string>();

        foreach (var path in EnumerateOwnedEphemeralDirectories(root, goalId))
        {
            if (ClearOrphanDirectory(path, kernel, "owned-ephemeral-sweep"))
            {
                removed++;
            }
            else if (Directory.Exists(path))
            {
                leftovers.Add(path);
            }
        }

        TryDeleteEmptyDirectory(Path.Combine(root, ".t"));
        TryDeleteEmptyDirectory(Path.Combine(root, ".scratch"));

        return new GoalOwnedEphemeralSweepResult(removed, leftovers);
    }

    private static GoalOwnedEphemeralSweepResult SweepGoalBuildArtifacts(
        string executionDirectory,
        GoalId goalId,
        GoalOwnedEphemeralSweepResult ownedEphemeralCleanup)
    {
        var root = DotnetBuildEnvironmentManager.GoalRoot(goalId);
        if (!Directory.Exists(root))
        {
            ClearCleanupNeeded(root, executionDirectory);
            return ownedEphemeralCleanup;
        }

        var leftovers = ownedEphemeralCleanup.LeftoverPaths.ToList();
        if (IsCleanupBackedOff(root, "remove", out var backoff, executionDirectory))
        {
            var lockHolders = FindLockHoldersForCleanup(root);
            if (!IsLockHeldCleanupNeededReason(backoff.Reason) || lockHolders.Count > 0)
            {
                WarnCleanupFailure(
                    root,
                    "remove:goal-artifacts:skip-backoff",
                    new IOException(BuildCleanupRetryMessage(root, backoff.Reason)));
                leftovers.Add(root);
                return new GoalOwnedEphemeralSweepResult(ownedEphemeralCleanup.RemovedCount, leftovers);
            }

            ClearCleanupNeeded(root, executionDirectory);
        }

        if (DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId))
        {
            ClearCleanupNeeded(root, executionDirectory);
            return ownedEphemeralCleanup with { RemovedCount = ownedEphemeralCleanup.RemovedCount + 1 };
        }

        WarnCleanupFailure(
            root,
            "remove:goal-artifacts",
            new IOException(BuildCleanupRetryMessage(root, "remove:goal-artifacts")));
        var failureReason = FindLockHoldersForCleanup(root).Count > 0
            ? "remove:goal-artifacts:lock-held"
            : "remove:goal-artifacts";
        RecordCleanupNeeded(root, failureReason, executionDirectory);
        leftovers.Add(root);
        return new GoalOwnedEphemeralSweepResult(ownedEphemeralCleanup.RemovedCount, leftovers);
    }

    /// <summary>
    /// True when the goal worktree has committed changes against the base/main branch (work to
    /// accept), comparing the goal branch tip to its merge-base with the current branch.
    /// </summary>

    private static GoalWorktreeRemoveResult CompleteOrDeferredRemoveResult(
        string path,
        GoalId goalId,
        string completeMessage,
        string incompleteMessage,
        GoalOwnedEphemeralSweepResult? ownedEphemeralCleanup = null)
    {
        if (!Directory.Exists(path))
        {
            ClearCleanupNeeded(path);
            if (ownedEphemeralCleanup is { IsComplete: false })
            {
                var leftover = ownedEphemeralCleanup.LeftoverPaths[0];
                return new GoalWorktreeRemoveResult(
                    $"{completeMessage} Owned ephemeral cleanup is incomplete. Conductor retry: {ConductorRetryCommand(goalId)}",
                    leftover,
                    FindLockHoldersForCleanup(leftover),
                    ConductorRetryCommand(goalId),
                    ownedEphemeralCleanup);
            }

            return new GoalWorktreeRemoveResult(completeMessage, null, [], null, ownedEphemeralCleanup);
        }

        var resumeCommand = ConductorRetryCommand(goalId);
        var lockHolders = FindLockHoldersForCleanup(path);
        RecordCleanupNeeded(
            path,
            lockHolders.Count > 0 ? "remove:leftover-directory:lock-held" : "remove:leftover-directory");
        var cleanupBackoff = TryGetCleanupBackoff(path);
        return new GoalWorktreeRemoveResult(
            $"{incompleteMessage} Conductor retry: {resumeCommand}" +
                (cleanupBackoff is null ? string.Empty : $" {FormatCleanupBackoff(cleanupBackoff)}"),
            path,
            lockHolders,
            resumeCommand,
            CleanupBackoff: cleanupBackoff);
    }

    private static string ConductorRetryCommand(GoalId goalId) => $"conduct {Prefix(goalId)} --loop";

    private static void DeleteUntrackedOrchestratorInternalArtifacts(string worktreePath)
    {
        var status = GitCli.Run(worktreePath, "status", "--porcelain");
        if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.Output))
        {
            return;
        }

        foreach (var relativePath in status.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("?? ", StringComparison.Ordinal))
            .SelectMany(GitCli.ParseStatusLinePaths)
            .Where(GitCli.IsOrchestratorInternalArtifactPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(Path.Combine(worktreePath, relativePath));
            var worktreeRoot = Path.GetFullPath(worktreePath);
            if (!fullPath.StartsWith(worktreeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WarnCleanupFailure(fullPath, "remove:internal-artifact-delete", ex);
            }
        }
    }

    private sealed class GoalWorktreeCleanupBudget
    {
        private readonly Stopwatch stopwatch;
        private readonly Func<long>? elapsedMilliseconds;

        private GoalWorktreeCleanupBudget(int totalMilliseconds, Func<long>? elapsedMilliseconds)
        {
            TotalMilliseconds = Math.Max(1, totalMilliseconds);
            this.elapsedMilliseconds = elapsedMilliseconds;
            stopwatch = Stopwatch.StartNew();
        }

        public int TotalMilliseconds { get; }

        public long ElapsedMilliseconds => elapsedMilliseconds?.Invoke() ?? stopwatch.ElapsedMilliseconds;

        public int RemainingMilliseconds =>
            Math.Max(1, TotalMilliseconds - (int)Math.Min(int.MaxValue, ElapsedMilliseconds));

        public bool IsExpired => ElapsedMilliseconds >= TotalMilliseconds;

        public static GoalWorktreeCleanupBudget Start(int totalMilliseconds, Func<long>? elapsedMilliseconds) =>
            new(totalMilliseconds, elapsedMilliseconds);
    }


    private static bool DeleteDirectoryWithRetry(string path)
    {
        return DeleteDirectoryWithReason(path).Succeeded;
    }

    private static GoalWorktreeDeleteResult DeleteDirectoryWithReason(string path)
    {
        if (!Directory.Exists(path))
        {
            return GoalWorktreeDeleteResult.Success;
        }

        var delay = InitialDeleteRetryDelay;
        var attemptedReadOnlyClear = false;
        GoalWorktreeDeleteResult lastFailure = GoalWorktreeDeleteResult.Failed(
            GoalWorktreeDeleteFailureKind.Unknown,
            "Directory deletion failed.");
        for (var attempt = 1; attempt <= DeleteRetryAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return GoalWorktreeDeleteResult.Success;
            }
            catch (Exception ex) when (IsTransientDeleteFailure(ex))
            {
                lastFailure = GoalWorktreeDeleteResult.Failed(ClassifyDeleteFailure(ex), ex.Message);
                if (attempt >= DeleteRetryAttempts)
                {
                    return lastFailure;
                }

                // Sandbox workers leave their checkout read-only; Directory.Delete cannot remove a
                // read-only file (it throws UnauthorizedAccessException), and icacls /reset does not
                // clear the read-only *attribute*. This is the dominant orphan-sweep failure cause, so
                // strip the attribute tree-wide once before retrying. In-use handles (not read-only)
                // fall through to the retry/defer path unchanged.
                if (!attemptedReadOnlyClear && ex is UnauthorizedAccessException)
                {
                    ClearReadOnlyAttributes(path);
                    attemptedReadOnlyClear = true;
                }

                Thread.Sleep(delay);
                delay += delay;
            }
        }

        return lastFailure;
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            var root = new DirectoryInfo(path);
            if ((root.Attributes & FileAttributes.ReadOnly) != 0)
            {
                root.Attributes &= ~FileAttributes.ReadOnly;
            }

            foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next delete attempt surfaces any remaining failure.
        }
    }

    private static bool ClearOrphanDirectory(string path, AgentOrchestratorKernel? kernel, string operation)
    {
        if (!Directory.Exists(path))
        {
            ClearOrphanCleanupBackoff(path);
            return true;
        }

        var cleanupBudget = GoalWorktreeCleanupBudget.Start(GitCli.DefaultTimeoutMilliseconds, CleanupElapsedMilliseconds);
        if (IsCleanupBackedOff(path, operation, out var backoff))
        {
            JournalCleanupBackoffSkip(path, operation + ":skip-backoff", backoff);
            return false;
        }

        var firstDelete = DeleteDirectoryForCleanup(path);
        if (firstDelete.Succeeded)
        {
            ClearOrphanCleanupBackoff(path);
            return true;
        }

        if (firstDelete.FailureKind != GoalWorktreeDeleteFailureKind.AccessDenied)
        {
            WarnCleanupFailure(
                path,
                operation,
                new IOException(BuildCleanupRetryMessage(path, firstDelete.Message ?? "Directory deletion failed.")));
            return RecordOrphanCleanupBackoffWhenLeftover(path, operation + ":delete-failed", operation + ":backoff");
        }

        ReapRecordedWorkerProcesses(kernel, path);
        if (!RunBoundedCleanupStep(path, operation + ":acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, operation, timeout)))
        {
            RecordOrphanCleanupBackoff(path, operation + ":acl-reset-timeout", operation + ":backoff");
            return false;
        }

        var secondDelete = DeleteDirectoryForCleanup(path);
        if (secondDelete.Succeeded)
        {
            ClearOrphanCleanupBackoff(path);
            return true;
        }

        WarnCleanupFailure(
            path,
            operation,
            new IOException(BuildCleanupRetryMessage(
                path,
                secondDelete.Message ?? "Directory deletion failed after ACL reset.")));
        return RecordOrphanCleanupBackoffWhenLeftover(path, operation + ":post-acl-delete-failed", operation + ":backoff");
    }

    private static IEnumerable<string> EnumerateOwnedEphemeralDirectories(string executionDirectory, GoalId goalId)
    {
        var fullGoalId = goalId.Value;
        var prefix = Prefix(goalId);
        var contextPath = Path.Combine(executionDirectory, ".orchestrator-context", fullGoalId);
        if (Directory.Exists(contextPath))
        {
            yield return contextPath;
        }

        foreach (var rootName in new[] { ".t", ".scratch" })
        {
            var root = Path.Combine(executionDirectory, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals(fullGoalId, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                {
                    yield return directory;
                }
            }
        }
    }

    private static bool RecordOrphanCleanupBackoffWhenLeftover(string path, string reason, string warningOperation = "orphan-sweep:backoff")
    {
        if (!Directory.Exists(path))
        {
            ClearOrphanCleanupBackoff(path);
            return true;
        }

        RecordOrphanCleanupBackoff(path, reason, warningOperation);
        return false;
    }

    private static bool RunBoundedCleanupStep(
        string worktreePath,
        string operation,
        GoalWorktreeCleanupBudget cleanupBudget,
        Action<int> action)
    {
        return RunBoundedCleanupStep(worktreePath, operation, cleanupBudget, timeout =>
        {
            action(timeout);
            return true;
        });
    }

    private static bool RunBoundedCleanupStep(
        string worktreePath,
        string operation,
        GoalWorktreeCleanupBudget cleanupBudget,
        Func<int, bool> action)
    {
        if (cleanupBudget.IsExpired)
        {
            WarnCleanupFailure(
                worktreePath,
                operation,
                new TimeoutException($"Cleanup budget exhausted before {operation}."));
            return false;
        }

        try
        {
            if (!action(cleanupBudget.RemainingMilliseconds))
            {
                return false;
            }
        }
        catch (Exception ex) when (IsJanitorialCleanupDeferralException(ex))
        {
            WarnCleanupFailure(worktreePath, operation, ex);
            return false;
        }

        if (!cleanupBudget.IsExpired)
        {
            return true;
        }

        WarnCleanupFailure(
            worktreePath,
            operation,
            new TimeoutException($"Cleanup budget exhausted during {operation}."));
        return false;
    }

    private static bool ResetSandboxAcl(string worktreePath, string operation, int timeoutMilliseconds)
    {
        try
        {
            SandboxAclHelper.ResetSandboxAcl(worktreePath, timeoutMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            WarnCleanupFailure(worktreePath, operation + ":acl-reset", ex);
            return false;
        }
    }

    private static bool IsJanitorialCleanupDeferralException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    private static void ReapRecordedWorkerProcesses(AgentOrchestratorKernel? kernel, string worktreePath)
    {
        if (kernel is null)
        {
            return;
        }

        var normalized = NormalizePath(worktreePath);
        foreach (var process in kernel.Goals
                     .SelectMany(goal => goal.Tasks)
                     .Select(task => task.LastProcess)
                     .OfType<TaskProcessRecord>()
                     .Where(process =>
                         process.IsRunning &&
                         string.Equals(NormalizePath(process.WorkingDirectory), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var processId in process.TrackedProcessIds.Distinct())
            {
                _ = TryKillRecordedProcess(processId);
            }
        }
    }

    private static bool DefaultTryKillRecordedProcess(int processId)
    {
        return WorkerProcessJobs.TryKillOrFallback(processId);
    }

    private static void WarnCleanupFailure(string path, string operation, Exception exception)
    {
        try
        {
            CleanupWarningSink(new GoalWorktreeCleanupWarning(path, operation, exception));
        }
        catch
        {
            // Warning sinks are observational only.
        }
    }

    private static void DefaultCleanupWarningSink(GoalWorktreeCleanupWarning warning)
    {
        Console.Error.WriteLine(
            $"warning: worktree-cleanup path=\"{warning.Path}\" operation=\"{warning.Operation}\" exception=\"{warning.Exception.GetType().Name}\" message=\"{warning.Exception.Message}\"");
    }

    private static bool IsTransientDeleteFailure(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    private static GoalWorktreeDeleteFailureKind ClassifyDeleteFailure(Exception ex) =>
        ex is UnauthorizedAccessException
            ? GoalWorktreeDeleteFailureKind.AccessDenied
            : GoalWorktreeDeleteFailureKind.Transient;

    private static string BuildCleanupRetryMessage(string path, string reason, GoalWorktreeCleanupBackoff? backoff = null)
    {
        var suffix = backoff is null ? string.Empty : $" {FormatCleanupBackoff(backoff)}";
        return $"{reason} Cleanup-needed record persisted in SQLite for conductor retry; path='{path}'.{suffix}";
    }


    private static void DefaultBuildServerShutdown(string worktreePath, int timeoutMilliseconds)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = worktreePath
            };
            startInfo.ArgumentList.Add("build-server");
            startInfo.ArgumentList.Add("shutdown");

            using var process = Process.Start(startInfo);
            if (process is null) return;
            var boundedTimeout = Math.Min(timeoutMilliseconds, (int)BuildServerShutdownTimeout.TotalMilliseconds);
            if (!process.WaitForExit(boundedTimeout))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort; ignore all failures so removal always proceeds.
        }
    }

    private static List<WorktreeLockHolder> FindLockHolders(string path)
    {
        var normalizedPath = NormalizePath(path);
        var processesByPid = new Dictionary<int, string>();

        foreach (var name in LockHolderCandidates)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                    {
                        processesByPid[proc.Id] = proc.ProcessName;
                    }
                }
            }
            catch
            {
                // Skip if enumeration fails for this candidate name.
            }
        }

        if (processesByPid.Count == 0)
        {
            return [];
        }

        var commandLines = ProcessCommandLines.Read(processesByPid.Keys);
        var holders = new List<WorktreeLockHolder>();

        foreach (var (pid, name) in processesByPid)
        {
            commandLines.TryGetValue(pid, out var cmdLine);
            var referencesPath = cmdLine is not null &&
                (cmdLine.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                 cmdLine.Contains(path, StringComparison.OrdinalIgnoreCase));
            var isKnownBuildServer = IsKnownBuildServer(name);

            // Include if we can confirm path reference, or if it's a known build server
            // whose command line we couldn't retrieve (VBCSCompiler/MSBuild are common holders).
            if (referencesPath || (isKnownBuildServer && cmdLine is null))
            {
                holders.Add(new WorktreeLockHolder(pid, name, cmdLine));
            }
        }

        return holders;
    }

    private static bool IsKnownBuildServer(string processName)
    {
        return string.Equals(processName, "VBCSCompiler", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "MSBuild", StringComparison.OrdinalIgnoreCase);
    }

    internal static Dictionary<int, string> ParseWmicListOutput(string output)
    {
        var result = new Dictionary<int, string>();
        var currentBlock = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushBlock()
        {
            if (currentBlock.TryGetValue("ProcessId", out var pidStr) &&
                currentBlock.TryGetValue("CommandLine", out var cmdLine) &&
                int.TryParse(pidStr, out var pid) &&
                !string.IsNullOrWhiteSpace(cmdLine))
            {
                result[pid] = cmdLine.Trim();
            }

            currentBlock.Clear();
        }

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line))
            {
                FlushBlock();
                continue;
            }

            var sep = line.IndexOf('=');
            if (sep > 0)
            {
                currentBlock[line[..sep]] = line[(sep + 1)..];
            }
        }

        FlushBlock();
        return result;
    }

}
