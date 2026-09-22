using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
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

    private static string Prefix(GoalId goalId)
    {
        var value = goalId.Value;
        return (value.Length <= 8 ? value : value[..8]).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

}
