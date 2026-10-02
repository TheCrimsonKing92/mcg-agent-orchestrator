using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    // A squash may change patch history, but it must preserve exactly the merged candidate tree.
    private static bool TryComputeSquashEquivalence(
        string executionDirectory, string oldHeadSha, string newHeadSha, out string evidence)
    {
        evidence = string.Empty;
        if (string.IsNullOrWhiteSpace(executionDirectory) || !Directory.Exists(executionDirectory) ||
            !TryResolveCommit(executionDirectory, oldHeadSha, out var oldHead) ||
            !TryResolveCommit(executionDirectory, newHeadSha, out var newHead) ||
            !TryReadGit(executionDirectory, ["rev-list", "--parents", "-n", "1", newHead], out var parentLine))
            return false;

        var parents = parentLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parents.Length != 2 || !string.Equals(parents[0], newHead, StringComparison.OrdinalIgnoreCase) ||
            !CommitShaPattern.IsMatch(parents[1])) return false;
        var mainParent = parents[1];
        var onMain = GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", mainParent, "refs/heads/main");
        if (!onMain.Succeeded || onMain.DrainTimedOut ||
            !HasMergeCommitsOutsideBase(executionDirectory, mainParent, oldHead) ||
            !TryReadCleanMergeTree(executionDirectory, mainParent, oldHead, out var mergedTree) ||
            !TryReadGit(executionDirectory, ["rev-parse", "--verify", $"{newHead}^{{tree}}"], out var newTree) ||
            !string.Equals(mergedTree, newTree, StringComparison.OrdinalIgnoreCase)) return false;

        evidence = $"squashed-merge-commits: {newHead} single parent {mainParent} on main; " +
            $"tree {newTree} equals git merge-tree --write-tree {mainParent} {oldHead}";
        return true;
    }

    private static bool HasMergeCommitsOutsideBase(string executionDirectory, string baseHead, string branchHead) =>
        TryReadGit(executionDirectory, ["rev-list", "--merges", "--count", $"{baseHead}..{branchHead}"], out var output) &&
        int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0;

    private static bool TryReadCleanMergeTree(
        string executionDirectory, string baseHead, string branchHead, out string tree)
    {
        tree = string.Empty;
        try
        {
            var merge = new WorkerGitContext().ReadReviewerMergeTreeStatus(executionDirectory, baseHead, branchHead);
            if (!merge.IsClean || merge.TreeId is null || !CommitShaPattern.IsMatch(merge.TreeId)) return false;
            tree = merge.TreeId;
            return true;
        }
        catch (ReviewerMergeTreeStatusException)
        {
            return false;
        }
    }
}
