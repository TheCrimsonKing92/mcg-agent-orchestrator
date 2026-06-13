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
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialDeleteRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int DeleteRetryAttempts = 6;
    private static readonly string[] LockHolderCandidates =
        ["dotnet", "VBCSCompiler", "MSBuild", "claude", "codex", "node", "powershell", "pwsh"];

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
            return existing;
        }

        RequireGitWorkTree(executionDirectory);

        var path = WorktreePath(executionDirectory, goalId);
        var branch = BranchName(goalId);
        var result = BranchExists(executionDirectory, branch)
            ? RunGit(executionDirectory, "worktree", "add", path, branch)
            : RunGit(executionDirectory, "worktree", "add", path, "-b", branch);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create goal workspace at '{path}': {result.Error}");
        }

        return path;
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
            throw new InvalidOperationException($"Goal '{Prefix(goalId)}' has no workspace to remove.");
        }

        var wasAlreadyUnregistered = !hasRegisteredWorktree;

        if (hasRegisteredWorktree)
        {
            var removal = RunGit(executionDirectory, "worktree", "remove", path);
            if (removal.ExitCode != 0 && IsRegisteredWorktree(executionDirectory, path))
            {
                throw new InvalidOperationException(
                    $"Failed to remove goal workspace '{path}': {removal.Error} Commit or discard its changes, or remove it manually with: git worktree remove --force \"{path}\"");
            }
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

        var branchRemoval = RunGit(executionDirectory, "branch", "-d", branch);
        _ = DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        return branchRemoval.ExitCode == 0
            ? new GoalWorktreeRemoveResult($"Removed workspace and merged branch {branch}.", null, [], null)
            : new GoalWorktreeRemoveResult($"Removed workspace; branch {branch} kept because it has unmerged commits.", null, [], null);
    }

    public static string? TryGetBranchDiff(string executionDirectory, GoalId goalId)
    {
        var worktreePath = TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return null;
        }

        var statResult = RunGit(worktreePath, "diff", "--stat", "main...HEAD");
        var patchResult = RunGit(worktreePath, "diff", "main...HEAD");

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

        var merge = RunGit(executionDirectory, "merge", "--ff-only", branch);
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

        var status = RunGit(worktreePath, "status", "--porcelain");
        if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Output))
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.DirtyWorktree,
                branch,
                $"Goal worktree for {branch} has uncommitted changes; commit or discard them before rebase recovery.",
                [],
                $"goal-recovery {Prefix(goalId)}");
        }

        if (RunGit(executionDirectory, "merge-base", "--is-ancestor", "HEAD", branch).ExitCode == 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                branch,
                $"Branch {branch} can already fast-forward into main; no rebase needed.",
                [],
                $"acceptance {Prefix(goalId)}");
        }

        var rebase = RunGit(worktreePath, "rebase", baseBranch);
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
        _ = RunGit(worktreePath, "rebase", "--abort");
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
        return RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
    }

    private static string? GetCurrentBranchName(string executionDirectory)
    {
        var result = RunGit(executionDirectory, "branch", "--show-current");
        var branch = result.Output.Trim();
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch) ? branch : null;
    }

    private static string[] GetConflictFiles(string worktreePath)
    {
        var result = RunGit(worktreePath, "diff", "--name-only", "--diff-filter=U");
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
        var result = RunGit(executionDirectory, "worktree", "list", "--porcelain");
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

    private static void RequireGitWorkTree(string executionDirectory)
    {
        var result = RunGit(executionDirectory, "rev-parse", "--is-inside-work-tree");
        if (result.ExitCode != 0 || !result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Goal workspaces require '{executionDirectory}' to be inside a git work tree: {result.Error}");
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

        var commandLines = TryGetProcessCommandLines(processesByPid.Keys);
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

    private static Dictionary<int, string> TryGetProcessCommandLines(IEnumerable<int> pids)
    {
        try
        {
            var pidList = pids.ToList();
            if (pidList.Count == 0)
            {
                return [];
            }

            var filter = string.Join(" OR ", pidList.Select(pid => $"ProcessId={pid}"));
            var psi = new ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = $"process where \"({filter})\" get ProcessId,CommandLine /format:list",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            return ParseWmicListOutput(output);
        }
        catch
        {
            return [];
        }
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

    private static GitResult RunGit(string workingDirectory, params string[] arguments)
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} timed out after {GitTimeout.TotalSeconds}s.");
        }

        return new GitResult(process.ExitCode, output, error.Trim());
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
