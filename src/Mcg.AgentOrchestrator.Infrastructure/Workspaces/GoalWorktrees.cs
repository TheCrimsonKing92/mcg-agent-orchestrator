using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalWorktreeMergeResult(
    bool FastForwarded,
    string BranchName,
    string Message,
    string? SuggestedCommand);

public enum GoalWorktreeRebaseStatus
{
    Rebased,
    AlreadyFastForwardable,
    MissingBranch,
    MissingWorktree,
    DirtyWorktree,
    Conflict,
    Failed
}

public sealed record GoalWorktreeRebaseResult(
    GoalWorktreeRebaseStatus Status,
    string BranchName,
    string Message,
    IReadOnlyList<string> ConflictFiles,
    string? SuggestedCommand)
{
    public bool UpdatedBranch => Status == GoalWorktreeRebaseStatus.Rebased ||
        Status == GoalWorktreeRebaseStatus.AlreadyFastForwardable;
}

public sealed record WorktreeLockHolder(int ProcessId, string ProcessName, string? CommandLine);

public sealed record GoalWorktreeRemoveResult(
    string Message,
    string? LeftoverPath,
    IReadOnlyList<WorktreeLockHolder> LockHolders,
    string? ResumeCommand)
{
    public bool IsComplete => LeftoverPath is null;
}

public sealed record GoalWorktreeCleanupWarning(string Path, string Operation, Exception Exception);

internal enum GoalWorktreeDeleteFailureKind
{
    None,
    AccessDenied,
    Transient,
    Unknown
}

internal sealed record GoalWorktreeDeleteResult(
    bool Succeeded,
    GoalWorktreeDeleteFailureKind FailureKind,
    string? Message)
{
    public static GoalWorktreeDeleteResult Success { get; } =
        new(true, GoalWorktreeDeleteFailureKind.None, null);

    public static GoalWorktreeDeleteResult Failed(GoalWorktreeDeleteFailureKind failureKind, string? message) =>
        new(false, failureKind, message);
}

public sealed record GoalWorktreeSweepResult(int RemovedCount, IReadOnlyList<string> LeftoverPaths);

public sealed record GoalWorktreeCleanupOptions(TimeSpan SweepInterval)
{
    public static GoalWorktreeCleanupOptions Default { get; } = new(TimeSpan.FromMinutes(5));
}

public sealed record GoalWorktreeGitMetadataAccess(
    string WorktreePath,
    string IndexLockPath,
    string CurrentIdentity,
    bool CurrentProcessCanWriteIndexLock,
    bool WorkerCanWriteIndexLock,
    string WorkerWriteDisposition,
    string CommitContract,
    string? Error);

public interface ISandboxAclHelper
{
    void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds);
}

public sealed class WindowsSandboxAclHelper : ISandboxAclHelper
{
    public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sandboxPath = Path.Combine(worktreePath, ".mcg-sandbox");
        if (!Directory.Exists(sandboxPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "icacls",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(sandboxPath);
        startInfo.ArgumentList.Add("/reset");
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");
        startInfo.ArgumentList.Add("/Q");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }
}

public sealed class NoOpSandboxAclHelper : ISandboxAclHelper
{
    public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds) { }
}

public static class GoalWorktrees
{
    public const string DirectoryName = ".orchestrator-worktrees";
    private static readonly TimeSpan InitialDeleteRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int DeleteRetryAttempts = 6;
    private static readonly string[] LockHolderCandidates =
        ["dotnet", "VBCSCompiler", "MSBuild", "claude", "codex", "node", "powershell", "pwsh"];
    private static readonly TimeSpan BuildServerShutdownTimeout = TimeSpan.FromSeconds(10);
    private const string CleanupBackoffTableSql = """
        CREATE TABLE IF NOT EXISTS worktree_cleanup_backoff (
            path TEXT PRIMARY KEY NOT NULL,
            skip_until_utc TEXT NOT NULL,
            reason TEXT NOT NULL
        );
        """;

