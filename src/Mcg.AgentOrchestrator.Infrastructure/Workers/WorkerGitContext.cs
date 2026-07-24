using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerGitContext
{
    private const int DiffSummaryMaxChars = 6000;
    internal const int ReviewerChangedFilePromptMaxFiles = 120;
    internal const string ReviewerScopeUnavailableErrorCode = "ERR_REVIEWER_SCOPE_UNAVAILABLE";
    internal const string ReviewerMergeBaseUnavailableErrorCode = "ERR_REVIEWER_MERGE_BASE_UNAVAILABLE";
    internal const string ReviewerMergeTreeUnavailableErrorCode = "ERR_REVIEWER_MERGE_TREE_UNAVAILABLE";
    internal const string ReviewerRoundTouchScopeUnavailableErrorCode = "ERR_REVIEWER_ROUND_TOUCH_SCOPE_UNAVAILABLE";
    internal const int DiffSummaryRetrievalMaxChars = DiffSummaryMaxChars;

    internal string[] ReadChangedFilesForTestImpact(string workingDirectory)
    {
        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            return [];
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryRunGit(workingDirectory, ["diff", "--name-only", "HEAD", "--"], out var diffOutput))
        {
            foreach (var line in SplitGitOutput(diffOutput))
            {
                files.Add(line);
            }
        }

        if (TryRunGit(workingDirectory, ["status", "--short"], out var statusOutput))
        {
            foreach (var line in SplitGitOutput(statusOutput))
            {
                if (line.Length < 4)
                {
                    continue;
                }

                var path = line[3..].Trim();
                var renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
                files.Add(renameIndex >= 0 ? path[(renameIndex + 4)..] : path);
            }
        }

        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal ReviewerChangedFileScope ReadReviewerChangedFileScope(string workingDirectory)
    {
        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            throw new ReviewerChangedFileScopeException(
                ReviewerScopeUnavailableErrorCode,
                "Reviewer changed-file scope unavailable because the working directory is not a git workspace.");
        }

        var mainResult = GitCli.Run(workingDirectory, 5_000, "rev-parse", "--verify", "main^{commit}");
        if (!mainResult.Succeeded || string.IsNullOrWhiteSpace(mainResult.Output))
        {
            throw new ReviewerChangedFileScopeException(
                ReviewerScopeUnavailableErrorCode,
                "Reviewer changed-file scope unavailable because git ref 'main' could not be resolved.",
                mainResult.Error);
        }

        var mergeBaseResult = GitCli.Run(workingDirectory, 5_000, "merge-base", "main", "HEAD");
        if (!mergeBaseResult.Succeeded || string.IsNullOrWhiteSpace(mergeBaseResult.Output))
        {
            throw new ReviewerChangedFileScopeException(
                ReviewerMergeBaseUnavailableErrorCode,
                "Reviewer changed-file scope unavailable because git merge-base main HEAD could not be computed.",
                mergeBaseResult.Error);
        }

        var diffResult = GitCli.Run(workingDirectory, 5_000, "diff", "--name-only", "main...HEAD", "--");
        if (!diffResult.Succeeded)
        {
            throw new ReviewerChangedFileScopeException(
                ReviewerScopeUnavailableErrorCode,
                "Reviewer changed-file scope unavailable because git diff --name-only main...HEAD failed.",
                diffResult.Error);
        }

        var changedFiles = SplitGitOutput(diffResult.Output)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ReviewerChangedFileScope(
            mergeBaseResult.Output.Trim(),
            changedFiles.Take(ReviewerChangedFilePromptMaxFiles).ToArray(),
            changedFiles.Length);
    }

    internal IReadOnlyList<ReviewFindingLocation> ReadReviewerRoundTouchedAnchors(
        string workingDirectory,
        string? previousReviewedCommit,
        string? currentCommit,
        IReadOnlyList<ReviewFindingLocation> anchors)
    {
        if (anchors.Count == 0)
        {
            return [];
        }

        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            throw new ReviewerRoundTouchScopeException(
                ReviewerRoundTouchScopeUnavailableErrorCode,
                "Reviewer round touched-anchor scope unavailable because the working directory is not a git workspace.");
        }

        if (string.IsNullOrWhiteSpace(previousReviewedCommit) ||
            string.IsNullOrWhiteSpace(currentCommit))
        {
            throw new ReviewerRoundTouchScopeException(
                ReviewerRoundTouchScopeUnavailableErrorCode,
                "Reviewer round touched-anchor scope unavailable because the previous or current reviewed commit is missing.");
        }

        if (string.Equals(previousReviewedCommit, currentCommit, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var touched = new List<ReviewFindingLocation>();
        foreach (var fileGroup in anchors.GroupBy(anchor => anchor.File, StringComparer.OrdinalIgnoreCase))
        {
            var path = fileGroup.Key.Replace('\\', '/');
            var diff = GitCli.Run(
                workingDirectory,
                5_000,
                "diff",
                "--unified=0",
                previousReviewedCommit,
                currentCommit,
                "--",
                path);
            if (!diff.Succeeded)
            {
                throw new ReviewerRoundTouchScopeException(
                    ReviewerRoundTouchScopeUnavailableErrorCode,
                    $"Reviewer round touched-anchor scope unavailable because git diff failed for '{path}'.",
                    diff.Error);
            }

            if (string.IsNullOrWhiteSpace(diff.Output))
            {
                continue;
            }

            var ranges = ParseChangedLineRanges(diff.Output);
            var oldSource = ReadCommitFile(
                workingDirectory,
                previousReviewedCommit,
                path,
                allowMissing: diff.Output.Contains("--- /dev/null", StringComparison.Ordinal));
            var newSource = ReadCommitFile(
                workingDirectory,
                currentCommit,
                path,
                allowMissing: diff.Output.Contains("+++ /dev/null", StringComparison.Ordinal));
            foreach (var anchor in fileGroup)
            {
                if (AnchorScopeWasTouched(anchor, oldSource, newSource, ranges))
                {
                    touched.Add(anchor);
                }
            }
        }

        return touched;
    }

    internal ReviewerMergeTreeStatus ReadReviewerMergeTreeStatus(string workingDirectory)
    {
        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            throw new ReviewerMergeTreeStatusException(
                ReviewerMergeTreeUnavailableErrorCode,
                "Reviewer merge-tree status unavailable because the working directory is not a git workspace.");
        }

        var mergeTreeResult = GitCli.Run(workingDirectory, 5_000, "merge-tree", "--write-tree", "--name-only", "main", "HEAD");
        if (mergeTreeResult.ExitCode == 0)
        {
            return new ReviewerMergeTreeStatus(IsClean: true, [], 0);
        }

        var conflictPaths = ParseMergeTreeConflictPaths(mergeTreeResult.Output);
        if (mergeTreeResult.ExitCode == 1 && conflictPaths.Length > 0)
        {
            return new ReviewerMergeTreeStatus(
                IsClean: false,
                conflictPaths.Take(ReviewerChangedFilePromptMaxFiles).ToArray(),
                conflictPaths.Length);
        }

        throw new ReviewerMergeTreeStatusException(
            ReviewerMergeTreeUnavailableErrorCode,
            "Reviewer merge-tree status unavailable because git merge-tree --write-tree --name-only main HEAD failed.",
            string.Join(" ", [mergeTreeResult.Output.Trim(), mergeTreeResult.Error.Trim()]).Trim());
    }

    private static string[] ParseMergeTreeConflictPaths(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        var paths = new List<string>();
        var sawTree = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!sawTree)
            {
                if (IsFullSha(line))
                {
                    sawTree = true;
                }

                continue;
            }

            if (line.Length == 0 || IsMergeTreeDiagnostic(line))
            {
                break;
            }

            paths.Add(line);
        }

        return paths
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsFullSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsMergeTreeDiagnostic(string value) =>
        value.StartsWith("Auto-merging ", StringComparison.Ordinal) ||
        value.StartsWith("CONFLICT ", StringComparison.Ordinal);

    private static string[] SplitGitOutput(string output) =>
        output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ReadCommitFile(
        string workingDirectory,
        string commit,
        string path,
        bool allowMissing)
    {
        if (allowMissing)
        {
            return string.Empty;
        }

        var result = GitCli.Run(workingDirectory, 5_000, "show", $"{commit}:{path}");
        if (result.Succeeded)
        {
            return result.Output;
        }

        throw new ReviewerRoundTouchScopeException(
            ReviewerRoundTouchScopeUnavailableErrorCode,
            $"Reviewer round touched-anchor scope unavailable because '{path}' could not be read at commit '{commit}'.",
            result.Error);
    }

    private static IReadOnlyList<ChangedLineRange> ParseChangedLineRanges(string diff)
    {
        var ranges = new List<ChangedLineRange>();
        foreach (var rawLine in diff.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!rawLine.StartsWith("@@ ", StringComparison.Ordinal))
            {
                continue;
            }

            var secondMarker = rawLine.IndexOf(" @@", 3, StringComparison.Ordinal);
            var header = secondMarker < 0 ? rawLine : rawLine[..secondMarker];
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 ||
                !TryParseRange(parts[1], '-', out var oldStart, out var oldCount) ||
                !TryParseRange(parts[2], '+', out var newStart, out var newCount))
            {
                continue;
            }

            ranges.Add(new ChangedLineRange(oldStart, oldCount, newStart, newCount));
        }

        return ranges;
    }

    private static bool TryParseRange(string value, char prefix, out int start, out int count)
    {
        start = 0;
        count = 0;
        if (value.Length < 2 || value[0] != prefix)
        {
            return false;
        }

        var parts = value[1..].Split(',', 2);
        if (!int.TryParse(parts[0], out start))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            count = 1;
            return true;
        }

        return int.TryParse(parts[1], out count);
    }

    private static bool AnchorScopeWasTouched(
        ReviewFindingLocation anchor,
        string oldSource,
        string newSource,
        IReadOnlyList<ChangedLineRange> changedRanges)
    {
        var oldScopes = FindStructuralScopes(oldSource, anchor);
        var newScopes = FindStructuralScopes(newSource, anchor);
        return changedRanges.Any(change =>
            oldScopes.Any(scope => scope.Overlaps(change.OldStart, change.OldCount)) ||
            newScopes.Any(scope => scope.Overlaps(change.NewStart, change.NewCount)));
    }

    private static IReadOnlyList<SourceLineRange> FindStructuralScopes(
        string source,
        ReviewFindingLocation anchor)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var regionPath = anchor.Region
            .Split(['.', ':', '#', '/', '\\', ' ', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        var regionToken = regionPath.LastOrDefault();
        var matches = FindTokenLines(lines, regionToken, requireDeclaration: true);
        if (matches.Count > 0 && regionPath.Length > 1)
        {
            var ownerTokens = regionPath[..^1];
            matches = matches
                .Where(line => ownerTokens.All(owner =>
                    FindTokenLines(lines, owner, requireDeclaration: true)
                        .Select(ownerLine => FindBraceScope(lines, ownerLine))
                        .Any(scope => scope.Contains(line + 1))))
                .ToList();
        }

        var regionScopes = matches
            .Select(line => FindBraceScope(lines, line))
            .Distinct()
            .ToArray();
        if (string.IsNullOrWhiteSpace(anchor.Hunk))
        {
            return regionScopes;
        }

        var hunkMatches = FindTokenLines(lines, anchor.Hunk, requireDeclaration: false);
        if (regionScopes.Length > 0)
        {
            hunkMatches = hunkMatches
                .Where(line => regionScopes.Any(scope => scope.Contains(line + 1)))
                .ToList();
        }

        return hunkMatches
            .Select(line => new SourceLineRange(line + 1, line + 1))
            .Distinct()
            .ToArray();
    }

    private static List<int> FindTokenLines(string[] lines, string? token, bool requireDeclaration)
    {
        var matches = new List<int>();
        if (string.IsNullOrWhiteSpace(token))
        {
            return matches;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var tokenIndex = IndexOfIdentifier(line, token);
            if (tokenIndex < 0)
            {
                continue;
            }

            if (requireDeclaration)
            {
                var prefix = line[..tokenIndex];
                var declarationLike =
                    prefix.Contains("class ", StringComparison.Ordinal) ||
                    prefix.Contains("record ", StringComparison.Ordinal) ||
                    prefix.Contains("struct ", StringComparison.Ordinal) ||
                    prefix.Contains("interface ", StringComparison.Ordinal) ||
                    prefix.Contains("namespace ", StringComparison.Ordinal) ||
                    prefix.Contains("public ", StringComparison.Ordinal) ||
                    prefix.Contains("private ", StringComparison.Ordinal) ||
                    prefix.Contains("protected ", StringComparison.Ordinal) ||
                    prefix.Contains("internal ", StringComparison.Ordinal);
                if (!declarationLike)
                {
                    continue;
                }
            }

            matches.Add(index);
        }

        return matches;
    }

    private static int IndexOfIdentifier(string line, string token)
    {
        var searchStart = 0;
        while (searchStart < line.Length)
        {
            var index = line.IndexOf(token, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return -1;
            }

            var beforeIsIdentifier = index > 0 && IsIdentifierCharacter(line[index - 1]);
            var afterIndex = index + token.Length;
            var afterIsIdentifier = afterIndex < line.Length && IsIdentifierCharacter(line[afterIndex]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
            {
                return index;
            }

            searchStart = index + token.Length;
        }

        return -1;
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static SourceLineRange FindBraceScope(string[] lines, int anchorLine)
    {
        var openLine = -1;
        for (var index = anchorLine; index < Math.Min(lines.Length, anchorLine + 8); index++)
        {
            if (lines[index].Contains('{'))
            {
                openLine = index;
                break;
            }
        }

        if (openLine < 0)
        {
            return new SourceLineRange(anchorLine + 1, anchorLine + 1);
        }

        var depth = 0;
        for (var index = openLine; index < lines.Length; index++)
        {
            depth += lines[index].Count(ch => ch == '{');
            depth -= lines[index].Count(ch => ch == '}');
            if (depth <= 0)
            {
                return new SourceLineRange(anchorLine + 1, index + 1);
            }
        }

        return new SourceLineRange(anchorLine + 1, lines.Length);
    }

    internal string BuildDiffSummary(string workingDirectory)
    {
        var lines = new List<string>
        {
            "# Diff Summary",
            string.Empty,
            $"Working directory: {workingDirectory}",
            "Regeneration: generated at dispatch preparation; treat as stale when git status or HEAD changes after dispatch.",
            string.Empty,
            "## Git Status"
        };

        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            lines.Add("- git data unavailable for this workspace.");
            lines.Add(string.Empty);
            lines.Add("## Changed Files Against HEAD");
            lines.Add("- git data unavailable for this workspace.");
            lines.Add(string.Empty);
            lines.Add("## Diff Stat Against HEAD");
            lines.Add("- git data unavailable for this workspace.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.AddRange(ReadGitSection(workingDirectory, ["status", "--short"]));
        lines.Add(string.Empty);
        lines.Add("## Changed Files Against HEAD");
        lines.AddRange(ReadGitSection(workingDirectory, ["diff", "--name-status", "HEAD", "--"]));
        lines.Add(string.Empty);
        lines.Add("## Diff Stat Against HEAD");
        lines.AddRange(ReadGitSection(workingDirectory, ["diff", "--stat", "HEAD", "--"]));

        return WorkerContextHelpers.TrimArtifactBlock(string.Join(Environment.NewLine, lines), DiffSummaryMaxChars);
    }

    private static bool LooksLikeGitWorkspace(string workingDirectory)
    {
        return Directory.Exists(Path.Combine(workingDirectory, ".git")) ||
            File.Exists(Path.Combine(workingDirectory, ".git"));
    }

    private static List<string> ReadGitSection(string workingDirectory, string[] arguments)
    {
        if (!TryRunGit(workingDirectory, arguments, out var output))
        {
            return ["- git data unavailable for this workspace."];
        }

        var lines = output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(80)
            .Select(line => $"- {line}")
            .ToList();
        return lines.Count == 0 ? ["- clean"] : lines;
    }

    private static bool TryRunGit(string workingDirectory, string[] arguments, out string output)
    {
        var result = GitCli.Run(workingDirectory, 5_000, arguments);
        output = result.Output;
        return result.Succeeded;
    }
}

internal sealed record ChangedLineRange(int OldStart, int OldCount, int NewStart, int NewCount);

internal sealed record SourceLineRange(int Start, int End)
{
    public bool Contains(int line) => line >= Start && line <= End;

    public bool Overlaps(int changedStart, int changedCount)
    {
        if (changedCount <= 0)
        {
            return false;
        }

        var changedEnd = changedStart + changedCount - 1;
        return changedStart <= End && changedEnd >= Start;
    }
}

internal sealed record ReviewerChangedFileScope(
    string MergeBase,
    IReadOnlyList<string> ChangedFiles,
    int TotalChangedFileCount)
{
    public bool Truncated => ChangedFiles.Count < TotalChangedFileCount;
}

internal sealed record ReviewerMergeTreeStatus(
    bool IsClean,
    IReadOnlyList<string> ConflictPaths,
    int TotalConflictPathCount)
{
    public bool Truncated => ConflictPaths.Count < TotalConflictPathCount;
}

internal sealed class ReviewerChangedFileScopeException : InvalidOperationException
{
    public ReviewerChangedFileScopeException(string errorCode, string message, string? detail = null)
        : base(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail.Trim()}")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

internal sealed class ReviewerMergeTreeStatusException : InvalidOperationException
{
    public ReviewerMergeTreeStatusException(string errorCode, string message, string? detail = null)
        : base(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail.Trim()}")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

internal sealed class ReviewerRoundTouchScopeException : InvalidOperationException
{
    public ReviewerRoundTouchScopeException(string errorCode, string message, string? detail = null)
        : base(string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail.Trim()}")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
