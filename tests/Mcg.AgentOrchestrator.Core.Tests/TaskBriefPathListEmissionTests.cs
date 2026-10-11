using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every fixture is an in-memory kernel with no shared state or I/O.
public sealed class TaskBriefPathListEmissionTests
{
    [Xunit.Theory]
    [Xunit.InlineData(0, 0)]
    [Xunit.InlineData(0, 125)]
    [Xunit.InlineData(2, 2)]
    [Xunit.InlineData(120, 120)]
    [Xunit.InlineData(125, 125)]
    public void ScopePreservesOrderedInputsAndExistingPreview(int length, int total)
    {
        var paths = Enumerable.Range(0, length).Select(i => $"src/file-{i:D3}.cs").ToArray();
        var input = new[] { "", " " }.Concat(paths).ToArray();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review scope", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve scope", [task]);
        var source = kernel.BuildTaskBriefSource(goal.Id, task.Id,
            reviewerScopeChangedFiles: input, reviewerScopeMergeBase: "base",
            reviewerScopeTotalChangedFileCount: total, reviewerMergeTreeClean: false,
            reviewerMergeTreeConflictPaths: input, reviewerMergeTreeTotalConflictPathCount: total);
        var segment = Assert.Single(source.Segments, s => s.ReviewerChangedFiles is not null);
        var changed = segment.ReviewerChangedFiles!;
        var conflicts = segment.MergeConflictPaths!;
        var preview = paths.Take(120).ToArray();

        Assert.Equal(input, changed.Paths);
        Assert.Equal(input, conflicts.Paths);
        Assert.Equal(total, changed.Total);
        Assert.Equal(total, conflicts.Total);
        Assert.Equal(120, changed.PreviewBound);
        Assert.Equal(120, conflicts.PreviewBound);
        Assert.Null(segment.CollapsedLines);
        Assert.Equal($"Changed files: {total}; showing {preview.Length}.",
            segment.Lines[changed.CountLineIndex!.Value]);
        Assert.Equal($"Conflicting paths: {total}; showing {preview.Length}.",
            segment.Lines[conflicts.CountLineIndex!.Value]);
        var expectedChanged = preview.Length == 0
            ? new List<string> { "- No changed files reported by git diff --name-only main...HEAD." }
            : preview.Select(p => $"- {p}").ToList();
        var expectedConflicts = preview.Select(p => $"- conflict: {p}").ToList();
        if (preview.Length < total)
        {
            expectedChanged.Add($"- Omitted {total - preview.Length} additional changed file(s) to preserve prompt budget.");
            expectedConflicts.Add($"- Omitted {total - preview.Length} additional conflict path(s) to preserve prompt budget.");
        }
        Assert.Equal(expectedChanged, segment.Lines.Skip(changed.FirstListLineIndex).Take(changed.ListLineCount));
        Assert.Equal(expectedConflicts, segment.Lines.Skip(conflicts.FirstListLineIndex).Take(conflicts.ListLineCount));
        Assert.Equal("## Reviewer Changed-File Scope", segment.Lines[0]);
        Assert.Equal("Merge base: base", segment.Lines[2]);
        Assert.Equal("Merge-tree status: conflicted against current main (git merge-tree --write-tree --name-only main HEAD).", segment.Lines[4]);
        Assert.StartsWith("Staleness policy:", segment.Lines[changed.FirstListLineIndex - 1], StringComparison.Ordinal);
        Assert.StartsWith("Independent scope checks", segment.Lines[changed.FirstListLineIndex + changed.ListLineCount], StringComparison.Ordinal);
        Assert.Equal(string.Empty, segment.Lines[^1]);
    }

    [Xunit.Fact]
    public void ConvergenceCarriesEveryPathWithoutAPreviewBound()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review convergence", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve convergence", [task]);
        kernel.ActivateGoal(goal.Id, [new AgentDefinition(AgentId.New(), "Reviewer", AgentRole.Reviewer,
            new ModelProfile("OpenAI", "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, "high"))]);
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 1,
            """
            WORKER_RESULT:
            findings: [{"stable_id":"scope","state":"open","severity":"blocking","location":{"file":"src/A.cs","region":"A.Run"},"description":"Recheck A"}]
            touched_anchors: []
            END_WORKER_RESULT
            """, string.Empty, DateTimeOffset.UnixEpoch,
            MergedReviewFindings: [new ReviewFinding("scope", ReviewFindingState.Open,
                new ReviewFindingLocation("src/A.cs", "A.Run"), "Recheck A")], WorkerResultPresent: true));
        var paths = Enumerable.Range(0, 125).Select(i => $"src/{i}.cs").ToArray();
        var source = kernel.BuildTaskBriefSource(goal.Id, task.Id, reviewerScopeChangedFiles: paths);
        var segment = Assert.Single(source.Segments, s => s.ConvergenceChangedFiles is not null);
        var list = segment.ConvergenceChangedFiles!;

        Assert.Equal(paths, list.Paths);
        Assert.Equal(125, list.Total);
        Assert.Null(list.PreviewBound);
        Assert.Null(list.CountLineIndex);
        Assert.Equal(paths.Select(p => $"- {p}"), segment.Lines.Skip(list.FirstListLineIndex).Take(list.ListLineCount));
        Assert.StartsWith("GOAL_DIFF_CHANGED_FILES ", segment.Lines[list.FirstListLineIndex - 1], StringComparison.Ordinal);
        Assert.StartsWith("Actively check ", segment.Lines[list.FirstListLineIndex + list.ListLineCount], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PathListSnapshotsInputsAndRejectsInvalidCoordinates()
    {
        var paths = new[] { "first", "second" };
        var list = new TaskBriefPathList(paths, 2, 120, 1, 2, 3, 0, "Changed files");
        paths[0] = "mutated";
        Assert.Equal(new[] { "first", "second" }, list.Paths);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskBriefPathList(paths, 2, 120, 2, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskBriefPathList(paths, 2, 120, 1, 2, 3, 1, "Changed files"));
    }
}
