using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

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

public static class GoalWorktrees
{
    public const string DirectoryName = ".orchestrator-worktrees";
    private static readonly TimeSpan InitialDeleteRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int DeleteRetryAttempts = 6;
    private static readonly string[] LockHolderCandidates =
        ["dotnet", "VBCSCompiler", "MSBuild", "claude", "codex", "node", "powershell", "pwsh"];
    private static readonly TimeSpan BuildServerShutdownTimeout = TimeSpan.FromSeconds(10);

    // Injectable for testing: called best-effort before directory deletion to release any
    // VBCSCompiler/Roslyn/MSBuild file handles held by the acceptance build server.
    internal static Action<string> BuildServerShutdown = DefaultBuildServerShutdown;

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

    public static string Ensure(string executionDirectory, GoalId goalId)
    {
        var existing = TryResolve(executionDirectory, goalId);
        if (existing is not null)
        {
            FastForwardToBaseIfStale(executionDirectory, existing);
            return existing;
        }

        RequireGitWorkTree(executionDirectory);

        var path = WorktreePath(executionDirectory, goalId);
        var branch = BranchName(goalId);
        var result = BranchExists(executionDirectory, branch)
            ? GitCli.Run(executionDirectory, "worktree", "add", path, branch)
            : GitCli.Run(executionDirectory, "worktree", "add", path, "-b", branch);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create goal workspace at '{path}': {result.Error}");
        }

        FastForwardToBaseIfStale(executionDirectory, path);
        return path;
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

    public static GoalWorktreeRemoveResult Remove(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);

        var path = WorktreePath(executionDirectory, goalId);
        var hasRegisteredWorktree = IsRegisteredWorktree(executionDirectory, path);
        var hasLeftoverDirectory = Directory.Exists(path);
        var branch = BranchName(goalId);
        var hasBranch = BranchExists(executionDirectory, branch);

        if (!hasRegisteredWorktree && !hasLeftoverDirectory && !hasBranch)
        {
            return new GoalWorktreeRemoveResult("Workspace already clean; nothing to remove.", null, [], null);
        }

        var wasAlreadyUnregistered = !hasRegisteredWorktree;

        if (hasRegisteredWorktree)
        {
            var removal = GitCli.Run(executionDirectory, "worktree", "remove", path);
            if (removal.ExitCode != 0 && IsRegisteredWorktree(executionDirectory, path))
            {
                throw new InvalidOperationException(
                    $"Failed to remove goal workspace '{path}': {removal.Error} Commit or discard its changes, or remove it manually with: git worktree remove --force \"{path}\"");
            }
        }
        else
        {
            // Worktree already unregistered; prune any stale tracking entries left by a prior
            // partial removal so git's internal state is consistent before we finish cleanup.
            GitCli.Run(executionDirectory, "worktree", "prune");
        }

        if (Directory.Exists(path))
        {
            BuildServerShutdown(path);
        }

        if (Directory.Exists(path) && !DeleteDirectoryWithRetry(path))
        {
            var lockHolders = FindLockHolders(path);
            var prefix = wasAlreadyUnregistered
                ? "Workspace already unregistered; directory could not be removed."
                : "Workspace unregistered; directory could not be removed.";
            var branchNote = BranchExists(executionDirectory, branch) ? $" Branch {branch} remains." : "";
            return new GoalWorktreeRemoveResult(
                prefix + branchNote,
                path,
                lockHolders,
                "workspace remove");
        }

        if (!BranchExists(executionDirectory, branch))
        {
            _ = DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            return new GoalWorktreeRemoveResult("Removed workspace.", null, [], null);
        }

        var branchRemoval = GitCli.Run(executionDirectory, "branch", "-d", branch);
        _ = DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        return branchRemoval.ExitCode == 0
            ? new GoalWorktreeRemoveResult($"Removed workspace and merged branch {branch}.", null, [], null)
            : new GoalWorktreeRemoveResult($"Removed workspace; branch {branch} kept because it has unmerged commits.", null, [], null);
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

        var rebase = GitCli.Run(worktreePath, "rebase", baseBranch);
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
        return GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
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
        var result = GitCli.Run(executionDirectory, "worktree", "list", "--porcelain");
        if (result.ExitCode != 0)
        {
            return false;
        }

        var target = NormalizePath(path);
        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => NormalizePath(line["worktree ".Length..]))
            .Any(worktree => string.Equals(worktree, target, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Returns true when <paramref name="executionDirectory"/> is inside a git work tree.
    /// Lets callers decide whether deterministic worktree chorekeeping is possible before
    /// attempting it (e.g. dispatch auto-create falls back to the execution directory otherwise).
    /// </summary>
    public static bool IsGitWorkTree(string executionDirectory)
    {
        var result = GitCli.Run(executionDirectory, "rev-parse", "--is-inside-work-tree");
        return result.ExitCode == 0 && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireGitWorkTree(string executionDirectory)
    {
        if (!IsGitWorkTree(executionDirectory))
        {
            throw new InvalidOperationException(
                $"Goal workspaces require '{executionDirectory}' to be inside a git work tree.");
        }
    }

    private static bool DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        var delay = InitialDeleteRetryDelay;
        for (var attempt = 1; attempt <= DeleteRetryAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (IsTransientDeleteFailure(ex))
            {
                if (attempt >= DeleteRetryAttempts)
                {
                    return false;
                }

                Thread.Sleep(delay);
                delay += delay;
            }
        }

        return false;
    }

    private static bool IsTransientDeleteFailure(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    private static void DefaultBuildServerShutdown(string worktreePath)
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
            if (!process.WaitForExit((int)BuildServerShutdownTimeout.TotalMilliseconds))
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
