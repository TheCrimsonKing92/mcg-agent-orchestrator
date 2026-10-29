using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Proves net candidate drift against one integration snapshot; subjects alone never prove content.
public static class IntegrationOnlyCandidateDrift
{
    public static bool TryProve(
        string executionDirectory, string boundSha, string candidateSha,
        string integrationBranch, string goalBranch, out string evidence)
    {
        evidence = string.Empty;
        if (string.IsNullOrWhiteSpace(executionDirectory) || !Directory.Exists(executionDirectory) ||
            !Read(executionDirectory, ["rev-parse", "--verify", $"{boundSha}^{{commit}}"], out var bound) ||
            !Read(executionDirectory, ["rev-parse", "--verify", $"{candidateSha}^{{commit}}"], out var candidate) ||
            !Read(executionDirectory, ["rev-parse", "--verify", $"refs/heads/{integrationBranch}^{{commit}}"], out var integration))
            return false;

        var walked = new List<string>();
        var head = candidate;
        while (!string.Equals(head, bound, StringComparison.OrdinalIgnoreCase))
        {
            if (walked.Count == 200 ||
                !Read(executionDirectory, ["rev-list", "--parents", "-n", "1", head], out var parentLine) ||
                !Read(executionDirectory, ["log", "-1", "--format=%s", head], out var subject)) return false;
            var parents = parentLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length == 3 && subject == $"Integrate {integrationBranch} into {goalBranch} before acceptance")
            {
                var ancestor = GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", parents[2], integration);
                if (!ancestor.Succeeded || ancestor.DrainTimedOut) return false;
            }
            else if (parents.Length == 2 && subject == SourceSizeRatchetRetightener.CommitSubject)
            {
                if (!Read(executionDirectory, ["diff", "--name-only", "--no-renames", parents[1], head], out var paths) ||
                    paths != SourceSizeRatchet.SourcePath) return false;
                var before = GitCli.Run(executionDirectory, "show", $"{parents[1]}:{SourceSizeRatchet.SourcePath}");
                var after = GitCli.Run(executionDirectory, "show", $"{head}:{SourceSizeRatchet.SourcePath}");
                if (!before.Succeeded || before.DrainTimedOut || !after.Succeeded || after.DrainTimedOut ||
                    before.Output.Contains('\uFFFD') || after.Output.Contains('\uFFFD') ||
                    !SourceSizeRatchetRetightener.IsNumericRetightenOnly(before.Output, after.Output)) return false;
            }
            else return false;
            walked.Add(head);
            head = parents[1];
        }

        if (!Read(executionDirectory, ["merge-base", integration, bound], out var oldBase) ||
            !Read(executionDirectory, ["merge-base", integration, candidate], out var newBase) ||
            !GoalWorktrees.TryReadChangedLines(executionDirectory, oldBase, bound, out var oldLines) ||
            !GoalWorktrees.TryReadChangedLines(executionDirectory, newBase, candidate, out var newLines) ||
            !CompareChanges(oldLines, newLines)) return false;
        evidence = $"integration-only: bound={bound} candidate={candidate} walked={string.Join(',', walked)}; O={oldBase}..{bound}; N={newBase}..{candidate}";
        return true;
    }

    private static bool Read(string directory, string[] args, out string value)
    {
        var result = GitCli.Run(directory, args);
        // Drop only Git's record terminator: exact subjects must retain other whitespace.
        value = result.Output.TrimEnd('\r', '\n');
        return result.Succeeded && !result.DrainTimedOut && value.Length > 0 && !value.Contains('\uFFFD');
    }

    internal static bool CompareChanges(string[] oldLines, string[] newLines)
    {
        var oldSections = Sections(oldLines);
        var newSections = Sections(newLines);
        var ratchetHeader = $"diff --git a/{SourceSizeRatchet.SourcePath} b/{SourceSizeRatchet.SourcePath}";
        // Compare whole sections, rather than removing identical line text shared by different files.
        if (!oldSections.Keys.Where(key => key != ratchetHeader).SequenceEqual(
                newSections.Keys.Where(key => key != ratchetHeader), StringComparer.Ordinal)) return false;
        foreach (var (header, lines) in oldSections)
            if (header != ratchetHeader && !lines.SequenceEqual(newSections[header], StringComparer.Ordinal)) return false;
        var oldRatchet = oldSections.GetValueOrDefault(ratchetHeader, []);
        var newRatchet = newSections.GetValueOrDefault(ratchetHeader, []);
        if (oldRatchet.SequenceEqual(newRatchet, StringComparer.Ordinal)) return true;
        if (!PairRows(oldRatchet, out var oldRows, out var oldRest) ||
            !PairRows(newRatchet, out var newRows, out var newRest)) return false;
        if (oldRatchet.Count == 0)
        {
            if (!newRest.Select(line => line.TrimEnd('\r')).SequenceEqual(new[] { ratchetHeader,
                    $"--- a/{SourceSizeRatchet.SourcePath}", $"+++ b/{SourceSizeRatchet.SourcePath}" }, StringComparer.Ordinal))
                return false;
        }
        else if (!oldRest.SequenceEqual(newRest, StringComparer.Ordinal)) return false;

        foreach (var (removed, added) in oldRows)
            if (!newRows.TryGetValue(removed, out var next) || !NonIncreasing(added, next, strict: false)) return false;
        foreach (var (removed, added) in newRows)
            if (!oldRows.ContainsKey(removed) && !NonIncreasing(removed, added, strict: true)) return false;
        return newRows.Count > 0;
    }

    private static Dictionary<string, List<string>> Sections(string[] lines)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                sections.Add(line, current = []);
            current!.Add(raw);
        }
        return sections;
    }

    // Non-row lines must survive verbatim. Numeric pairs must be unique in both directions.
    private static bool PairRows(
        List<string> lines, out Dictionary<string, string> rows, out List<string> rest)
    {
        rows = new(StringComparer.Ordinal);
        rest = [];
        var used = new HashSet<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith('-') || lines[index].StartsWith("--- ", StringComparison.Ordinal)) continue;
            var matches = Enumerable.Range(0, lines.Count).Where(other =>
                lines[other].StartsWith('+') && !lines[other].StartsWith("+++ ", StringComparison.Ordinal) &&
                SourceSizeRatchetRetightener.IsNumericRetightenOnly(lines[index][1..], lines[other][1..])).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1 || !used.Add(matches[0]) || !rows.TryAdd(lines[index][1..], lines[matches[0]][1..]))
                return false;
            used.Add(index);
        }
        rest = lines.Where((_, index) => !used.Contains(index)).ToList();
        return true;
    }

    private static bool NonIncreasing(string before, string after, bool strict)
    {
        if (before != after && !SourceSizeRatchetRetightener.IsNumericRetightenOnly(before, after)) return false;
        var oldNumbers = Regex.Matches(before, "[0-9]+").Select(match =>
            BigInteger.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        var newNumbers = Regex.Matches(after, "[0-9]+").Select(match =>
            BigInteger.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        return oldNumbers.Length == newNumbers.Length && oldNumbers.Length > 0 &&
            oldNumbers.Zip(newNumbers).All(pair => pair.Second <= pair.First) &&
            (!strict || oldNumbers.Zip(newNumbers).Any(pair => pair.Second < pair.First));
    }
}
