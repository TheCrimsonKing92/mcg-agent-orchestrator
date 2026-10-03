using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SourceSizeRetightenedRow(string RelativePath, int OldCeiling, int NewCeiling);

internal static class SourceSizeRatchetRetightener
{
    internal const string CommitSubject = "Retighten SourceSizeRatchet rows after integrating main";
    // Consume comments and strings first, so row-shaped text inside them cannot be rewritten.
    private static readonly Regex RowPattern = new(
        "//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|new\\s+SourceSizeCeiling\\(\\s*\"(?<path>[^\"]+)\"\\s*,\\s*(?<ceiling>\\d+)\\s*\\)|@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"",
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
        out IReadOnlyList<SourceSizeRetightenedRow> rows)
    {
        var integrated = ReadRows(integratedAuthority);
        var main = ReadRows(mainAuthority).GroupBy(match => match.Groups["path"].Value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var duplicates = integrated.GroupBy(match => match.Groups["path"].Value, StringComparer.Ordinal)
            .Where(group => group.Count() != 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var edits = new List<(Group Number, SourceSizeRetightenedRow Row)>();
        foreach (var match in integrated)
        {
            var path = match.Groups["path"].Value;
            if (duplicates.Contains(path) || !main.TryGetValue(path, out var baseline) ||
                !TryCeiling(match, out var oldCeiling) || !TryCeiling(baseline, out var mainCeiling) ||
                oldCeiling >= mainCeiling) continue;
            var count = integratedLineCount(path);
            if (count is null || count <= oldCeiling || addedLinesVersusMain(path) != 0) continue;
            edits.Add((match.Groups["ceiling"], new(path, oldCeiling, count.Value)));
        }
        var rewritten = new StringBuilder(integratedAuthority);
        foreach (var edit in edits.OrderByDescending(edit => edit.Number.Index))
        {
            rewritten.Remove(edit.Number.Index, edit.Number.Length);
            rewritten.Insert(edit.Number.Index, edit.Row.NewCeiling.ToString(CultureInfo.InvariantCulture));
        }
        rows = edits.Select(edit => edit.Row).OrderBy(row => row.RelativePath, StringComparer.Ordinal).ToArray();
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
            rewritten = Rewrite(Utf8.GetString(original), main.Output,
                relative => CountLines(worktreeRoot, relative),
                relative => AddedLines(worktreeRoot, mainRevision, relative), out rows);
            if (rows.Count == 0) return [];
            writeAttempted = true;
            File.WriteAllBytes(path, Utf8.GetBytes(rewritten));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (writeAttempted) RestoreAuthority(worktreeRoot);
            return [];
        }

        var body = string.Join("\n", rows.Select(row => $"{row.RelativePath}: {row.OldCeiling} -> {row.NewCeiling}"));
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
            ProgressSink($"RATCHET_RETIGHTEN goal={subjectId} path={row.RelativePath} old={row.OldCeiling} new={row.NewCeiling}");
        return rows;
    }

    private static Match[] ReadRows(string text) => RowPattern.Matches(text)
        .Where(match => match.Groups["path"].Success).ToArray();

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
        var oldRows = ReadRows(before);
        var newRows = ReadRows(after);
        if (oldRows.Length == 0 || oldRows.Length != newRows.Length) return false;
        var changed = false;
        for (var index = 0; index < oldRows.Length; index++)
        {
            if (oldRows[index].Groups["path"].Value != newRows[index].Groups["path"].Value ||
                !TryCeiling(oldRows[index], out var oldValue) || !TryCeiling(newRows[index], out var newValue) ||
                newValue < oldValue) return false;
            changed |= newValue > oldValue;
        }
        static string MaskNumbers(string text, Match[] matches)
        {
            var masked = new StringBuilder(text);
            foreach (var match in matches.Reverse())
            {
                var number = match.Groups["ceiling"];
                masked.Remove(number.Index, number.Length).Insert(number.Index, "#");
            }
            return masked.ToString();
        }
        return changed && MaskNumbers(before, oldRows) == MaskNumbers(after, newRows);
    }
}
