using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each fixture owns an in-memory kernel and fixed clock; no external resources are used.
public sealed class RoleRequirementIntegrationBranchTests
{
    [Fact]
    public void ReviewerBriefUsesProjectBranchInBothScopeSources()
    {
        var fixture = new BriefFixture(AgentRole.Reviewer, TaskComplexity.Simple);
        var text = Render(fixture.Build("master"));

        Assert.Contains("git diff --name-only master...HEAD", text);
        Assert.Contains("Independent scope checks must use git diff master...HEAD", text);
        Assert.Contains("Use git diff master...HEAD for scope", text);
        Assert.Contains("master..HEAD", text);
        Assert.Contains("--name-only master HEAD", text);
        Assert.Contains("current master", text);
        Assert.Contains("branch-behind-master", text);
        Assert.DoesNotContain("main...HEAD", text);
        Assert.DoesNotContain("main..HEAD", text);
        Assert.DoesNotContain("current main", text);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Simple)]
    [InlineData(AgentRole.Reviewer, TaskComplexity.Complex)]
    [InlineData(AgentRole.Developer, TaskComplexity.Simple)]
    [InlineData(AgentRole.Developer, TaskComplexity.Complex)]
    [InlineData(AgentRole.Tester, TaskComplexity.Simple)]
    [InlineData(AgentRole.Tester, TaskComplexity.Complex)]
    public void HomeAndUnspecifiedBranchesPreserveEntireBrief(AgentRole role, TaskComplexity complexity)
    {
        var fixture = new BriefFixture(role, complexity);
        var baseline = fixture.BuildWithParameterOmitted();
        if (role == AgentRole.Reviewer)
            Assert.Single(baseline.Segments, segment => segment.ReviewerChangedFiles is not null);

        foreach (var branch in new string?[] { "main", null, "", " \t " })
        {
            var actual = fixture.Build(branch);
            Assert.Equal(Render(baseline), Render(actual));
            Assert.Equal(Render(baseline, true), Render(actual, true));
        }
    }

    [Theory]
    [InlineData("main...HEAD", "master...HEAD")]
    [InlineData("main..HEAD", "master..HEAD")]
    [InlineData("git merge-tree --write-tree --name-only main HEAD", "git merge-tree --write-tree --name-only master HEAD")]
    [InlineData("current main", "current master")]
    [InlineData("Branch-behind-main", "Branch-behind-master")]
    [InlineData("branch-behind-main", "branch-behind-master")]
    public void ApplyRewritesEachNamedToken(string input, string expected)
    {
        Assert.Equal(new[] { expected }, Apply([input], "master"));
    }

    [Fact]
    public void ApplyPreservesEmbeddedWordsConceptualProseAndOrdinalCasing()
    {
        string[] lines =
        [
            "domain maintain domain...HEAD domain..HEAD",
            "fail against main; fail-on-main proof; observable on main; main conflicts",
            "Main...HEAD; Current main; BRANCH-behind-main; recurrent main"
        ];
        Assert.Same(lines, Apply(lines, "master"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("main")]
    public void ApplyReturnsOriginalListForHomeOrUnspecifiedBranch(string? branch)
    {
        string[] lines = ["main...HEAD; main..HEAD; current main"];
        Assert.Same(lines, Apply(lines, branch));
    }

    [Theory]
    [InlineData("release/v1.2+$1")]
    [InlineData("release/main..HEAD")]
    [InlineData("Main")]
    public void ApplyInsertsBranchLiterallyWithoutRescanning(string branch)
    {
        string[] lines =
        [
            "main...HEAD main..HEAD --name-only main HEAD current main Branch-behind-main branch-behind-main",
            "domain...HEAD maintain"
        ];
        var actual = Apply(lines, branch);
        Assert.Equal(new[]
        {
            $"{branch}...HEAD {branch}..HEAD --name-only {branch} HEAD current {branch} Branch-behind-{branch} branch-behind-{branch}",
            lines[1]
        }, actual);
        Assert.Equal("main...HEAD main..HEAD --name-only main HEAD current main Branch-behind-main branch-behind-main", lines[0]);
    }

    [Fact]
    public void RewriteLeavesOperatorSuppliedGoalTextUntouched()
    {
        const string supplied = "Operator example: git diff main...HEAD against current main.";
        var fixture = new BriefFixture(AgentRole.Reviewer, TaskComplexity.Simple, supplied);
        var text = Render(fixture.Build("master"));
        Assert.Contains(supplied, text);
        Assert.Contains("Independent scope checks must use git diff master...HEAD", text);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ScopeRewritePreservesPreviewCoordinates(bool clean, bool empty)
    {
        var fixture = new BriefFixture(AgentRole.Reviewer, TaskComplexity.Simple);
        var changed = empty ? Array.Empty<string>() : new[] { "src/A.cs", "src/B.cs" };
        var baseline = Scope(fixture.BuildScope(null, changed, clean));
        var actual = Scope(fixture.BuildScope("master", changed, clean));

        Assert.Equal(baseline.Lines.Count, actual.Lines.Count);
        Assert.Equal(string.Empty, actual.Lines[^1]);
        Assert.Contains($"Merge-tree status: {(clean ? "clean" : "conflicted")} against current master (git merge-tree --write-tree --name-only master HEAD).", actual.Lines);
        AssertPathCoordinates(baseline.ReviewerChangedFiles!, actual.ReviewerChangedFiles!, actual);
        var expectedPreview = empty
            ? new[] { "- No changed files reported by git diff --name-only master...HEAD." }
            : new[] { "- src/A.cs", "- src/B.cs", "- Omitted 3 additional changed file(s) to preserve prompt budget." };
        Assert.Equal(expectedPreview, Preview(actual, actual.ReviewerChangedFiles!));

        if (clean)
        {
            Assert.Null(actual.MergeConflictPaths);
        }
        else
        {
            AssertPathCoordinates(baseline.MergeConflictPaths!, actual.MergeConflictPaths!, actual);
            Assert.Equal(new[]
            {
                "- conflict: src/C.cs", "- conflict: src/D.cs",
                "- Omitted 2 additional conflict path(s) to preserve prompt budget."
            }, Preview(actual, actual.MergeConflictPaths!));
        }
    }

    private static void AssertPathCoordinates(TaskBriefPathList baseline, TaskBriefPathList actual, TaskBriefSegment segment)
    {
        Assert.Equal(baseline.Paths, actual.Paths);
        Assert.Equal(baseline.Total, actual.Total);
        Assert.Equal(baseline.PreviewBound, actual.PreviewBound);
        Assert.Equal(baseline.FirstListLineIndex, actual.FirstListLineIndex);
        Assert.Equal(baseline.ListLineCount, actual.ListLineCount);
        Assert.Equal(baseline.CountLineIndex, actual.CountLineIndex);
        Assert.Equal(baseline.CountLabel, actual.CountLabel);
        Assert.Equal($"{actual.CountLabel}: {actual.Total}; showing {actual.Paths.Count}.", segment.Lines[actual.CountLineIndex!.Value]);
    }

    private static string[] Preview(TaskBriefSegment segment, TaskBriefPathList paths) =>
        segment.Lines.Skip(paths.FirstListLineIndex).Take(paths.ListLineCount).ToArray();

    private static TaskBriefSegment Scope(TaskBriefSource source) =>
        Assert.Single(source.Segments, segment => segment.ReviewerChangedFiles is not null);

    private static string Render(TaskBriefSource source, bool typed = false) =>
        source.ProjectLegacyMarkedTextV1(typed).Content;

    private static IReadOnlyList<string> Apply(IReadOnlyList<string> lines, string? branch)
    {
        var type = typeof(AgentOrchestratorKernel).Assembly.GetType(
            "Mcg.AgentOrchestrator.Core.RoleRequirementIntegrationBranch", throwOnError: true)!;
        var method = type.GetMethod("Apply", [typeof(IReadOnlyList<string>), typeof(string)])!;
        return (IReadOnlyList<string>)method.Invoke(null, [lines, branch])!;
    }

    private sealed class BriefFixture
    {
        private readonly AgentOrchestratorKernel kernel = new(new FixedClock());
        private readonly Goal goal;
        private readonly TaskSpec task;

        public BriefFixture(AgentRole role, TaskComplexity complexity, string? suppliedGoalText = null)
        {
            task = new TaskSpec(TaskId.New(), complexity == TaskComplexity.Simple
                ? "Update the label."
                : "Implement authentication migration for worker persistence state across API and CLI services with integration tests.", role);
            goal = kernel.CreateGoal(suppliedGoalText ?? "Update the label.", [task]);
            Assert.Equal(complexity, TaskComplexityEstimator.Estimate(task.Description, goal.Objective, role));
        }

        public TaskBriefSource BuildWithParameterOmitted() => kernel.BuildTaskBriefSource(
            goal.Id, task.Id, reviewerScopeChangedFiles: ["src/A.cs"], reviewerScopeMergeBase: "base",
            reviewerMergeTreeClean: true);

        public TaskBriefSource Build(string? branch) => kernel.BuildTaskBriefSource(
            goal.Id, task.Id, reviewerScopeChangedFiles: ["src/A.cs"], reviewerScopeMergeBase: "base",
            reviewerMergeTreeClean: true, integrationBranchPromptName: branch);

        public TaskBriefSource BuildScope(string? branch, string[] changed, bool clean) => kernel.BuildTaskBriefSource(
            goal.Id, task.Id, reviewerScopeChangedFiles: changed, reviewerScopeMergeBase: "base",
            reviewerScopeTotalChangedFileCount: changed.Length == 0 ? 0 : 5, reviewerMergeTreeClean: clean,
            reviewerMergeTreeConflictPaths: ["src/C.cs", "src/D.cs"], reviewerMergeTreeTotalConflictPathCount: 4,
            integrationBranchPromptName: branch);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    }
}
