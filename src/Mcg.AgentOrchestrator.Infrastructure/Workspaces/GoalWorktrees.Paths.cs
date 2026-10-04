using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    public static string BranchName(GoalId goalId) => GoalWorktreeLayout.BranchName(goalId);

    public static string WorktreePath(string executionDirectory, GoalId goalId) => GoalWorktreeLayout.WorktreePath(executionDirectory, goalId);

    public static string? TryResolve(string executionDirectory, GoalId goalId) => GoalWorktreeLayout.TryResolve(executionDirectory, goalId);

    internal static bool IsExclusiveGoalWorktree(string path, GoalId goalId) => GoalWorktreeLayout.IsExclusiveGoalWorktree(path, goalId);

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

    private static string Prefix(GoalId goalId) => GoalWorktreeLayout.Prefix(goalId);

    private static string NormalizePath(string path) => GoalWorktreeLayout.NormalizePath(path);

}
