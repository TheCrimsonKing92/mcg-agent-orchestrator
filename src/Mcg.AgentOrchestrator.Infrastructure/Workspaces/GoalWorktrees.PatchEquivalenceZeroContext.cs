namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    // This fallback is deliberately narrower than range-diff: it cannot reason about merge parents.
    private static bool TryCompareZeroContextCommitsInOrder(
        string directory, string oldBase, string oldHead, string newBase, string newHead, int expectedCount)
    {
        return TryListLinearCommits(directory, oldBase, oldHead, expectedCount, out var oldCommits) &&
            TryListLinearCommits(directory, newBase, newHead, expectedCount, out var newCommits) &&
            oldCommits.Zip(newCommits).All(pair =>
                TryReadChangedLines(directory, pair.First, out var oldLines) &&
                TryReadChangedLines(directory, pair.Second, out var newLines) &&
                oldLines.SequenceEqual(newLines, StringComparer.Ordinal));
    }

    private static bool TryListLinearCommits(
        string directory, string baseSha, string headSha, int expectedCount, out string[] commits)
    {
        commits = [];
        var result = GitCli.Run(directory, "rev-list", "--reverse", $"{baseSha}..{headSha}");
        if (!result.Succeeded || result.DrainTimedOut) return false;
        commits = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length != expectedCount || commits.Any(sha => !CommitShaPattern.IsMatch(sha))) return false;
        foreach (var commit in commits)
        {
            if (!TryReadGit(directory, ["rev-list", "--parents", "-n", "1", commit], out var parents) ||
                parents.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length != 2)
                return false;
        }
        return true;
    }

    private static bool TryReadChangedLines(string directory, string commit, out string[] changedLines)
    {
        changedLines = [];
        var result = GitCli.Run(directory, "diff", "--no-ext-diff", "--no-textconv", "--no-renames",
            "--no-color", "-U0", $"{commit}^", commit, "--");
        if (!result.Succeeded || result.DrainTimedOut || result.Output.Contains('\uFFFD')) return false;

        var lines = new List<string>();
        var inHunk = false;
        var inFile = false;
        foreach (var line in result.Output.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inFile = true;
                inHunk = false;
                lines.Add(line);
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                     line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                return false;
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunk = true;
            }
            else if (inFile && !inHunk &&
                     (line.StartsWith("old mode ", StringComparison.Ordinal) ||
                      line.StartsWith("new mode ", StringComparison.Ordinal) ||
                      line.StartsWith("new file mode ", StringComparison.Ordinal) ||
                      line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
                      line.StartsWith("--- ", StringComparison.Ordinal) ||
                      line.StartsWith("+++ ", StringComparison.Ordinal)))
            {
                lines.Add(line);
            }
            else if (inFile && inHunk &&
                     (line.StartsWith('+') || line.StartsWith('-') || line.StartsWith('\\')))
            {
                lines.Add(line);
            }
        }
        changedLines = [.. lines];
        return lines.Count > 0;
    }
}
