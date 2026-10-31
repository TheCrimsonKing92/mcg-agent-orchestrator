using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Captures only the goal facts available to this pass. A kernel can be a working set;
// absence from it never proves that a goal-id-shaped directory is abandoned.
internal sealed class OrphanWorktreeSweepEligibility
{
    internal static readonly TimeSpan GraceWindow = TimeSpan.FromMinutes(10);
    private readonly (GoalId Id, bool IsTerminal)[] goals;

    internal OrphanWorktreeSweepEligibility(AgentOrchestratorKernel? kernel)
    {
        try { goals = kernel?.Goals.Select(goal => (goal.Id, goal.IsTerminal)).ToArray() ?? []; }
        catch (InvalidOperationException) { goals = []; }
    }

    internal GoalId? ResolveGoal(string directory)
    {
        var name = System.IO.Path.GetFileName(directory);
        var matches = goals.Where(goal => goal.Id.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 0 ? null : matches.FirstOrDefault(goal => !goal.IsTerminal, matches[0]).Id;
    }

    internal string Classify(string directory, IReadOnlySet<string> registered, DateTimeOffset referenceTime)
    {
        if (registered.Contains(GoalWorktreeLayout.NormalizePath(directory))) return "registered";
        var name = System.IO.Path.GetFileName(directory);
        var matches = goals.Where(goal => goal.Id.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Any(goal => !goal.IsTerminal)) return "non-terminal-goal";
        if (matches.Length == 0 && name.Length == 8 && name.All(Uri.IsHexDigit)) return "goal-state-unavailable";

        try
        {
            var pending = new Stack<FileSystemInfo>();
            pending.Push(new DirectoryInfo(directory));
            var cutoff = referenceTime.UtcDateTime - GraceWindow;
            while (pending.TryPop(out var entry))
            {
                entry.Refresh();
                // Never follow a junction/symlink or authorize deletion from missing metadata.
                if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0) return "freshness-unknown";
                if (entry.LastWriteTimeUtc >= cutoff) return "recent";
                if (entry is DirectoryInfo childDirectory)
                    foreach (var child in childDirectory.EnumerateFileSystemInfos()) pending.Push(child);
            }
            return "eligible";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "freshness-unknown";
        }
    }
}
