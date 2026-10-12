namespace Mcg.AgentOrchestrator.Core;

// Owns the reviewer changed-file and conflict previews together with their typed line coordinates.
internal static class ReviewerChangedFileScopeBriefSection
{
    private const int ReviewerChangedFileScopeMaxLines = 120;

    internal static void AppendTo(
        ICollection<TaskBriefSegment> segments, TaskBriefSegment? scope, string? integrationBranch)
    {
        if (scope is null)
            return;

        // Substitution preserves line order/count, so the scope's typed coordinates remain valid.
        segments.Add(scope with { Lines = RoleRequirementIntegrationBranch.Apply(scope.Lines, integrationBranch) });
    }

    internal static TaskBriefSegment? Build(
        TaskSpec task,
        IReadOnlyList<string>? changedFiles,
        string? mergeBase,
        int? totalChangedFileCount,
        bool? mergeTreeClean,
        IReadOnlyList<string>? mergeTreeConflictPaths,
        int? totalMergeTreeConflictPathCount)
    {
        if (task.RequiredRole != AgentRole.Reviewer || (changedFiles is null && !mergeTreeClean.HasValue))
        {
            return null;
        }

        var boundedChangedFiles = (changedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Take(ReviewerChangedFileScopeMaxLines)
            .ToArray();
        var total = Math.Max(totalChangedFileCount ?? boundedChangedFiles.Length, boundedChangedFiles.Length);
        var lines = new List<string>
        {
            "## Reviewer Changed-File Scope",
            "Authoritative dispatch-preparation scope: git diff --name-only main...HEAD using three-dot merge-base semantics against current main.",
            $"Merge base: {(string.IsNullOrWhiteSpace(mergeBase) ? "unknown" : mergeBase.Trim())}",
            $"Changed files: {total}; showing {boundedChangedFiles.Length}."
        };

        var conflictTotal = 0;
        int? conflictCountLineIndex = null;
        var firstConflictLineIndex = 0;
        var conflictLineCount = 0;

        if (mergeTreeClean is true)
        {
            lines.Add("Merge-tree status: clean against current main (git merge-tree --write-tree --name-only main HEAD).");
        }
        else if (mergeTreeClean is false)
        {
            var boundedConflictPaths = (mergeTreeConflictPaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(ReviewerChangedFileScopeMaxLines)
                .ToArray();
            conflictTotal = Math.Max(totalMergeTreeConflictPathCount ?? boundedConflictPaths.Length, boundedConflictPaths.Length);
            lines.Add("Merge-tree status: conflicted against current main (git merge-tree --write-tree --name-only main HEAD).");
            conflictCountLineIndex = lines.Count;
            lines.Add($"Conflicting paths: {conflictTotal}; showing {boundedConflictPaths.Length}.");
            firstConflictLineIndex = lines.Count;
            foreach (var path in boundedConflictPaths)
            {
                lines.Add($"- conflict: {path}");
            }

            if (boundedConflictPaths.Length < conflictTotal)
            {
                lines.Add($"- Omitted {conflictTotal - boundedConflictPaths.Length} additional conflict path(s) to preserve prompt budget.");
            }
            conflictLineCount = lines.Count - firstConflictLineIndex;
        }

        lines.Add("Staleness policy: branch-behind-main alone is NOT a blocker; the deterministic acceptance gate rebases and verifies the integrated result. Staleness may block only with concrete integration-risk evidence: merge-tree conflicts, semantic overlap with landed changes in the same files, or a diff that no longer applies. Otherwise record staleness as advisory.");
        var firstChangedLineIndex = lines.Count;

        if (boundedChangedFiles.Length == 0)
        {
            lines.Add("- No changed files reported by git diff --name-only main...HEAD.");
        }
        else
        {
            foreach (var path in boundedChangedFiles)
            {
                lines.Add($"- {path}");
            }
        }

        if (boundedChangedFiles.Length < total)
        {
            lines.Add($"- Omitted {total - boundedChangedFiles.Length} additional changed file(s) to preserve prompt budget.");
        }

        var changedLineCount = lines.Count - firstChangedLineIndex;
        lines.Add("Independent scope checks must use git diff main...HEAD. Do not use two-dot diffs such as main..HEAD, git diff HEAD, git status, or working-tree-only comparisons as scope verdict evidence.");
        lines.Add(string.Empty);
        return new TaskBriefSegment(lines,
            ReviewerChangedFiles: new TaskBriefPathList(
                changedFiles ?? [], total, ReviewerChangedFileScopeMaxLines,
                firstChangedLineIndex, changedLineCount, lines.Count, 3, "Changed files"),
            MergeConflictPaths: conflictCountLineIndex.HasValue
                ? new TaskBriefPathList(
                    mergeTreeConflictPaths ?? [], conflictTotal, ReviewerChangedFileScopeMaxLines,
                    firstConflictLineIndex, conflictLineCount, lines.Count, conflictCountLineIndex, "Conflicting paths")
                : null);
    }
}
