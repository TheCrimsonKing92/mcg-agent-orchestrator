using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SourceSizeRetightenedRow(string RelativePath, int OldCeiling, int NewCeiling,
    int? OldFileCount = null, int? NewFileCount = null);

internal static class SourceSizeRatchetRetightener
{
    internal const string CommitSubject = "Retighten SourceSizeRatchet rows after integrating main";
    // Consume comments and strings first, so row-shaped text inside them cannot be rewritten.
    private static readonly Regex RowPattern = new(
        "//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|(?<raw>\"{3,})[\\s\\S]*?\\k<raw>|" +
        "new\\s+SourceSizeCeiling\\(\\s*\"(?<path>[^\"]+)\"\\s*,\\s*(?<ceiling>\\d+)\\s*\\)|" +
        "new\\s+SourceClassCeiling\\(\\s*\"(?<name>[^\"]+)\"\\s*,\\s*(?<total>\\d+)\\s*,\\s*(?<count>\\d+)\\s*\\)|" +
        "@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'",
        RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly AsyncLocal<Action<string>?> ProgressOverride = new();

    internal static Action<string> ProgressSink
    {
        get => ProgressOverride.Value ?? Console.WriteLine;
        set => ProgressOverride.Value = value;
    }

    internal static string Rewrite(
        string integratedAuthority, string mainAuthority,
        Func<string, int?> integratedLineCount, Func<string, int?> addedLinesVersusMain,
        out IReadOnlyList<SourceSizeRetightenedRow> rows,
        IReadOnlyDictionary<string, (int TotalLines, int PartialFiles)>? classMeasurements = null)
    {
        var integrated = ReadRows(integratedAuthority);
        var main = ReadRows(mainAuthority).GroupBy(match => match.Groups["path"].Value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var duplicates = integrated.GroupBy(match => match.Groups["path"].Value, StringComparer.Ordinal)
            .Where(group => group.Count() != 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var edits = new List<(Group Number, int Value)>();
        var changed = new List<SourceSizeRetightenedRow>();
        foreach (var match in integrated)
        {
            var path = match.Groups["path"].Value;
            if (duplicates.Contains(path) || !TryCeiling(match, out var oldCeiling)) continue;
            var count = integratedLineCount(path);
            if (count is null) continue;
            if (count < oldCeiling)
            {
                edits.Add((match.Groups["ceiling"], count.Value));
                changed.Add(new(path, oldCeiling, count.Value));
                continue;
            }
            if (!main.TryGetValue(path, out var baseline) ||
                !TryCeiling(baseline, out var mainCeiling) ||
                oldCeiling >= mainCeiling) continue;
            if (count <= oldCeiling || addedLinesVersusMain(path) != 0) continue;
            edits.Add((match.Groups["ceiling"], count.Value));
            changed.Add(new(path, oldCeiling, count.Value));
        }
        foreach (var group in ReadClassRows(integratedAuthority).GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal))
        {
            if (group.Count() != 1 || classMeasurements is null ||
                !classMeasurements.TryGetValue(group.Key, out var measured) || measured.PartialFiles == 0) continue;
            var match = group.Single();
            if (!TryNumber(match.Groups["total"], out var oldTotal) || !TryNumber(match.Groups["count"], out var oldCount)) continue;
            var total = Math.Min(oldTotal, measured.TotalLines);
            var count = Math.Min(oldCount, measured.PartialFiles);
            if (total == oldTotal && count == oldCount) continue;
            if (total != oldTotal) edits.Add((match.Groups["total"], total));
            if (count != oldCount) edits.Add((match.Groups["count"], count));
            changed.Add(new($"class:{group.Key}", oldTotal, total, oldCount, count));
        }
        var rewritten = new StringBuilder(integratedAuthority);
        foreach (var edit in edits.OrderByDescending(edit => edit.Number.Index))
        {
            rewritten.Remove(edit.Number.Index, edit.Number.Length);
            rewritten.Insert(edit.Number.Index, edit.Value.ToString(CultureInfo.InvariantCulture));
        }
        rows = changed.ToArray();
        return rewritten.ToString();
    }

    internal static IReadOnlyList<SourceSizeRetightenedRow> RetightenAndCommit(
        string worktreeRoot, string mainRevision, string subjectId, DateTimeOffset? commitDate = null)
    {
        var path = Path.Combine(worktreeRoot, SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return [];
        var main = GitCli.Run(worktreeRoot, "show", $"{mainRevision}:{SourceSizeRatchet.SourcePath}");
        if (!main.Succeeded || main.DrainTimedOut) return [];
        var status = GitCli.Run(worktreeRoot, "status", "--porcelain", "--", SourceSizeRatchet.SourcePath);
        if (!status.Succeeded || status.DrainTimedOut || !string.IsNullOrWhiteSpace(status.Output)) return [];

        byte[] original;
        string rewritten;
        IReadOnlyList<SourceSizeRetightenedRow> rows;
        var writeAttempted = false;
        try
        {
            original = File.ReadAllBytes(path);
            var authority = Utf8.GetString(original);
            var measurements = SourceSizeRatchet.MeasureClasses(worktreeRoot,
                ReadClassRows(authority).Select(match => match.Groups["name"].Value), out var unreadable);
            rewritten = Rewrite(authority, main.Output,
                relative => CountLines(worktreeRoot, relative),
                relative => AddedLines(worktreeRoot, mainRevision, relative), out rows,
                unreadable.Count == 0 ? measurements : null);
            if (rows.Count == 0) return [];
            writeAttempted = true;
            File.WriteAllBytes(path, Utf8.GetBytes(rewritten));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (writeAttempted) RestoreAuthority(worktreeRoot);
            return [];
        }

        var body = string.Join("\n", rows.Select(row => $"{row.RelativePath}: {row.OldCeiling} -> {row.NewCeiling}" +
            (row.OldFileCount is null ? "" : $" lines, {row.OldFileCount} -> {row.NewFileCount} files")));
        string[] args = ["-c", "user.name=mcg-orchestrator", "-c", "user.email=mcg-orchestrator@localhost",
            "commit", "--only", "-m", CommitSubject, "-m", body, "--", SourceSizeRatchet.SourcePath];
        var commit = commitDate is null ? GitCli.Run(worktreeRoot, args)
            : GitCli.RunWithEnvironment(worktreeRoot, new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = FormattableString.Invariant($"@{commitDate.Value.ToUnixTimeSeconds()} +0000"),
                ["GIT_COMMITTER_DATE"] = FormattableString.Invariant($"@{commitDate.Value.ToUnixTimeSeconds()} +0000")
            }, args);
        if (!commit.Succeeded || commit.DrainTimedOut)
        {
            RestoreAuthority(worktreeRoot);
            return [];
        }
        foreach (var row in rows)
            ProgressSink($"RATCHET_RETIGHTEN goal={subjectId} path={row.RelativePath} old={row.OldCeiling} new={row.NewCeiling}" +
                (row.OldFileCount is null ? "" : $" oldFiles={row.OldFileCount} newFiles={row.NewFileCount}"));
        return rows;
    }

    private static Match[] ReadRows(string text) => RowPattern.Matches(text)
        .Where(match => match.Groups["path"].Success).ToArray();

    private static Match[] ReadClassRows(string text) => RowPattern.Matches(text)
        .Where(match => match.Groups["name"].Success).ToArray();

    private static bool TryNumber(Group number, out int value) => int.TryParse(
        number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryCeiling(Match match, out int ceiling) => int.TryParse(
        match.Groups["ceiling"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ceiling);

    private static int? CountLines(string root, string relative)
    {
        try { return File.ReadLines(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))).Count(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static int? AddedLines(string root, string main, string relative)
    {
        var result = GitCli.Run(root, "diff", "--numstat", "--no-renames", main, "HEAD", "--", relative);
        if (!result.Succeeded || result.DrainTimedOut) return null;
        var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        // Require a deletion-only numstat row; binary, unchanged or ambiguous output is ineligible.
        var fields = lines.Length == 1 ? lines[0].Split('\t') : [];
        return fields.Length == 3 && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added)
            ? added : null;
    }

    private static void RestoreAuthority(string root)
    {
        var restored = GitCli.Run(root, "restore", "--source=HEAD", "--staged", "--worktree", "--", SourceSizeRatchet.SourcePath);
        if (!restored.Succeeded || restored.DrainTimedOut)
            throw new InvalidOperationException($"Could not restore SourceSizeRatchet after retighten failure: {restored.Error}");
    }

    internal static bool IsNumericRetightenOnly(string before, string after)
    {
        static Match[] ReadAllRows(string text) => RowPattern.Matches(text)
            .Where(match => match.Groups["path"].Success || match.Groups["name"].Success).ToArray();
        var oldRows = ReadAllRows(before);
        var newRows = ReadAllRows(after);
        if (oldRows.Length == 0 || oldRows.Length != newRows.Length) return false;
        var changed = false;
        for (var index = 0; index < oldRows.Length; index++)
        {
            if (oldRows[index].Groups["path"].Value != newRows[index].Groups["path"].Value ||
                oldRows[index].Groups["name"].Value != newRows[index].Groups["name"].Value) return false;
            foreach (var key in new[] { "ceiling", "total", "count" })
            {
                var oldNumber = oldRows[index].Groups[key];
                var newNumber = newRows[index].Groups[key];
                if (oldNumber.Success != newNumber.Success) return false;
                if (!oldNumber.Success) continue;
                if (!TryNumber(oldNumber, out var oldValue) || !TryNumber(newNumber, out var newValue)) return false;
                changed |= newValue != oldValue;
            }
        }
        static string MaskNumbers(string text, Match[] matches)
        {
            var masked = new StringBuilder(text);
            foreach (var number in matches.SelectMany(match => new[] { match.Groups["ceiling"], match.Groups["total"], match.Groups["count"] })
                .Where(group => group.Success).OrderByDescending(group => group.Index))
            {
                masked.Remove(number.Index, number.Length).Insert(number.Index, "#");
            }
            return masked.ToString();
        }
        return changed && MaskNumbers(before, oldRows) == MaskNumbers(after, newRows);
    }
}
