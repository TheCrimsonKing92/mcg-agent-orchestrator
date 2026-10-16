using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SourceSizeRetightenedRow(string RelativePath, int OldCeiling, int NewCeiling,
    int? OldFileCount = null, int? NewFileCount = null, string? Rule = null, string? Field = null);

internal static class SourceSizeRatchetRetightener
{
    internal sealed record ClassBranch(string Authority,
        Func<string, (int TotalLines, int PartialFiles)?> Head,
        Func<string, (int TotalLines, int PartialFiles)?> MergeBase);

    internal sealed record ClassRaiseInputs(Func<string, int?> AddedLines,
        Func<string, (int TotalLines, int PartialFiles)?> Main, IReadOnlyList<ClassBranch> Branches);

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
        IReadOnlyDictionary<string, (int TotalLines, int PartialFiles)>? classMeasurements = null,
        ClassRaiseInputs? classRaises = null)
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
        var mainClasses = UniqueClassRows(mainAuthority);
        foreach (var group in ReadClassRows(integratedAuthority).GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal))
        {
            if (group.Count() != 1 || classMeasurements is null ||
                !classMeasurements.TryGetValue(group.Key, out var measured) || measured.PartialFiles == 0) continue;
            var match = group.Single();
            if (!TryNumber(match.Groups["total"], out var oldTotal) || !TryNumber(match.Groups["count"], out var oldCount)) continue;
            var total = Math.Min(oldTotal, measured.TotalLines);
            var count = Math.Min(oldCount, measured.PartialFiles);
            if (total != oldTotal) edits.Add((match.Groups["total"], total));
            if (count != oldCount) edits.Add((match.Groups["count"], count));
            if (total != oldTotal || count != oldCount)
                changed.Add(new($"class:{group.Key}", oldTotal, total, oldCount, count));
            if (classRaises is null || !mainClasses.TryGetValue(group.Key, out var mainClass)) continue;
            RaiseField("total", oldTotal, measured.TotalLines, null);
            RaiseField("count", oldCount, measured.PartialFiles, "files");

            void RaiseField(string field, int old, int actual, string? label)
            {
                if (actual <= old || !TryNumber(mainClass.Groups[field], out var mainLimit)) return;
                string? rule = null;
                if (old < mainLimit && TryNumber(mainClass.Groups["count"], out var mainCount) &&
                    measured.PartialFiles <= mainCount && classRaises.AddedLines(group.Key) == 0)
                    rule = "A";
                else if (old == mainLimit && IsBranchGrowth(group.Key, field, actual, classRaises))
                    rule = "B";
                if (rule is null) return;
                edits.Add((match.Groups[field], actual));
                changed.Add(new($"class:{group.Key}", old, actual, Rule: rule, Field: label));
            }
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
            var partialFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var measurements = SourceSizeRatchet.MeasureClasses(worktreeRoot,
                ReadClassRows(authority).Select(match => match.Groups["name"].Value), out var unreadable,
                partialFiles: partialFiles);
            rewritten = Rewrite(authority, main.Output,
                relative => CountLines(worktreeRoot, relative),
                relative => AddedLines(worktreeRoot, mainRevision, relative), out rows,
                unreadable.Count == 0 ? measurements : null,
                unreadable.Count == 0 ? CreateClassRaises(worktreeRoot, mainRevision, authority, measurements, partialFiles) : null);
            if (rows.Count == 0) return [];
            writeAttempted = true;
            File.WriteAllBytes(path, Utf8.GetBytes(rewritten));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (writeAttempted) RestoreAuthority(worktreeRoot);
            return [];
        }

        var body = string.Join("\n", rows.Select(row => row.Rule is not null
            ? $"class {row.RelativePath[6..]}{(row.Field is null ? "" : " files")}: {row.OldCeiling} -> {row.NewCeiling} (rule {row.Rule})"
            : $"{row.RelativePath}: {row.OldCeiling} -> {row.NewCeiling}" +
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
            ProgressSink(row.Rule is not null
                ? $"RATCHET_RETIGHTEN goal={subjectId} class={row.RelativePath[6..]}{(row.Field is null ? "" : " field=files")} old={row.OldCeiling} new={row.NewCeiling} rule={row.Rule}"
                : $"RATCHET_RETIGHTEN goal={subjectId} path={row.RelativePath} old={row.OldCeiling} new={row.NewCeiling}" +
                    (row.OldFileCount is null ? "" : $" oldFiles={row.OldFileCount} newFiles={row.NewFileCount}"));
        return rows;
    }

    private static Match[] ReadRows(string text) => RowPattern.Matches(text)
        .Where(match => match.Groups["path"].Success).ToArray();

    private static Match[] ReadClassRows(string text) => RowPattern.Matches(text)
        .Where(match => match.Groups["name"].Success).ToArray();

    private static Dictionary<string, Match> UniqueClassRows(string text) => ReadClassRows(text)
        .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal).Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

    private static bool IsBranchGrowth(string name, string field, int actual, ClassRaiseInputs inputs)
    {
        static int Value((int TotalLines, int PartialFiles) measurement, string field) =>
            field == "total" ? measurement.TotalLines : measurement.PartialFiles;
        if (inputs.Branches.Count == 0 || inputs.Main(name) is not { } main) return false;
        long delta = 0;
        foreach (var branch in inputs.Branches)
        {
            if (!UniqueClassRows(branch.Authority).TryGetValue(name, out var row) ||
                !TryNumber(row.Groups[field], out var limit) || branch.Head(name) is not { } head ||
                branch.MergeBase(name) is not { } basis || Value(head, field) > limit) return false;
            delta += (long)Value(head, field) - Value(basis, field);
        }
        return (long)actual - Value(main, field) == delta;
    }

    private static ClassRaiseInputs? CreateClassRaises(string root, string main, string authority,
        IReadOnlyDictionary<string, (int TotalLines, int PartialFiles)> measurements,
        Dictionary<string, List<string>> partialFiles)
    {
        var names = UniqueClassRows(authority).Where(pair => measurements.TryGetValue(pair.Key, out var value) &&
            TryNumber(pair.Value.Groups["total"], out var total) && TryNumber(pair.Value.Groups["count"], out var count) &&
            (value.TotalLines > total || value.PartialFiles > count)).Select(pair => pair.Key).ToArray();
        if (names.Length == 0) return null;
        // Revision evidence cannot authorise uncommitted or untracked source growth.
        var status = GitText(root, "status", "--porcelain", "--untracked-files=all", "--", "src");
        var mainSha = GitText(root, "rev-parse", "--verify", $"{main}^{{commit}}")?.Trim();
        if (status is null || !string.IsNullOrWhiteSpace(status) || string.IsNullOrEmpty(mainSha)) return null;
        var cache = new Dictionary<string, IReadOnlyDictionary<string, (int TotalLines, int PartialFiles)>?>(StringComparer.Ordinal);
        (int TotalLines, int PartialFiles)? Measure(string revision, string name)
        {
            if (!cache.TryGetValue(revision, out var values))
                cache[revision] = values = MeasureRevision(root, revision, names);
            return values is not null && values.TryGetValue(name, out var value) ? value : null;
        }
        var branches = new List<ClassBranch>();
        foreach (var head in ResolveBranchHeads(root, mainSha))
        {
            var basis = GitText(root, "merge-base", head, mainSha)?.Trim();
            var headAuthority = GitText(root, "show", $"{head}:{SourceSizeRatchet.SourcePath}");
            if (string.IsNullOrEmpty(basis) || headAuthority is null)
            {
                branches.Clear();
                break;
            }
            branches.Add(new(headAuthority, name => Measure(head, name), name => Measure(basis, name)));
        }
        return new(name =>
        {
            if (!partialFiles.TryGetValue(name, out var paths) || paths.Count == 0) return null;
            var total = 0;
            foreach (var path in paths)
            {
                var added = AddedLines(root, mainSha, path, allowUnchanged: true);
                if (added is null) return null;
                total += added.Value;
            }
            return total;
        }, name => Measure(mainSha, name), branches);
    }

    private static IReadOnlyDictionary<string, (int TotalLines, int PartialFiles)>? MeasureRevision(
        string root, string revision, string[] names)
    {
        // Grep is only a candidate prefilter; the shared helper still owns matching and counting.
        var args = new List<string> { "grep", "--full-name", "-l", "-z", "-F" };
        foreach (var name in names) args.AddRange(["-e", name]);
        args.AddRange([revision, "--", ":(glob)src/**/*.cs"]);
        var candidates = GitCli.Run(root, args.ToArray());
        if (candidates.DrainTimedOut || (!candidates.Succeeded && candidates.ExitCode != 1)) return null;
        if (!candidates.Succeeded && !string.IsNullOrEmpty(candidates.Error)) return null;
        var prefix = revision + ":";
        var paths = candidates.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Any(path => !path.StartsWith(prefix, StringComparison.Ordinal))) return null;
        var measured = SourceSizeRatchet.MeasureClasses(root, names, out var unreadable,
            path => ReadRevisionLines(root, revision, path),
            _ => paths.Select(path => path[prefix.Length..]));
        return unreadable.Count == 0 ? measured : null;
    }

    private static IEnumerable<string> ReadRevisionLines(string root, string revision, string path)
    {
        var content = GitText(root, "show", $"{revision}:{path}") ?? throw new IOException("Cannot read class revision evidence.");
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string[] ResolveBranchHeads(string root, string main)
    {
        static string[][] Commits(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var headText = GitText(root, "rev-list", "--parents", "-n", "1", "HEAD");
        if (headText is null) return [];
        var head = Commits(headText).SingleOrDefault();
        // A branch-first merge of main retains the pre-integration branch as its first parent.
        if (head is { Length: 3 } && head[2] == main) return [head[1]];
        var chainText = GitText(root, "rev-list", "--parents", "--first-parent", $"{main}..HEAD");
        if (chainText is null) return [];
        var chain = Commits(chainText);
        if (chain.Length == 0) return [];
        // Main-first partition/cohort merges preserve each branch as a second parent.
        if (chain.All(commit => commit.Length == 3) && chain[^1][1] == main)
            return chain.Select(commit => commit[2]).Distinct(StringComparer.Ordinal).ToArray();

        // A single rebase preserves the original head in ORIG_HEAD. Reject ambiguous histories,
        // squashes and multi-member compositions rather than guessing an authorisation source.
        if (chain.Any(commit => commit.Length != 2)) return [];
        var original = GitText(root, "rev-parse", "--verify", "ORIG_HEAD^{commit}")?.Trim();
        if (string.IsNullOrEmpty(original)) return [];
        var basis = GitText(root, "merge-base", original, main)?.Trim();
        if (string.IsNullOrEmpty(basis) || basis == original) return [];
        var originalCommits = GitText(root, "rev-list", "--parents", $"{basis}..{original}");
        if (originalCommits is null || Commits(originalCommits).Any(commit => commit.Length != 2)) return [];
        var before = GitText(root, "log", "--reverse", "--format=%s", $"{basis}..{original}");
        var after = GitText(root, "log", "--reverse", "--format=%s", $"{main}..HEAD");
        return !string.IsNullOrWhiteSpace(before) && before == after ? [original] : [];
    }

    private static string? GitText(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        return result.Succeeded && !result.DrainTimedOut ? result.Output : null;
    }

    private static bool TryNumber(Group number, out int value) => int.TryParse(
        number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryCeiling(Match match, out int ceiling) => int.TryParse(
        match.Groups["ceiling"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ceiling);

    private static int? CountLines(string root, string relative)
    {
        try { return File.ReadLines(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))).Count(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static int? AddedLines(string root, string main, string relative, bool allowUnchanged = false)
    {
        var result = GitCli.Run(root, "diff", "--numstat", "--no-renames", main, "HEAD", "--", relative);
        if (!result.Succeeded || result.DrainTimedOut) return null;
        var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (allowUnchanged && lines.Length == 0) return 0;
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