    // Injectable for testing: called best-effort before directory deletion to release any
    // VBCSCompiler/Roslyn/MSBuild file handles held by the acceptance build server.
    internal static Action<string, int> BuildServerShutdown = DefaultBuildServerShutdown;
    internal static ISandboxAclHelper SandboxAclHelper { get; set; } =
        OperatingSystem.IsWindows() ? new WindowsSandboxAclHelper() : new NoOpSandboxAclHelper();
    internal static Func<int, bool> TryKillRecordedProcess { get; set; } = DefaultTryKillRecordedProcess;
    internal static Func<string, bool> DeleteDirectory { get; set; } = DeleteDirectoryWithRetry;
    internal static Func<string, GoalWorktreeDeleteResult> DeleteDirectoryForCleanup { get; set; } = DeleteDirectoryWithReason;
    internal static Func<string, IReadOnlyList<WorktreeLockHolder>> FindLockHoldersForCleanup { get; set; } = FindLockHolders;
    internal static Action<GoalWorktreeCleanupWarning> CleanupWarningSink { get; set; } = DefaultCleanupWarningSink;
    internal static Func<long>? CleanupElapsedMilliseconds { get; set; }
    internal static Func<DateTimeOffset> CleanupUtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    internal static TimeSpan CleanupBackoffDuration { get; set; } = TimeSpan.FromMinutes(30);

    public static string BranchName(GoalId goalId) => $"goal/{Prefix(goalId)}";

    public static string WorktreePath(string executionDirectory, GoalId goalId)
    {
        return Path.Combine(Path.GetFullPath(executionDirectory), DirectoryName, Prefix(goalId));
    }

    public static string? TryResolve(string executionDirectory, GoalId goalId)
    {
        var path = WorktreePath(executionDirectory, goalId);
        // A linked worktree has a .git file (not directory) pointing at the main repository.
        return File.Exists(Path.Combine(path, ".git")) ? path : null;
    }

    public static IReadOnlyDictionary<GoalId, string> ResolveAll(
        string executionDirectory,
        IEnumerable<GoalId> goalIds)
    {
        var root = Path.Combine(Path.GetFullPath(executionDirectory), DirectoryName);
        if (!Directory.Exists(root))
        {
            return new Dictionary<GoalId, string>();
        }

        var linkedWorktreePaths = Directory.EnumerateDirectories(root)
            .Where(path => File.Exists(Path.Combine(path, ".git")))
            .ToDictionary(
                path => Path.GetFileName(path),
                path => path,
                StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<GoalId, string>();
        foreach (var goalId in goalIds)
        {
            if (linkedWorktreePaths.TryGetValue(Prefix(goalId), out var path))
            {
                resolved[goalId] = path;
            }
        }

        return resolved;
    }

    public static GoalWorktreeGitMetadataAccess InspectGitMetadataAccess(
        string worktreePath,
        WorkerSandboxOptions? sandboxOptions = null)
    {
        var fullWorktreePath = Path.GetFullPath(worktreePath);
        var indexLockPath = ResolveGitPath(fullWorktreePath, "index.lock");
        var currentIdentity = Environment.UserName;
        var currentCanWrite = false;
        string? error = null;

        if (string.IsNullOrWhiteSpace(indexLockPath))
        {
            indexLockPath = Path.Combine(fullWorktreePath, ".git", "index.lock");
            error = "git rev-parse --git-path index.lock failed";
        }
        else
        {
            currentCanWrite = TryProbeCreateFile(indexLockPath, out error);
        }

        var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
        var lowIntegrityWorker = sandbox.Enabled && OperatingSystem.IsWindows();
        var workerCanWrite = !lowIntegrityWorker && currentCanWrite;
        var disposition = lowIntegrityWorker
            ? "blocked-by-low-integrity"
            : currentCanWrite ? "same-as-orchestrator" : "unavailable";

        return new GoalWorktreeGitMetadataAccess(
            fullWorktreePath,
            indexLockPath,
            currentIdentity,
            currentCanWrite,
            workerCanWrite,
            disposition,
            "workers edit worktree files; orchestrator commits verified dirty edits on behalf",
            error);
    }

    public static string Ensure(string executionDirectory, GoalId goalId)
    {
        var existing = TryResolve(executionDirectory, goalId);
        if (existing is not null)
        {
            FastForwardToBaseIfStale(executionDirectory, existing);
            return existing;
        }

        RequireGitWorkTree(executionDirectory);
        EnsureWorktreeRootIgnored(executionDirectory);

        var path = WorktreePath(executionDirectory, goalId);
        var branch = BranchName(goalId);
        var branchExists = BranchExists(executionDirectory, branch);
        var result = AddWorktree(executionDirectory, path, branch, branchExists);
        if (result.ExitCode != 0 && WorktreeAddFailedBecausePathExists(result, path))
        {
            ClearOrphanDirectory(path, kernel: null, operation: "worktree-add-retry");
            result = AddWorktree(executionDirectory, path, branch, BranchExists(executionDirectory, branch));
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create goal workspace at '{path}': {result.Error}");
        }

        FastForwardToBaseIfStale(executionDirectory, path);
        return path;
    }

    private static string ResolveGitPath(string workingDirectory, string path)
    {
        var result = GitCli.Run(workingDirectory, "rev-parse", "--git-path", path);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Output))
        {
            return string.Empty;
        }

