namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    // A manually resolved integration merge may preserve every goal-owned line.
    // Compare the resulting tree against main rather than treating the merge as a patch.
    private static bool TryCompareGoalOwnedLines(
        string directory, string oldBase, string oldHead, string newBase, string newHead)
    {
        return TryReadChangedLines(directory, oldBase, oldHead, out var oldLines) &&
            TryReadChangedLines(directory, newBase, newHead, out var newLines) &&
            oldLines.SequenceEqual(newLines, StringComparer.Ordinal);
    }
}
