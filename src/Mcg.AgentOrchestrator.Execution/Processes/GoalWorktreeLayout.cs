using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalWorktreeLayout
{
    public const string DirectoryName = ".orchestrator-worktrees";

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

    internal static bool IsExclusiveGoalWorktree(string path, GoalId goalId)
    {
        var normalized = NormalizePath(path);
        var worktreeDirectory = Directory.GetParent(normalized);
        var executionDirectory = worktreeDirectory?.Parent;
        if (worktreeDirectory is null || executionDirectory is null ||
            !worktreeDirectory.Name.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var resolved = TryResolve(executionDirectory.FullName, goalId);
        return resolved is not null &&
            string.Equals(
                NormalizePath(resolved),
                normalized,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static string Prefix(GoalId goalId)
    {
        var value = goalId.Value;
        return (value.Length <= 8 ? value : value[..8]).ToLowerInvariant();
    }

    internal static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
