namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    // Success with no merge leaves both outputs empty; an unusable merge refuses the fallback.
    private static bool TryReadIntegratedMainParent(
        string directory, string baseSha, string headSha, out string merge, out string mainParent)
    {
        merge = string.Empty;
        mainParent = string.Empty;
        var listed = GitCli.Run(directory, "rev-list", "--merges", "--topo-order", $"{baseSha}..{headSha}");
        if (!listed.Succeeded || listed.DrainTimedOut) return false;

        var newest = listed.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (newest is null) return true;
        if (!CommitShaPattern.IsMatch(newest) ||
            !TryReadGit(directory, ["rev-list", "--parents", "-n", "1", newest], out var parentLine))
            return false;

        var parents = parentLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parents.Length != 3 ||
            !string.Equals(parents[0], newest, StringComparison.OrdinalIgnoreCase) ||
            parents.Any(parent => !CommitShaPattern.IsMatch(parent)))
            return false;

        // The main-side parent is outside the goal range: it is an ancestor of its base.
        // Do not re-check it against the mutable main ref.
        for (var i = 1; i < parents.Length; i++)
        {
            var ancestor = GitCli.Run(directory, "merge-base", "--is-ancestor", parents[i], baseSha);
            if (!ancestor.ProcessStarted || ancestor.DrainTimedOut || ancestor.ExitCode is not (0 or 1))
                return false;
            if (ancestor.ExitCode == 1) continue;
            if (mainParent.Length > 0) return false;
            mainParent = parents[i];
        }

        if (mainParent.Length == 0) return false;
        merge = newest;
        return true;
    }

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
