using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Proves preserved goal changes across integration merges and numeric ratchet retightening.
public static class PassedHeadCandidateDrift
{
    public static bool TryProve(
        string executionDirectory, string boundSha, string headSha, string integrationBranch, out string evidence)
    {
        evidence = string.Empty;
        if (string.IsNullOrWhiteSpace(executionDirectory) || !Directory.Exists(executionDirectory) ||
            !Read(executionDirectory, ["rev-parse", "--verify", $"{boundSha}^{{commit}}"], out var bound) ||
            !Read(executionDirectory, ["rev-parse", "--verify", $"{headSha}^{{commit}}"], out var head) ||
            !Read(executionDirectory, ["rev-parse", "--verify", $"refs/heads/{integrationBranch}^{{commit}}"], out var integration))
            return false;

        var walked = new List<string>();
        var current = head;
        while (!string.Equals(current, bound, StringComparison.OrdinalIgnoreCase))
        {
            if (walked.Count == 200 ||
                !Read(executionDirectory, ["rev-list", "--parents", "-n", "1", current], out var parentLine))
                return false;
            var parents = parentLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length == 3)
            {
                var ancestor = GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", parents[2], integration);
                if (!ancestor.Succeeded || ancestor.DrainTimedOut) return false;
            }
            else if (parents.Length == 2)
            {
                if (!IsRatchetRetighten(executionDirectory, parents[1], current)) return false;
            }
            else return false;
            walked.Add(current);
            current = parents[1];
        }

        if (!Read(executionDirectory, ["merge-base", integration, bound], out var oldBase) ||
            !Read(executionDirectory, ["merge-base", integration, head], out var newBase) ||
            !GoalWorktrees.TryReadChangedLines(executionDirectory, oldBase, bound, out var oldLines) ||
            !GoalWorktrees.TryReadChangedLines(executionDirectory, newBase, head, out var newLines) ||
            !CompareChanges(oldLines, newLines)) return false;
        evidence = $"passed-at-head: bound={bound} head={head} walked={string.Join(',', walked)}";
        return true;
    }

    private static bool Read(string directory, string[] args, out string value)
    {
        var result = GitCli.Run(directory, args);
        value = result.Output.TrimEnd('\r', '\n');
        return result.Succeeded && !result.DrainTimedOut && value.Length > 0 && !value.Contains('\uFFFD');
    }

    private static bool IsRatchetRetighten(string directory, string parent, string head)
    {
        if (!Read(directory, ["diff", "--name-only", "--no-renames", parent, head], out var paths) ||
            paths != SourceSizeRatchet.SourcePath) return false;
        var before = GitCli.Run(directory, "show", $"{parent}:{SourceSizeRatchet.SourcePath}");
        var after = GitCli.Run(directory, "show", $"{head}:{SourceSizeRatchet.SourcePath}");
        return before.Succeeded && !before.DrainTimedOut && after.Succeeded && !after.DrainTimedOut &&
            !before.Output.Contains('\uFFFD') && !after.Output.Contains('\uFFFD') &&
            SourceSizeRatchetRetightener.IsNumericRetightenOnly(before.Output, after.Output) &&
            NonIncreasing(before.Output, after.Output);
    }

    private static bool CompareChanges(string[] oldLines, string[] newLines)
    {
        var oldSections = Sections(oldLines);
        var newSections = Sections(newLines);
        if (oldSections.Count != newSections.Count) return false;
        var ratchetHeader = $"diff --git a/{SourceSizeRatchet.SourcePath} b/{SourceSizeRatchet.SourcePath}";
        foreach (var (before, after) in oldSections.Zip(newSections))
        {
            if (before[0] != after[0] || before.Count != after.Count) return false;
            if (before[0].TrimEnd('\r') != ratchetHeader)
            {
                if (!before.SequenceEqual(after, StringComparer.Ordinal)) return false;
                continue;
            }
            for (var index = 1; index < before.Count; index++)
            {
                var oldLine = before[index];
                var newLine = after[index];
                if (oldLine == newLine) continue;
                if (oldLine.Length == 0 || newLine.Length == 0 || oldLine[0] != newLine[0] ||
                    oldLine[0] is not ('+' or '-') ||
                    oldLine.StartsWith("--- ", StringComparison.Ordinal) ||
                    oldLine.StartsWith("+++ ", StringComparison.Ordinal) ||
                    !SourceSizeRatchetRetightener.IsNumericRetightenOnly(oldLine[1..], newLine[1..]) ||
                    oldLine[0] == '+' && !NonIncreasing(oldLine[1..], newLine[1..])) return false;
            }
        }
        return true;
    }

    private static List<List<string>> Sections(string[] lines)
    {
        var sections = new List<List<string>>();
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) sections.Add([]);
            sections[^1].Add(line);
        }
        return sections;
    }

    private static bool NonIncreasing(string before, string after)
    {
        var oldNumbers = Regex.Matches(before, "[0-9]+").Select(match =>
            BigInteger.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        var newNumbers = Regex.Matches(after, "[0-9]+").Select(match =>
            BigInteger.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        return oldNumbers.Length > 0 && oldNumbers.Length == newNumbers.Length &&
            oldNumbers.Zip(newNumbers).All(pair => pair.Second <= pair.First);
    }
}
