using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record GoalWorktreeMergeResult(
    bool FastForwarded,
    string BranchName,
    string Message,
    string? SuggestedCommand);

public static class GoalWorktrees
{
    public const string DirectoryName = ".orchestrator-worktrees";
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

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

    public static string Remove(string executionDirectory, GoalId goalId)
    {
        var path = TryResolve(executionDirectory, goalId)
            ?? throw new InvalidOperationException($"Goal '{Prefix(goalId)}' has no workspace to remove.");

        var removal = RunGit(executionDirectory, "worktree", "remove", path);
        if (removal.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to remove goal workspace '{path}': {removal.Error} Commit or discard its changes, or remove it manually with: git worktree remove --force \"{path}\"");
        }

        var branch = BranchName(goalId);
        var branchRemoval = RunGit(executionDirectory, "branch", "-d", branch);
        return branchRemoval.ExitCode == 0
            ? $"Removed workspace and merged branch {branch}."
            : $"Removed workspace; branch {branch} kept because it has unmerged commits.";
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

    private static string Prefix(GoalId goalId)
    {
        var value = goalId.Value;
        return (value.Length <= 8 ? value : value[..8]).ToLowerInvariant();
    }

    private static bool BranchExists(string executionDirectory, string branch)
    {
        return RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
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
