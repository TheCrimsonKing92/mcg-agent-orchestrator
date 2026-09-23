using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private const int PatchEquivalenceCommitLimit = 200;
    private static readonly Regex CommitShaPattern = new(
        "^[0-9a-f]{40}([0-9a-f]{24})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RangeDiffHeaderPattern = new(
        @"^\s*(?<left>\d+):\s+[0-9a-f]+\s+(?<operator>[=!<>])\s+(?<right>\d+):\s+[0-9a-f]+\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryComputePatchEquivalence(
        string executionDirectory,
        string oldHeadSha,
        string newHeadSha,
        out string evidence)
    {
        evidence = string.Empty;
        if (string.IsNullOrWhiteSpace(executionDirectory) ||
            !TryResolveCommit(executionDirectory, oldHeadSha, out var oldHead) ||
            !TryResolveCommit(executionDirectory, newHeadSha, out var newHead) ||
            !TryReadGit(executionDirectory, ["merge-base", "refs/heads/main", oldHead], out var oldBase) ||
            !TryReadGit(executionDirectory, ["merge-base", "refs/heads/main", newHead], out var newBase) ||
            !TryReadCommitCount(executionDirectory, oldBase, oldHead, out var oldCount) ||
            !TryReadCommitCount(executionDirectory, newBase, newHead, out var newCount) ||
            oldCount != newCount)
        {
            return false;
        }

        var rangeDiff = GitCli.Run(
            executionDirectory,
            "range-diff",
            "--no-color",
            $"{oldBase}..{oldHead}",
            $"{newBase}..{newHead}");
        if (!rangeDiff.Succeeded || rangeDiff.DrainTimedOut || string.IsNullOrWhiteSpace(rangeDiff.Output))
        {
            return false;
        }

        var headers = rangeDiff.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => RangeDiffHeaderPattern.Match(line))
            .Where(match => match.Success)
            .ToArray();
        if (headers.Length != oldCount || headers.Any(match =>
                match.Groups["operator"].Value != "=" ||
                !string.Equals(
                    match.Groups["left"].Value,
                    match.Groups["right"].Value,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        evidence = $"range-diff {oldBase}..{oldHead} vs {newBase}..{newHead}: {oldCount}/{newCount} commits have identical patch ids in order";
        return true;
    }

    private static bool TryResolveCommit(string executionDirectory, string sha, out string resolved)
    {
        resolved = string.Empty;
        return CommitShaPattern.IsMatch(sha) &&
            TryReadGit(executionDirectory, ["rev-parse", "--verify", "--quiet", $"{sha}^{{commit}}"], out resolved) &&
            CommitShaPattern.IsMatch(resolved);
    }

    private static bool TryReadCommitCount(
        string executionDirectory,
        string baseSha,
        string headSha,
        out int count)
    {
        count = 0;
        return TryReadGit(executionDirectory, ["rev-list", "--count", $"{baseSha}..{headSha}"], out var output) &&
            int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
            count is > 0 and <= PatchEquivalenceCommitLimit;
    }

    private static bool TryReadGit(string executionDirectory, string[] arguments, out string output)
    {
        var result = GitCli.Run(executionDirectory, arguments);
        output = result.Output.Trim();
        return result.Succeeded && !result.DrainTimedOut && !string.IsNullOrWhiteSpace(output);
    }
}