        var resolved = result.Output.Trim();
        return Path.IsPathFullyQualified(resolved)
            ? Path.GetFullPath(resolved)
            : Path.GetFullPath(Path.Combine(workingDirectory, resolved));
    }

    private static bool TryProbeCreateFile(string path, out string? error)
    {
        error = null;
        if (File.Exists(path))
        {
            error = "index.lock already exists";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Brings an UNDRIVEN goal worktree up to the base branch (main) before a worker runs, so workers
    // never build on a stale base — building on an old main and then failing to merge over fixes that
    // landed meanwhile is the stale-base conflict that otherwise forces a manual re-dispatch. Only
    // fast-forwards: a branch that has diverged (its own commits ahead of base) or a dirty worktree is
    // left untouched, because that is real in-progress work reconciled by TryRebaseOntoMain/acceptance,
    // not here. Best-effort: never blocks dispatch.
    private static void FastForwardToBaseIfStale(string executionDirectory, string worktreePath)
    {
        try
        {
            if (GitCli.IsWorktreeDirty(worktreePath))
            {
                return;
            }

            var baseBranch = GetCurrentBranchName(executionDirectory) ?? "main";
            // --ff-only fast-forwards when the branch is strictly behind base, is a no-op when already
            // up to date, and fails harmlessly (branch left as-is) when the branch has diverged with
            // its own commits — exactly the "only advance undriven branches" semantics we want.
            GitCli.Run(worktreePath, "merge", "--ff-only", baseBranch);
        }
        catch
        {
            // Freshness is best-effort; a worker building on a slightly stale base still goes through
            // the acceptance gate, which catches a genuine conflict.
        }
    }

    public static GoalWorktreeRemoveResult Remove(string executionDirectory, GoalId goalId, AgentOrchestratorKernel? kernel = null) =>
        Remove(executionDirectory, goalId, kernel, GitCli.DefaultTimeoutMilliseconds);

    public static GoalWorktreeRemoveResult Remove(
        string executionDirectory,
        GoalId goalId,
        AgentOrchestratorKernel? kernel,
        int gitTimeoutMilliseconds)
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

        var hasRegisteredWorktree = IsRegisteredWorktree(executionDirectory, path, cleanupBudget.RemainingMilliseconds);
        var hasLeftoverDirectory = Directory.Exists(path);
        var branch = BranchName(goalId);
        var hasBranch = BranchExists(executionDirectory, branch, cleanupBudget.RemainingMilliseconds);

        if (!hasRegisteredWorktree && !hasLeftoverDirectory && !hasBranch)
        {
            return new GoalWorktreeRemoveResult("Workspace already clean; nothing to remove.", null, [], null);
        }

        var wasAlreadyUnregistered = !hasRegisteredWorktree;

        if (hasLeftoverDirectory && IsCleanupBackedOff(path, "remove", out var backoff))
        {
            var lockHolders = FindLockHoldersForCleanup(path);
            if (lockHolders.Count > 0)
            {
                WarnCleanupFailure(
                    path,
                    "remove:skip-backoff",
                    new IOException(BuildCleanupRetryMessage(path, backoff.Reason)));
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred by cleanup-needed backoff for {path}.",
                    path,
                    lockHolders,
                    ConductorRetryCommand(goalId));
            }
        }

        if (hasRegisteredWorktree)
        {
            var removal = GitCli.Run(executionDirectory, cleanupBudget.RemainingMilliseconds, "worktree", "remove", path);
            if (removal.ExitCode != 0 && IsRegisteredWorktree(executionDirectory, path, cleanupBudget.RemainingMilliseconds))
            {
                throw new InvalidOperationException(
                    $"Failed to remove goal workspace '{path}': {removal.Error} Commit, discard, or recover its changes; conductor cleanup will retry after the worktree is clean.");
            }
        }
        else
        {
            // Worktree already unregistered; prune any stale tracking entries left by a prior
            // partial removal so git's internal state is consistent before we finish cleanup.
            GitCli.Run(executionDirectory, cleanupBudget.RemainingMilliseconds, "worktree", "prune");
        }

        if (Directory.Exists(path))
        {
            ReapRecordedWorkerProcesses(kernel, path);
            if (!RunBoundedCleanupStep(path, "remove:build-server-shutdown", cleanupBudget, timeout => BuildServerShutdown(path, timeout)) ||
                !RunBoundedCleanupStep(path, "remove:acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, "remove", timeout)))
            {
                RecordCleanupNeeded(path, "remove:cleanup-budget-exhausted");
                return new GoalWorktreeRemoveResult(
                    $"Workspace cleanup deferred because cleanup budget was exhausted before deleting {path}.",
                    path,
                    FindLockHoldersForCleanup(path),
                    ConductorRetryCommand(goalId));
            }
        }

        if (Directory.Exists(path) && !DeleteDirectory(path))
        {
            WarnCleanupFailure(path, "remove", new IOException("Directory deletion failed after ACL reset."));
        }

        if (!BranchExists(executionDirectory, branch, cleanupBudget.RemainingMilliseconds))
        {
            _ = DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            return CompleteOrDeferredRemoveResult(
                path,
                goalId,
                "Removed workspace.",
                "Removed workspace, but leftover directory cleanup is incomplete.");
        }

        var branchRemoval = GitCli.Run(executionDirectory, cleanupBudget.RemainingMilliseconds, "branch", "-d", branch);
        _ = DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        return branchRemoval.ExitCode == 0
            ? CompleteOrDeferredRemoveResult(
                path,
                goalId,
                $"Removed workspace and merged branch {branch}.",
                $"Removed workspace and merged branch {branch}, but leftover directory cleanup is incomplete.")
            : CompleteOrDeferredRemoveResult(
                path,
                goalId,
                $"Removed workspace; branch {branch} kept because it has unmerged commits.",
                $"Removed workspace; branch {branch} kept because it has unmerged commits, but leftover directory cleanup is incomplete.");
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

    /// <summary>
    /// True when the goal worktree has committed changes against the base/main branch (work to
    /// accept), comparing the goal branch tip to its merge-base with the current branch.
    /// </summary>
    public static bool HasChangesAgainstMain(string executionDirectory, GoalId goalId)
    {
        var worktree = TryResolve(executionDirectory, goalId);
        if (worktree is null)
        {
            return false;
        }

        var baseBranch = GetCurrentBranchName(executionDirectory) ?? "main";
        var result = GitCli.Run(worktree, "diff", "--name-only", $"{baseBranch}...HEAD");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }

    /// <summary>
    /// True when the goal worktree has no uncommitted changes (git status --porcelain is empty).
    /// </summary>
    public static bool IsWorktreeClean(string executionDirectory, GoalId goalId)
    {
        var worktree = TryResolve(executionDirectory, goalId);
        if (worktree is null)
        {
            return false;
        }

        return !GitCli.IsWorktreeDirty(worktree);
    }

    public static string? TryGetBranchDiff(string executionDirectory, GoalId goalId)
    {
        var worktreePath = TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return null;
        }

        var statResult = GitCli.Run(worktreePath, "diff", "--stat", "main...HEAD");
        var patchResult = GitCli.Run(worktreePath, "diff", "main...HEAD");

        if (statResult.ExitCode != 0 && patchResult.ExitCode != 0)
        {
            return null;
        }

        var stat = statResult.Output.Trim();
        var patch = patchResult.Output.Trim();

        if (string.IsNullOrWhiteSpace(stat) && string.IsNullOrWhiteSpace(patch))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(stat) ? patch : $"{stat}{Environment.NewLine}---{Environment.NewLine}{patch}";
    }

    public static bool IsBranchMergedIntoCurrent(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);

        var branch = BranchName(goalId);
        return !BranchExists(executionDirectory, branch) ||
            GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", branch, "HEAD").ExitCode == 0;
    }

    public static bool HasBranch(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);
        return BranchExists(executionDirectory, BranchName(goalId));
    }

    public static GoalWorktreeMergeResult? TryFastForwardMerge(string executionDirectory, GoalId goalId)
    {
        var branch = BranchName(goalId);
        if (!BranchExists(executionDirectory, branch))
        {
            return null;
        }

        var merge = GitCli.Run(executionDirectory, "merge", "--ff-only", branch);
        if (merge.ExitCode == 0)
        {
            return new GoalWorktreeMergeResult(
                true,
                branch,
                $"Fast-forwarded to {branch}.",
                null);
        }

        return new GoalWorktreeMergeResult(
            false,
            branch,
            $"Branch {branch} cannot fast-forward; merge it explicitly after review.",
            $"git merge {branch}");
    }

    public static GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);

        var branch = BranchName(goalId);
        var baseBranch = GetCurrentBranchName(executionDirectory) ?? "main";
        if (!BranchExists(executionDirectory, branch))
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.MissingBranch,
                branch,
                $"Goal branch {branch} is missing.",
                [],
                "goal-recovery");
        }

        var worktreePath = TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.MissingWorktree,
                branch,
                $"Goal branch {branch} has no registered worktree.",
                [],
                $"workspace create {Prefix(goalId)}");
        }

        if (GitCli.IsWorktreeDirty(worktreePath))
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.DirtyWorktree,
                branch,
                $"Goal worktree for {branch} has uncommitted changes; commit or discard them before rebase recovery.",
                [],
                $"goal-recovery {Prefix(goalId)}");
        }

        if (GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", branch).ExitCode == 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                branch,
                $"Branch {branch} can already fast-forward into main; no rebase needed.",
                [],
                $"acceptance {Prefix(goalId)}");
        }

        // Use the "merge" backend (a real per-commit 3-way merge), NOT "--apply" (the legacy am/patch
        // backend). --apply matches on patch CONTEXT, so it spuriously conflicts when main changed lines
        // NEAR the goal's changes in the same file — even non-overlapping — a base-skew false-conflict
        // that forces an escalation + manual re-dispatch. --merge only conflicts on actually-overlapping
        // hunks. (--no-stat + the RunGitDirect fallback keep the Windows stat-path workaround intact.)
        var rebase = GitCli.Run(worktreePath, "rebase", "--merge", "--no-stat", baseBranch);
        if (IsRebaseStatPathFailure(rebase))
        {
            rebase = RunGitDirect(worktreePath, "rebase", "--merge", "--no-stat", baseBranch);
        }
        if (rebase.ExitCode == 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Rebased,
                branch,
                $"Rebased {branch} onto {baseBranch}; acceptance can now fast-forward after review.",
                [],
                $"acceptance {Prefix(goalId)}");
        }

        var conflictFiles = GetConflictFiles(worktreePath);
        _ = GitCli.Run(worktreePath, "rebase", "--abort");
        if (conflictFiles.Length > 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict,
                branch,
                $"Rebase of {branch} onto {baseBranch} found conflicts; branch was restored to its pre-rebase state.",
                conflictFiles,
                $"Create an operator task to resolve conflicts in order: {string.Join(", ", conflictFiles)}");
        }

        var detail = string.IsNullOrWhiteSpace(rebase.Error) ? rebase.Output.Trim() : rebase.Error.Trim();
        return new GoalWorktreeRebaseResult(
            GoalWorktreeRebaseStatus.Failed,
            branch,
            string.IsNullOrWhiteSpace(detail)
                ? $"Rebase of {branch} onto {baseBranch} failed; branch was restored to its pre-rebase state."
                : $"Rebase of {branch} onto {baseBranch} failed: {detail}",
            [],
            $"goal-recovery {Prefix(goalId)}");
    }

    private static string Prefix(GoalId goalId)
    {
        var value = goalId.Value;
        return (value.Length <= 8 ? value : value[..8]).ToLowerInvariant();
    }

    private static bool BranchExists(string executionDirectory, string branch)
    {
        return BranchExists(executionDirectory, branch, GitCli.DefaultTimeoutMilliseconds);
    }

    private static bool BranchExists(string executionDirectory, string branch, int gitTimeoutMilliseconds)
    {
        return GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
    }

    private static GitCli.GitResult AddWorktree(string executionDirectory, string path, string branch, bool branchExists)
    {
        return branchExists
            ? GitCli.Run(executionDirectory, "worktree", "add", path, branch)
            : GitCli.Run(executionDirectory, "worktree", "add", path, "-b", branch);
    }

    private static bool WorktreeAddFailedBecausePathExists(GitCli.GitResult result, string path)
    {
        if (result.ExitCode == 0 || !Directory.Exists(path))
        {
            return false;
        }

        var message = $"{result.Output}{Environment.NewLine}{result.Error}";
        return message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentBranchName(string executionDirectory)
    {
        var result = GitCli.Run(executionDirectory, "branch", "--show-current");
        var branch = result.Output.Trim();
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch) ? branch : null;
    }

    private static string[] GetConflictFiles(string worktreePath)
    {
        var result = GitCli.Run(worktreePath, "diff", "--name-only", "--diff-filter=U");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsRegisteredWorktree(string executionDirectory, string path)
    {
        return IsRegisteredWorktree(executionDirectory, path, GitCli.DefaultTimeoutMilliseconds);
    }

    private static bool IsRegisteredWorktree(string executionDirectory, string path, int gitTimeoutMilliseconds)
    {
        return RegisteredWorktreePaths(executionDirectory, gitTimeoutMilliseconds).Contains(NormalizePath(path));
    }

    private static GoalWorktreeRemoveResult CompleteOrDeferredRemoveResult(
        string path,
        GoalId goalId,
        string completeMessage,
        string incompleteMessage)
    {
        if (!Directory.Exists(path))
        {
            ClearCleanupNeeded(path);
            return new GoalWorktreeRemoveResult(completeMessage, null, [], null);
        }

        var resumeCommand = ConductorRetryCommand(goalId);
        RecordCleanupNeeded(path, "remove:leftover-directory");
        return new GoalWorktreeRemoveResult(
            $"{incompleteMessage} Conductor retry: {resumeCommand}",
            path,
            FindLockHoldersForCleanup(path),
            resumeCommand);
    }

    private static string ConductorRetryCommand(GoalId goalId) => $"conduct {Prefix(goalId)} --loop";

    private static bool IsRebaseStatPathFailure(GitCli.GitResult result)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }

        var message = $"{result.Output}{Environment.NewLine}{result.Error}";
        return message.Contains("failed to stat", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("...", StringComparison.Ordinal);
    }

    private static GitCli.GitResult RunGitDirect(string workingDirectory, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.longpaths=true");
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitCli.GitResult(1, string.Empty, "failed to start git process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitCli.DefaultTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new GitCli.GitResult(-1, string.Empty, $"git {string.Join(' ', args)} timed out after {GitCli.DefaultTimeoutMilliseconds}ms");
            }

            Task.WaitAll([outputTask, errorTask], 5_000);
            var output = outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty;
            var error = errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty;
            return new GitCli.GitResult(process.ExitCode, output, error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new GitCli.GitResult(1, string.Empty, ex.Message);
        }
    }

    private static HashSet<string> RegisteredWorktreePaths(string executionDirectory)
    {
        return RegisteredWorktreePaths(executionDirectory, GitCli.DefaultTimeoutMilliseconds);
    }

    private static HashSet<string> RegisteredWorktreePaths(string executionDirectory, int gitTimeoutMilliseconds)
    {
        var result = GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "worktree", "list", "--porcelain");
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => NormalizePath(line["worktree ".Length..]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    private static void EnsureWorktreeRootIgnored(string executionDirectory)
    {
        try
        {
            var gitDirResult = GitCli.Run(executionDirectory, "rev-parse", "--git-dir");
            if (gitDirResult.ExitCode != 0 || string.IsNullOrWhiteSpace(gitDirResult.Output))
            {
                return;
            }

            var gitDir = gitDirResult.Output.Trim();
            if (!Path.IsPathRooted(gitDir))
            {
                gitDir = Path.GetFullPath(Path.Combine(executionDirectory, gitDir));
            }

            var infoDirectory = Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDirectory);
            var excludePath = Path.Combine(infoDirectory, "exclude");
            const string ignoreEntry = ".orchestrator-worktrees/";
            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            if (existing
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => string.Equals(line, ignoreEntry, StringComparison.Ordinal)))
            {
                return;
            }

            File.AppendAllText(excludePath,
                (existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : string.Empty) +
                ignoreEntry +
                Environment.NewLine);
        }
        catch
        {
            // Ignore hygiene failures; git worktree creation remains the authoritative operation.
        }
    }

    /// <summary>
    /// Returns true when <paramref name="executionDirectory"/> is inside a git work tree.
    /// Lets callers decide whether deterministic worktree chorekeeping is possible before
    /// attempting it (e.g. dispatch auto-create falls back to the execution directory otherwise).
    /// </summary>
    public static bool IsGitWorkTree(string executionDirectory)
    {
        return IsGitWorkTree(executionDirectory, GitCli.DefaultTimeoutMilliseconds);
    }

    private static bool IsGitWorkTree(string executionDirectory, int gitTimeoutMilliseconds)
    {
        var result = GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "rev-parse", "--is-inside-work-tree");
        return result.ExitCode == 0 && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireGitWorkTree(string executionDirectory)
    {
        RequireGitWorkTree(executionDirectory, GitCli.DefaultTimeoutMilliseconds);
    }

    private static void RequireGitWorkTree(string executionDirectory, int gitTimeoutMilliseconds)
    {
        if (!IsGitWorkTree(executionDirectory, gitTimeoutMilliseconds))
        {
            throw new InvalidOperationException(
                $"Goal workspaces require '{executionDirectory}' to be inside a git work tree.");
        }
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
            WarnCleanupFailure(
                path,
                operation + ":skip-backoff",
                new IOException(BuildCleanupRetryMessage(path, backoff.Reason)));
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
            return !Directory.Exists(path);
        }

        ReapRecordedWorkerProcesses(kernel, path);
        if (!RunBoundedCleanupStep(path, operation + ":acl-reset", cleanupBudget, timeout => ResetSandboxAcl(path, operation, timeout)))
        {
            RecordOrphanCleanupBackoff(path, operation + ":acl-reset-timeout");
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
        return !Directory.Exists(path);
    }

    private static bool RunBoundedCleanupStep(
        string worktreePath,
        string operation,
        GoalWorktreeCleanupBudget cleanupBudget,
        Action<int> action)
    {
        if (cleanupBudget.IsExpired)
        {
            WarnCleanupFailure(
                worktreePath,
                operation,
                new TimeoutException($"Cleanup budget exhausted before {operation}."));
            return false;
        }

        action(cleanupBudget.RemainingMilliseconds);
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

    private static void ResetSandboxAcl(string worktreePath, string operation, int timeoutMilliseconds)
    {
        try
        {
            SandboxAclHelper.ResetSandboxAcl(worktreePath, timeoutMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            WarnCleanupFailure(worktreePath, operation + ":acl-reset", ex);
        }
    }

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

    private static string BuildCleanupRetryMessage(string path, string reason) =>
        $"{reason} Cleanup-needed record persisted in SQLite for conductor retry; path='{path}'.";

    private static string CleanupBackoffStorePath(string orphanPath)
    {
        var root = LocateWorktreeRoot(orphanPath);
        return Path.Combine(Path.GetDirectoryName(root)!, ".orchestrator", "state.db");
    }

    private static string LocateWorktreeRoot(string orphanPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(orphanPath));
        while (directory.Parent is not null)
        {
            if (directory.Parent.Name.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetDirectoryName(Path.GetFullPath(orphanPath)) ?? Path.GetFullPath(orphanPath);
    }

    private static bool IsCleanupBackedOff(string path, string operation, out OrphanCleanupBackoffEntry entry)
    {
        entry = default!;
        if (!operation.Equals("orphan-sweep", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryReadOrphanCleanupBackoff(path, out var existingEntry))
        {
            return false;
        }

        entry = existingEntry;
        if (entry.SkipUntilUtc > CleanupUtcNow())
        {
            return true;
        }

        ClearOrphanCleanupBackoff(path);
        return false;
    }

    private static void RecordCleanupNeeded(string path, string reason) =>
        RecordOrphanCleanupBackoff(path, reason, "remove:cleanup-needed");

    private static void ClearCleanupNeeded(string path) =>
        ClearOrphanCleanupBackoff(path);

    private static void RecordOrphanCleanupBackoff(string path, string reason, string warningOperation = "orphan-sweep:backoff")
    {
        try
        {
            using var conn = OpenCleanupBackoffConnection(path);
            using var command = conn.CreateCommand();
            command.CommandText = """
                INSERT INTO worktree_cleanup_backoff(path, skip_until_utc, reason)
                VALUES ($path, $skipUntilUtc, $reason)
                ON CONFLICT(path) DO UPDATE SET
                    skip_until_utc = excluded.skip_until_utc,
                    reason = excluded.reason;
                """;
            command.Parameters.AddWithValue("$path", NormalizePath(path));
            command.Parameters.AddWithValue("$skipUntilUtc", CleanupUtcNow().Add(CleanupBackoffDuration).ToString("O"));
            command.Parameters.AddWithValue("$reason", reason);
            command.ExecuteNonQuery();
            WarnCleanupFailure(path, warningOperation, new TimeoutException(BuildCleanupRetryMessage(path, reason)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:backoff-write", ex);
        }
    }

    private static bool TryReadOrphanCleanupBackoff(string path, out OrphanCleanupBackoffEntry entry)
    {
        entry = default!;
        try
        {
            var statePath = CleanupBackoffStorePath(path);
            if (!File.Exists(statePath))
            {
                return false;
            }

            using var conn = OpenCleanupBackoffConnection(path);
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT skip_until_utc, reason FROM worktree_cleanup_backoff WHERE path = $path";
            command.Parameters.AddWithValue("$path", NormalizePath(path));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(reader.GetString(0), out var skipUntilUtc))
            {
                return false;
            }

            entry = new OrphanCleanupBackoffEntry(skipUntilUtc, reader.GetString(1));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:backoff-read", ex);
            return false;
        }
    }

    private static void ClearOrphanCleanupBackoff(string path)
    {
        try
        {
            var statePath = CleanupBackoffStorePath(path);
            if (!File.Exists(statePath))
                return;

            using var conn = OpenCleanupBackoffConnection(path);
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM worktree_cleanup_backoff WHERE path = $path";
            command.Parameters.AddWithValue("$path", NormalizePath(path));
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:backoff-clear", ex);
        }
    }

    private static SqliteConnection OpenCleanupBackoffConnection(string path)
    {
        var statePath = CleanupBackoffStorePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = CleanupBackoffTableSql;
        command.ExecuteNonQuery();
        return conn;
    }

    private sealed record OrphanCleanupBackoffEntry(DateTimeOffset SkipUntilUtc, string Reason);

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
