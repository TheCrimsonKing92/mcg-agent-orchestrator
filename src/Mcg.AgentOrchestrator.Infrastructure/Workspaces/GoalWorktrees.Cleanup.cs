using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    public static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            precomputedHasRegisteredWorktree: null,
            precomputedHasBranch: null,
            forceTerminalCleanup: false,
            bypassCleanupBackoff: false,
            hooks ?? new GoalWorktreeCleanupHooks());

    public static GoalWorktreeRemoveResult RemoveTerminal(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel kernel,
        GoalWorktreeCleanupHooks? hooks = null) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            precomputedHasRegisteredWorktree: null,
            precomputedHasBranch: null,
            forceTerminalCleanup: true,
            bypassCleanupBackoff: false,
            hooks ?? new GoalWorktreeCleanupHooks());

    public static GoalWorktreeRemoveResult RemoveTerminalNow(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel kernel,
        GoalWorktreeCleanupHooks? hooks = null) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            precomputedHasRegisteredWorktree: null,
            precomputedHasBranch: null,
            forceTerminalCleanup: true,
            bypassCleanupBackoff: true,
            hooks ?? new GoalWorktreeCleanupHooks());

    public static GoalWorktreeRemoveResult RemoveSupersededTerminal(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel kernel,
        string expectedBranchTip,
        bool hasRegisteredWorktree,
        bool hasBranch,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        if (kernel.GetGoal(goalId).Status != GoalStatus.Completed)
        {
            throw new InvalidOperationException("Superseded branch cleanup requires a completed goal.");
        }

        return Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            hasRegisteredWorktree,
            hasBranch,
            forceTerminalCleanup: false,
            bypassCleanupBackoff: false,
            hooks ?? new GoalWorktreeCleanupHooks(),
            expectedSupersededBranchTip: expectedBranchTip);
    }

    public static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        bool hasRegisteredWorktree,
        bool hasBranch,
        GoalWorktreeCleanupHooks? hooks = null) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            hasRegisteredWorktree,
            hasBranch,
            forceTerminalCleanup: false,
            bypassCleanupBackoff: false,
            hooks ?? new GoalWorktreeCleanupHooks());

    public static GoalWorktreeRemoveResult RemoveTerminal(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel kernel,
        bool hasRegisteredWorktree,
        bool hasBranch,
        GoalWorktreeCleanupHooks? hooks = null) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            GitCli.DefaultTimeoutMilliseconds,
            hasRegisteredWorktree,
            hasBranch,
            forceTerminalCleanup: true,
            bypassCleanupBackoff: false,
            hooks ?? new GoalWorktreeCleanupHooks());

    public static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        int gitTimeoutMilliseconds,
        bool forceTerminalCleanup = false,
        GoalWorktreeCleanupHooks? hooks = null) =>
        Remove(
            executionDirectory,
            goalId,
            kernel,
            gitTimeoutMilliseconds,
            precomputedHasRegisteredWorktree: null,
            precomputedHasBranch: null,
            forceTerminalCleanup,
            bypassCleanupBackoff: false,
            hooks ?? new GoalWorktreeCleanupHooks());

    private static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        int gitTimeoutMilliseconds,
        bool? precomputedHasRegisteredWorktree,
        bool? precomputedHasBranch,
        bool forceTerminalCleanup,
        bool bypassCleanupBackoff,
        GoalWorktreeCleanupHooks hooks,
        string? expectedSupersededBranchTip = null)
    {
        var cleanupBudget = GoalWorktreeCleanupBudget.Start(gitTimeoutMilliseconds, hooks.CleanupElapsedMilliseconds());
        var path = WorktreePath(executionDirectory, goalId);
        if (forceTerminalCleanup &&
            !CanDirectDeleteTerminalWorktree(executionDirectory, path, kernel, out var terminalSafetyFailure))
        {
            return new GoalWorktreeRemoveResult(
                $"Terminal worktree cleanup refused unsafe target '{path}': {terminalSafetyFailure}.",
                path,
                Directory.Exists(path) ? hooks.FindLockHoldersForCleanup(path) : [],
                ConductorRetryCommand(goalId));
        }

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
            var earlyOwnedEphemeralCleanup = SweepOwnedEphemeralDirectories(executionDirectory, goalId, kernel, hooks);
            earlyOwnedEphemeralCleanup = SweepGoalBuildArtifacts(executionDirectory, goalId, earlyOwnedEphemeralCleanup, hooks);
            return CompleteOrDeferredRemoveResult(
                path,
                goalId,
                "Workspace already clean; nothing to remove.",
                "Workspace already clean, but leftover cleanup is incomplete.",
                earlyOwnedEphemeralCleanup,
                hooks);
        }

        var wasAlreadyUnregistered = !hasRegisteredWorktree;

        if (!bypassCleanupBackoff &&
            hasLeftoverDirectory &&
            IsCleanupBackedOff(path, "remove", out var backoff, hooks: hooks))
        {
            var lockHolders = hooks.FindLockHoldersForCleanup(path);
            if ((!IsLockHeldCleanupNeededReason(backoff.Reason) &&
                    !IsBudgetExhaustedCleanupNeededReason(backoff.Reason)) ||
                lockHolders.Count > 0)
            {
                var detail = ToCleanupBackoff(backoff, hooks);
                WarnCleanupFailure(
                    path,
                    "remove:skip-backoff",
                    new IOException(BuildCleanupRetryMessage(path, backoff.Reason, detail)),
                    hooks);
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred by cleanup-needed backoff for {path}. {FormatCleanupBackoff(detail)}",
                    path,
                    lockHolders,
                    ConductorRetryCommand(goalId),
                    CleanupBackoff: detail);
            }
        }
        else if (!bypassCleanupBackoff &&
            !hasLeftoverDirectory &&
            hasBranch &&
            IsCleanupBackedOff(path, "remove", out var branchBackoff, hooks: hooks))
        {
            var detail = ToCleanupBackoff(branchBackoff, hooks);
            WarnCleanupFailure(
                path,
                "remove:skip-backoff",
                new IOException(BuildCleanupRetryMessage(path, branchBackoff.Reason, detail)),
                hooks);
            return new GoalWorktreeRemoveResult(
                $"Workspace cleanup deferred by cleanup-needed backoff for branch {branch}. {FormatCleanupBackoff(detail)}",
                path,
                [],
                ConductorRetryCommand(goalId),
                CleanupBackoff: detail);
        }

        if (hasRegisteredWorktree)
        {
            DeleteUntrackedOrchestratorInternalArtifacts(path, hooks);
            var removal = hooks.RunWorktreeRemove(
                executionDirectory,
                cleanupBudget.RemainingMilliseconds,
                forceTerminalCleanup ? ToExtendedLengthPath(path) : path,
                forceTerminalCleanup);
            if (removal.ExitCode != 0 && IsRegisteredWorktree(executionDirectory, path, cleanupBudget.RemainingMilliseconds))
            {
                var safetyFailure = "terminal cleanup was not authorized";
                if (!forceTerminalCleanup ||
                    !CanDirectDeleteTerminalWorktree(executionDirectory, path, kernel, out safetyFailure))
                {
                    var failureReason = forceTerminalCleanup
                        ? "remove:unsafe-direct-delete-blocked"
                        : "remove:worktree-remove-failed";
                    RecordCleanupNeeded(path, failureReason, goalId: goalId, hooks: hooks);
                    var detail = TryGetCleanupBackoff(path, hooks);
                    return new GoalWorktreeRemoveResult(
                        forceTerminalCleanup
                            ? $"Workspace cleanup blocked after git worktree remove failed because direct deletion was unsafe: {safetyFailure}." +
                                (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}")
                            : $"Workspace cleanup deferred because git worktree remove failed for {path}: {removal.Error.Trim()} Commit, discard, or recover its changes; conductor cleanup will retry after the worktree is clean." +
                                (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}"),
                        path,
                        hooks.FindLockHoldersForCleanup(path),
                        ConductorRetryCommand(goalId),
                        CleanupBackoff: detail);
                }

                ReapRecordedWorkerProcesses(kernel, path, hooks);
                if (!RunBoundedCleanupStep(path, "remove:fallback-build-server-shutdown", cleanupBudget, timeout => hooks.BuildServerShutdown(path, timeout), hooks) ||
                    !RunBoundedCleanupStep(path, "remove:fallback-acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, "remove:fallback", timeout, hooks), hooks) ||
                    !hooks.DeleteDirectory(path))
                {
                    RecordCleanupNeeded(path, "remove:direct-delete-failed", goalId: goalId, hooks: hooks);
                    var detail = TryGetCleanupBackoff(path, hooks);
                    return new GoalWorktreeRemoveResult(
                        $"Workspace cleanup deferred because git removal and long-path filesystem fallback both failed for {path}." +
                            (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}"),
                        path,
                        hooks.FindLockHoldersForCleanup(path),
                        ConductorRetryCommand(goalId),
                        CleanupBackoff: detail);
                }

                _ = hooks.RunWorktreePrune(
                    executionDirectory,
                    cleanupBudget.RemainingMilliseconds,
                    true);
                if (IsRegisteredWorktree(executionDirectory, path, cleanupBudget.RemainingMilliseconds))
                {
                    RecordCleanupNeeded(path, "remove:worktree-prune-failed", goalId: goalId, hooks: hooks);
                    var detail = TryGetCleanupBackoff(path, hooks);
                    return new GoalWorktreeRemoveResult(
                        $"Workspace directory was removed, but git still registers worktree {path} after prune." +
                            (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}"),
                        path,
                        [],
                        ConductorRetryCommand(goalId),
                        CleanupBackoff: detail);
                }
            }
            else if (removal.ExitCode != 0)
            {
                _ = hooks.RunWorktreePrune(
                    executionDirectory,
                    cleanupBudget.RemainingMilliseconds,
                    true);
            }
        }
        else
        {
            // Worktree already unregistered; prune any stale tracking entries left by a prior
            // partial removal so git's internal state is consistent before we finish cleanup.
            hooks.RunWorktreePrune(executionDirectory, cleanupBudget.RemainingMilliseconds, false);
        }

        GitCli.GitResult? branchRemoval = null;
        if (hasBranch)
        {
            if (!forceTerminalCleanup &&
                expectedSupersededBranchTip is null &&
                !IsBranchAncestorOfHead(executionDirectory, branch, cleanupBudget.RemainingMilliseconds))
            {
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup aborted; branch {branch} kept because it has unmerged commits at deletion time.",
                    path,
                    Directory.Exists(path) ? hooks.FindLockHoldersForCleanup(path) : [],
                    ConductorRetryCommand(goalId));
            }

            branchRemoval = expectedSupersededBranchTip is not null
                ? GitCli.Run(
                    executionDirectory,
                    cleanupBudget.RemainingMilliseconds,
                    "update-ref",
                    "-d",
                    $"refs/heads/{branch}",
                    expectedSupersededBranchTip)
                : GitCli.Run(
                    executionDirectory,
                    cleanupBudget.RemainingMilliseconds,
                    "branch",
                    forceTerminalCleanup ? "-D" : "-d",
                    branch);
        }

        if (Directory.Exists(path))
        {
            ReapRecordedWorkerProcesses(kernel, path, hooks);
            if (!RunBoundedCleanupStep(path, "remove:build-server-shutdown", cleanupBudget, timeout => hooks.BuildServerShutdown(path, timeout), hooks) ||
                !RunBoundedCleanupStep(path, "remove:acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, "remove", timeout, hooks), hooks))
            {
                RecordCleanupNeeded(path, "remove:cleanup-budget-exhausted", goalId: goalId, hooks: hooks);
                var detail = TryGetCleanupBackoff(path, hooks);
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred because cleanup budget was exhausted before deleting {path}." +
                        (detail is null ? string.Empty : $" {FormatCleanupBackoff(detail)}"),
                    path,
                    hooks.FindLockHoldersForCleanup(path),
                    ConductorRetryCommand(goalId),
                    CleanupBackoff: detail);
            }
        }

        if (Directory.Exists(path) && !hooks.DeleteDirectory(path))
        {
            WarnCleanupFailure(path, "remove", new IOException("Directory deletion failed after ACL reset."), hooks);
        }

        if (!hasBranch || branchRemoval is { ExitCode: 0 })
        {
            var completeMessage = branchRemoval is { ExitCode: 0 }
                ? expectedSupersededBranchTip is not null
                    ? $"Removed workspace and superseded branch {branch}."
                    : $"Removed workspace and merged branch {branch}."
                : "Removed workspace.";
            var incompleteMessage = branchRemoval is { ExitCode: 0 }
                ? expectedSupersededBranchTip is not null
                    ? $"Removed workspace and superseded branch {branch}, but leftover directory cleanup is incomplete."
                    : $"Removed workspace and merged branch {branch}, but leftover directory cleanup is incomplete."
                : "Removed workspace, but leftover directory cleanup is incomplete.";
            var ephemeralCleanup = SweepOwnedEphemeralDirectories(executionDirectory, goalId, kernel, hooks);
            ephemeralCleanup = SweepGoalBuildArtifacts(executionDirectory, goalId, ephemeralCleanup, hooks);
            return CompleteOrDeferredRemoveResult(
                path,
                goalId,
                completeMessage,
                incompleteMessage,
                ephemeralCleanup,
                hooks);
        }

        var ownedEphemeralCleanup = SweepOwnedEphemeralDirectories(executionDirectory, goalId, kernel, hooks);
        ownedEphemeralCleanup = SweepGoalBuildArtifacts(executionDirectory, goalId, ownedEphemeralCleanup, hooks);
        RecordCleanupNeeded(path, "remove:branch-delete-failed", goalId: goalId, hooks: hooks);
        var branchCleanupBackoff = TryGetCleanupBackoff(path, hooks);
        return new GoalWorktreeRemoveResult(
            $"Removed workspace; branch {branch} kept because branch deletion failed. Conductor retry: {ConductorRetryCommand(goalId)}" +
                (branchCleanupBackoff is null ? string.Empty : $" {FormatCleanupBackoff(branchCleanupBackoff)}"),
            path,
            [],
            ConductorRetryCommand(goalId),
            ownedEphemeralCleanup,
            branchCleanupBackoff);
    }

    public static GoalWorktreeSweepResult SweepOrphanedWorktrees(
        string executionDirectory,
        AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        hooks ??= new GoalWorktreeCleanupHooks();
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

            if (ClearOrphanDirectory(directory, kernel, "orphan-sweep", hooks))
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
        AgentOrchestratorKernel? kernel = null,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        hooks ??= new GoalWorktreeCleanupHooks();
        var root = Path.GetFullPath(executionDirectory);
        var removed = 0;
        var leftovers = new List<string>();

        foreach (var path in EnumerateOwnedEphemeralDirectories(root, goalId))
        {
            if (ClearOrphanDirectory(path, kernel, "owned-ephemeral-sweep", hooks))
            {
                removed++;
            }
            else if (Directory.Exists(path))
            {
                leftovers.Add(path);
            }
        }

        TryDeleteEmptyDirectory(Path.Combine(root, ".t"), hooks);
        TryDeleteEmptyDirectory(Path.Combine(root, ".scratch"), hooks);

        return new GoalOwnedEphemeralSweepResult(removed, leftovers);
    }

    private static GoalOwnedEphemeralSweepResult SweepGoalBuildArtifacts(
        string executionDirectory,
        GoalId goalId,
        GoalOwnedEphemeralSweepResult ownedEphemeralCleanup,
        GoalWorktreeCleanupHooks hooks)
    {
        var storageRoot = hooks.BuildStorageRoot ?? DotnetBuildEnvironmentManager.CaptureStorageRoot();
        var root = DotnetBuildEnvironmentManager.GoalRoot(goalId, storageRoot);
        if (!Directory.Exists(root))
        {
            ClearCleanupNeeded(root, executionDirectory, hooks);
            return ownedEphemeralCleanup;
        }

        var leftovers = ownedEphemeralCleanup.LeftoverPaths.ToList();
        if (IsCleanupBackedOff(root, "remove", out var backoff, executionDirectory, hooks))
        {
            var lockHolders = hooks.FindLockHoldersForCleanup(root);
            if (!IsLockHeldCleanupNeededReason(backoff.Reason) || lockHolders.Count > 0)
            {
                WarnCleanupFailure(
                    root,
                    "remove:goal-artifacts:skip-backoff",
                    new IOException(BuildCleanupRetryMessage(root, backoff.Reason)),
                    hooks);
                leftovers.Add(root);
                return new GoalOwnedEphemeralSweepResult(ownedEphemeralCleanup.RemovedCount, leftovers);
            }

            ClearCleanupNeeded(root, executionDirectory, hooks);
        }

        if (DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId, storageRoot))
        {
            ClearCleanupNeeded(root, executionDirectory, hooks);
            return ownedEphemeralCleanup with { RemovedCount = ownedEphemeralCleanup.RemovedCount + 1 };
        }

        WarnCleanupFailure(
            root,
            "remove:goal-artifacts",
            new IOException(BuildCleanupRetryMessage(root, "remove:goal-artifacts")),
            hooks);
        var failureReason = hooks.FindLockHoldersForCleanup(root).Count > 0
            ? "remove:goal-artifacts:lock-held"
            : "remove:goal-artifacts";
        RecordCleanupNeeded(root, failureReason, executionDirectory, hooks: hooks);
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
        GoalOwnedEphemeralSweepResult? ownedEphemeralCleanup,
        GoalWorktreeCleanupHooks hooks)
    {
        if (!Directory.Exists(path))
        {
            ClearCleanupNeeded(path, hooks: hooks);
            if (ownedEphemeralCleanup is { IsComplete: false })
            {
                var leftover = ownedEphemeralCleanup.LeftoverPaths[0];
                return new GoalWorktreeRemoveResult(
                    $"{completeMessage} Owned ephemeral cleanup is incomplete. Conductor retry: {ConductorRetryCommand(goalId)}",
                    leftover,
                    hooks.FindLockHoldersForCleanup(leftover),
                    ConductorRetryCommand(goalId),
                    ownedEphemeralCleanup);
            }

            return new GoalWorktreeRemoveResult(completeMessage, null, [], null, ownedEphemeralCleanup);
        }

        var resumeCommand = ConductorRetryCommand(goalId);
        var lockHolders = hooks.FindLockHoldersForCleanup(path);
        RecordCleanupNeeded(
            path,
            lockHolders.Count > 0 ? "remove:leftover-directory:lock-held" : "remove:leftover-directory",
            goalId: goalId,
            hooks: hooks);
        var cleanupBackoff = TryGetCleanupBackoff(path, hooks);
        return new GoalWorktreeRemoveResult(
            $"{incompleteMessage} Conductor retry: {resumeCommand}" +
                (cleanupBackoff is null ? string.Empty : $" {FormatCleanupBackoff(cleanupBackoff)}"),
            path,
            lockHolders,
            resumeCommand,
            CleanupBackoff: cleanupBackoff);
    }

    private static string ConductorRetryCommand(GoalId goalId) => $"conduct {Prefix(goalId)} --loop";

    internal static GitCli.GitResult DefaultRunWorktreeRemove(
        string executionDirectory,
        int timeoutMilliseconds,
        string path,
        bool forceTerminalCleanup) =>
        forceTerminalCleanup
            ? GitCli.Run(
                executionDirectory,
                timeoutMilliseconds,
                "worktree",
                "remove",
                "--force",
                path)
            : GitCli.Run(executionDirectory, timeoutMilliseconds, "worktree", "remove", path);

    internal static GitCli.GitResult DefaultRunWorktreePrune(
        string executionDirectory,
        int timeoutMilliseconds,
        bool expireNow) =>
        expireNow
            ? GitCli.Run(executionDirectory, timeoutMilliseconds, "worktree", "prune", "--expire", "now")
            : GitCli.Run(executionDirectory, timeoutMilliseconds, "worktree", "prune");

    private static void DeleteUntrackedOrchestratorInternalArtifacts(
        string worktreePath,
        GoalWorktreeCleanupHooks hooks)
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
                WarnCleanupFailure(fullPath, "remove:internal-artifact-delete", ex, hooks);
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


    internal static bool DeleteDirectoryWithRetry(string path)
    {
        return DeleteDirectoryWithReason(path).Succeeded;
    }

    internal static GoalWorktreeDeleteResult DeleteDirectoryWithReason(string path)
    {
        var deletionPath = ToExtendedLengthPath(path);
        if (!Directory.Exists(deletionPath))
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
                Directory.Delete(deletionPath, recursive: true);
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
                    ClearReadOnlyAttributes(deletionPath);
                    attemptedReadOnlyClear = true;
                }

                Thread.Sleep(delay);
                delay += delay;
            }
        }

        return lastFailure;
    }

    private static string ToExtendedLengthPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static bool CanDirectDeleteTerminalWorktree(
        string executionDirectory,
        string path,
        AgentOrchestratorKernel? kernel,
        out string failure)
    {
        var worktreesRoot = NormalizePath(Path.Combine(Path.GetFullPath(executionDirectory), DirectoryName));
        var normalizedPath = NormalizePath(path);
        if (!normalizedPath.StartsWith(worktreesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"target '{normalizedPath}' is not strictly under '{worktreesRoot}'";
            return false;
        }

        if (kernel is not null)
        {
            var activeGoal = kernel.Goals.FirstOrDefault(goal =>
                !IsTerminalCleanupStatus(goal.Status) &&
                NormalizePath(WorktreePath(executionDirectory, goal.Id))
                    .Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (activeGoal is not null)
            {
                failure = $"target belongs to non-terminal goal {Prefix(activeGoal.Id)} ({activeGoal.Status})";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static bool IsTerminalCleanupStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

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

    private static bool ClearOrphanDirectory(
        string path,
        AgentOrchestratorKernel? kernel,
        string operation,
        GoalWorktreeCleanupHooks hooks)
    {
        if (!Directory.Exists(path))
        {
            ClearOrphanCleanupBackoff(path, hooks: hooks);
            return true;
        }

        var cleanupBudget = GoalWorktreeCleanupBudget.Start(GitCli.DefaultTimeoutMilliseconds, hooks.CleanupElapsedMilliseconds());
        if (IsCleanupBackedOff(path, operation, out var backoff, hooks: hooks))
        {
            JournalCleanupBackoffSkip(path, operation + ":skip-backoff", backoff, hooks);
            return false;
        }

        var firstDelete = hooks.DeleteDirectoryForCleanup(path);
        if (firstDelete.Succeeded)
        {
            ClearOrphanCleanupBackoff(path, hooks: hooks);
            return true;
        }

        if (firstDelete.FailureKind != GoalWorktreeDeleteFailureKind.AccessDenied)
        {
            WarnCleanupFailure(
                path,
                operation,
                new IOException(BuildCleanupRetryMessage(path, firstDelete.Message ?? "Directory deletion failed.")),
                hooks);
            return RecordOrphanCleanupBackoffWhenLeftover(path, operation + ":delete-failed", operation + ":backoff", hooks);
        }

        ReapRecordedWorkerProcesses(kernel, path, hooks);
        if (!RunBoundedCleanupStep(path, operation + ":acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, operation, timeout, hooks), hooks))
        {
            RecordOrphanCleanupBackoff(
                path,
                operation + ":acl-reset-timeout",
                operation + ":backoff",
                hooks: hooks);
            return false;
        }

        var secondDelete = hooks.DeleteDirectoryForCleanup(path);
        if (secondDelete.Succeeded)
        {
            ClearOrphanCleanupBackoff(path, hooks: hooks);
            return true;
        }

        WarnCleanupFailure(
            path,
            operation,
            new IOException(BuildCleanupRetryMessage(
                path,
                secondDelete.Message ?? "Directory deletion failed after ACL reset.")),
            hooks);
        return RecordOrphanCleanupBackoffWhenLeftover(path, operation + ":post-acl-delete-failed", operation + ":backoff", hooks);
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

    private static bool RecordOrphanCleanupBackoffWhenLeftover(
        string path,
        string reason,
        string warningOperation,
        GoalWorktreeCleanupHooks hooks)
    {
        if (!Directory.Exists(path))
        {
            ClearOrphanCleanupBackoff(path, hooks: hooks);
            return true;
        }

        RecordOrphanCleanupBackoff(path, reason, warningOperation, hooks: hooks);
        return false;
    }

    private static bool RunBoundedCleanupStep(
        string worktreePath,
        string operation,
        GoalWorktreeCleanupBudget cleanupBudget,
        Action<int> action,
        GoalWorktreeCleanupHooks hooks)
    {
        return RunBoundedCleanupStep(worktreePath, operation, cleanupBudget, timeout =>
        {
            action(timeout);
            return true;
        }, hooks);
    }

    private static bool RunBoundedCleanupStep(
        string worktreePath,
        string operation,
        GoalWorktreeCleanupBudget cleanupBudget,
        Func<int, bool> action,
        GoalWorktreeCleanupHooks hooks)
    {
        if (cleanupBudget.IsExpired)
        {
            WarnCleanupFailure(
                worktreePath,
                operation,
                new TimeoutException($"Cleanup budget exhausted before {operation}."),
                hooks);
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
            WarnCleanupFailure(worktreePath, operation, ex, hooks);
            return false;
        }

        if (!cleanupBudget.IsExpired)
        {
            return true;
        }

        WarnCleanupFailure(
            worktreePath,
            operation,
            new TimeoutException($"Cleanup budget exhausted during {operation}."),
            hooks);
        return false;
    }

    private static bool ResetSandboxAcl(
        string worktreePath,
        string operation,
        int timeoutMilliseconds,
        GoalWorktreeCleanupHooks hooks)
    {
        try
        {
            hooks.ResetSandboxAcl(worktreePath, timeoutMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            WarnCleanupFailure(worktreePath, operation + ":acl-reset", ex, hooks);
            return false;
        }
    }

    private static bool IsJanitorialCleanupDeferralException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    private static void ReapRecordedWorkerProcesses(
        AgentOrchestratorKernel? kernel,
        string worktreePath,
        GoalWorktreeCleanupHooks hooks)
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
                _ = hooks.TryKillRecordedProcess(processId);
            }
        }
    }

    internal static bool DefaultTryKillRecordedProcess(int processId)
    {
        return WorkerProcessJobs.TryKillOrFallback(processId);
    }

    private static void WarnCleanupFailure(
        string path,
        string operation,
        Exception exception,
        GoalWorktreeCleanupHooks hooks)
    {
        try
        {
            hooks.CleanupWarningSink(new GoalWorktreeCleanupWarning(path, operation, exception));
        }
        catch
        {
            // Warning sinks are observational only.
        }
    }

    internal static void DefaultCleanupWarningSink(GoalWorktreeCleanupWarning warning)
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


    internal static void DefaultBuildServerShutdown(string worktreePath, int timeoutMilliseconds)
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

    internal static List<WorktreeLockHolder> FindLockHolders(
        string path,
        Func<IEnumerable<string>, ProcessCommandLineSnapshot>? processCommandLineSnapshot = null)
    {
        var normalizedPath = NormalizePath(path);
        var snapshot = (processCommandLineSnapshot ?? ProcessCommandLines.SnapshotByNames)(LockHolderCandidates);
        var holders = new List<WorktreeLockHolder>();
        if (snapshot.Failure is { } failure)
        {
            holders.Add(new WorktreeLockHolder(
                0,
                "process-inspection-unavailable",
                $"status={failure.Status} nativeError={failure.NativeError} operation={failure.Operation}"));
            return holders;
        }

        foreach (var (pid, record) in snapshot.Records)
        {
            if (record.Status is ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled)
            {
                continue;
            }

            var name = record.Name;
            var cmdLine = record.Status == ProcessInspectionStatus.Available
                ? record.CommandLine
                : null;
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

}
