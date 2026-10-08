using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: every test owns its real repository, worktree, configuration and event sink.
public sealed class GoalWorktreeTestsRebaseRegistryAwareMerge
{
    private const string A = "src/A.cs";
    private const string B = "src/B.cs";
    private const string C = "src/C.cs";
    private const string GuardedText = "// guarded\n";

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public void DifferentKeys_MergesAllChangesWithCommentsInBaseOrder(bool bom, string lineEnding)
    {
        var baseline = Registry(Row(A, 10, "base A") + Row(B, 10, "base B") + Row(C, 10, "base C"));
        var main = Registry(Row(A, 11, "main A") + Row(B, 12, "main B") + Row(C, 10, "base C"));
        var goal = Registry(Row(A, 10, "base A") + Row(B, 10, "base B") + Row(C, 13, "goal C"));
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, baseline, main, goal), Guard(A), Guard(B), Guard(C)
        ], bom: bom, lineEnding: lineEnding);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        AssertMerged(fixture, result, events);
        var expected = Registry(Row(A, 11, "main A") + Row(B, 12, "main B") + Row(C, 13, "goal C"));
        Assert.Equal(expected, fixture.Read(SourceSizeRatchet.SourcePath));
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Worktree, SourceSizeRatchet.SourcePath));
        Assert.Equal(bom, bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var encoded = new UTF8Encoding(bom).GetBytes(expected.ReplaceLineEndings(lineEnding));
        Assert.Equal(encoded, bytes[(bom ? 3 : 0)..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameKey_MeasuresResolvedTreeAndKeepsBothJustifications(bool sourceConflict)
    {
        var mainWidget = sourceConflict ? MainText : BaseText.Replace("class Widget", "// main\nclass Widget");
        var goalWidget = sourceConflict ? GoalText : BaseText + "// goal\n";
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(WidgetPath, 3, "base")),
                Registry(Row(WidgetPath, 4, "shared", "main")), Registry(Row(WidgetPath, 4, "shared", "goal"))),
            new(WidgetPath, BaseText, mainWidget, goalWidget)
        ]);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        AssertMerged(fixture, result, events);
        var expectedSource = sourceConflict ? MergedText : "// main\n" + BaseText + "// goal\n";
        Assert.Equal(expectedSource, fixture.Read());
        var count = File.ReadLines(Path.Combine(fixture.Worktree, WidgetPath)).Count();
        Assert.Equal(Registry(Row(WidgetPath, count, "shared", "main", "goal")), fixture.Read(SourceSizeRatchet.SourcePath));
        Assert.Equal(count, Assert.Single(SourceSizeRatchetPreflight.TryReadAuthority(fixture.Worktree)!).MaximumLineCount);
    }

    [Fact]
    public void BelowMeasuredRow_RefusesAndRestoresOriginalCleanBranch()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(WidgetPath, 3, "base") + Row(B, 10, "base B")),
                Registry(Row(WidgetPath, 4, "main growth") + Row(B, 10, "base B")),
                Registry(Row(WidgetPath, 3, "base") + Row(B, 11, "goal B"))),
            new(WidgetPath, BaseText, MainText, GoalText), Guard(B)
        ]);

        AssertRefused(fixture, "registry-ceiling-below-measured");
    }

    [Fact]
    public void DivergentClassEntry_RefusesWithRegistryReasonAndRestoresBranch()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10), ClassRow(10)),
                Registry(Row(A, 10), ClassRow(11)), Registry(Row(A, 10), ClassRow(12))), Guard(A)
        ]);

        AssertRefused(fixture, "registry-same-key-conflict");
    }

    [Fact]
    public void DeleteVersusModify_RefusesRatherThanReinstatingRemovedGuard()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10) + Row(B, 10)),
                Registry(Row(A, 11, "main") + Row(B, 10)), Registry(Row(B, 10))), Guard(A), Guard(B)
        ]);

        AssertRefused(fixture, "registry-same-key-conflict");
    }

    [Fact]
    public void MissingRemeasuredFile_RefusesAndRestoresBranch()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10)),
                Registry(Row(A, 11, "main")), Registry(Row(A, 12, "goal")))
        ]);

        AssertRefused(fixture, "registry-same-key-conflict");
    }

    [Fact]
    public void OutsideEntryConflict_FallsBackToExistingNonEmptyBaseRefusal()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10)),
                Registry(Row(A, 10)).Replace("// file header", "// main header"),
                Registry(Row(A, 10)).Replace("// file header", "// goal header")), Guard(A)
        ]);

        AssertRefused(fixture, "non-empty-base");
    }

    [Fact]
    public void FrozenRegistry_StillRefusesBeforeRegistryReconciliation()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10)),
                Registry(Row(A, 11)), Registry(Row(A, 12))), Guard(A)
        ]);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId,
            new([SourceSizeRatchet.SourcePath], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        fixture.AssertRestored();
        Assert.Contains("result=refused", Assert.Single(events));
        Assert.Contains("reason=frozen-path", events[0]);
    }

    [Fact]
    public void AdditionsAndRemoval_PreservesSurvivingOrderAndMainFirstAnchors()
    {
        const string d = "src/D.cs";
        const string e = "src/E.cs";
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10) + Row(B, 10) + Row(C, 10)),
                Registry(Row(A, 11, "main A") + Row(d, 10, "main addition") + Row(B, 10) + Row(C, 10)),
                Registry(Row(A, 10) + Row(e, 10, "goal addition") + Row(C, 12, "goal C"))),
            Guard(A), Guard(B), Guard(C), Guard(d), Guard(e)
        ]);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        AssertMerged(fixture, result, events);
        Assert.Equal(Registry(Row(A, 11, "main A") + Row(d, 10, "main addition") +
            Row(e, 10, "goal addition") + Row(C, 12, "goal C")), fixture.Read(SourceSizeRatchet.SourcePath));
    }

    [Fact]
    public void AdditionToEmptyClassArray_StaysInsideItsOwningArray()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10) + Row(B, 10)),
                Registry(Row(A, 11) + Row(B, 10), ClassRow(10)), Registry(Row(A, 10) + Row(B, 12))),
            Guard(A), Guard(B)
        ]);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        AssertMerged(fixture, result, events);
        Assert.Equal(Registry(Row(A, 11) + Row(B, 12), ClassRow(10)), fixture.Read(SourceSizeRatchet.SourcePath));
    }

    [Fact]
    public void DuplicateIdentity_DeclinesRegistryMergeAndRestoresBranch()
    {
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry(Row(A, 10) + Row(A, 10)),
                Registry(Row(A, 11) + Row(A, 10)), Registry(Row(A, 12) + Row(A, 10))), Guard(A)
        ]);

        AssertRefused(fixture, "non-empty-base");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DeletedLayoutBoundary_WithOtherSideAddition_Declines(bool mainLayout, bool classArray)
    {
        var baseline = classArray ? Registry(Row(A, 10), ClassRow(10)) : Registry(Row(A, 10) + Row(B, 10));
        var layout = Registry(Row(A, 11)).Replace("// file header", "// changed header", StringComparison.Ordinal);
        var other = classArray
            ? Registry(Row(A, 12), ClassRow(10) + "        new SourceClassCeiling(\"Other\", 10, 1),\n")
            : Registry(Row(A, 12) + Row(B, 10) + Row(C, 10));
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, baseline, mainLayout ? layout : other, mainLayout ? other : layout),
            Guard(A), Guard(B), Guard(C)
        ]);

        AssertRefused(fixture, "non-empty-base");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void OneSidedNonEntryChange_MergesWithEntryChanges(bool mainLayout, bool trailer)
    {
        string ChangeLayout(string text) => trailer ? text + "// changed trailer\n"
            : text.Replace("// file header", "// changed header", StringComparison.Ordinal);
        var baseline = Registry(Row(A, 10) + Row(B, 10));
        var main = Registry(Row(A, 11, "main A") + Row(B, 10));
        var goal = Registry(Row(A, 10) + Row(B, 12, "goal B"));
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, baseline, mainLayout ? ChangeLayout(main) : main,
                mainLayout ? goal : ChangeLayout(goal)), Guard(A), Guard(B)
        ]);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        AssertMerged(fixture, result, events);
        Assert.Equal(ChangeLayout(Registry(Row(A, 11, "main A") + Row(B, 12, "goal B"))),
            fixture.Read(SourceSizeRatchet.SourcePath));
    }

    [Fact]
    public void NoSizeRows_RefusesWithUnavailableEvidenceReasonAndRestoresBranch()
    {
        const string other = "        new SourceClassCeiling(\"Other\", 10, 1),\n";
        using var fixture = new AdditiveConflictGitFixture([
            new(SourceSizeRatchet.SourcePath, Registry("", ClassRow(10) + other),
                Registry("", ClassRow(11) + other),
                Registry("", ClassRow(10) + other.Replace(", 10,", ", 12,", StringComparison.Ordinal)))
        ]);

        AssertRefused(fixture, "registry-verification-unavailable");
    }

    private static ConflictFile Guard(string path) => new(path, GuardedText, GuardedText, GuardedText);

    private static string Row(string path, int ceiling, params string[] comments) =>
        string.Concat(comments.Select(comment => "        // " + comment + "\n")) +
        $"        new SourceSizeCeiling(\"{path}\", {ceiling}),\n";

    private static string ClassRow(int ceiling) => $"        new SourceClassCeiling(\"Widget\", {ceiling}, 1),\n";

    private static string Registry(string rows, string classes = "") =>
        "// file header\ninternal static class SourceSizeRatchet\n{\n" +
        "    internal static SourceSizeCeiling[] SeededCeilings = new[]\n    {\n" + rows + "    };\n\n" +
        "    internal static SourceClassCeiling[] SeededClassCeilings = new[]\n    {\n" + classes + "    };\n}\n";

    private static void AssertMerged(AdditiveConflictGitFixture fixture, GoalWorktreeRebaseResult result, List<string> events)
    {
        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
        Assert.Equal("additive-conflict-auto-merge", result.Detail);
        var parents = Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
        Assert.Equal(3, parents.Length);
        Assert.Equal(fixture.OriginalHead, parents[1]);
        Assert.Equal(fixture.MainHead, parents[2]);
        Assert.Contains("result=merged", Assert.Single(events));
        Assert.Contains("reason=none", events[0]);
        Assert.Contains("merge=" + parents[0], events[0]);
        Assert.Equal("", Git(fixture.Worktree, "status", "--porcelain=v1", "--untracked-files=all"));
    }

    private static void AssertRefused(AdditiveConflictGitFixture fixture, string reason)
    {
        var events = new List<string>();
        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));
        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        fixture.AssertRestored();
        Assert.Contains("result=refused", Assert.Single(events));
        Assert.Contains("reason=" + reason + " merge=none", events[0]);
    }
}
