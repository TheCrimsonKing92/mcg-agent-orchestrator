namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerGitContext
{
    private const int DiffSummaryMaxChars = 6000;
    internal const int ReviewerChangedFilePromptMaxFiles = 120;
    internal const string ReviewerScopeUnavailableErrorCode = "ERR_REVIEWER_SCOPE_UNAVAILABLE";
    internal const string ReviewerMergeBaseUnavailableErrorCode = "ERR_REVIEWER_MERGE_BASE_UNAVAILABLE";
    internal const string ReviewerMergeTreeUnavailableErrorCode = "ERR_REVIEWER_MERGE_TREE_UNAVAILABLE";
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
