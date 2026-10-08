using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: each fact owns an isolated real git repository and worktree.
public sealed class GoalWorktreeTestsRebaseAdditiveConflictMerge
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditiveProbe_UsesLandingClassifierWithoutChangingRefsOrLeavingWorktrees(bool overlappingEdit)
    {
        using var fixture = overlappingEdit
            ? new AdditiveConflictGitFixture([new(WidgetPath, "class Old {}\n", "class Main {}\n", "class Goal {}\n")])
            : new AdditiveConflictGitFixture();
        // The planner probe targets main; the shared fixture otherwise inherits init.defaultBranch.
        Git(fixture.Repository, "branch", "-M", "main");
        Assert.Equal(fixture.MainHead, Git(fixture.Repository, "rev-parse", "refs/heads/main").Trim());
        var before = Git(fixture.Repository, "worktree", "list", "--porcelain");
        var mainStatus = Git(fixture.Repository, "status", "--porcelain=v1", "--untracked-files=all");
        var mergeTree = new Mcg.AgentOrchestrator.Infrastructure.WorkerGitContext()
            .ReadReviewerMergeTreeStatus(fixture.Worktree, fixture.MainHead, fixture.OriginalHead);
        Assert.False(mergeTree.IsClean);
        Assert.Equal(new[] { WidgetPath }, mergeTree.ConflictPaths);

        var probe = GoalWorktrees.ProbeAdditiveConflictMerge(fixture.Repository, fixture.GoalId, []);

        Assert.Equal(overlappingEdit ? AdditiveConflictProbeOutcome.Unresolvable : AdditiveConflictProbeOutcome.Resolvable, probe.Outcome);
        Assert.Equal(fixture.OriginalHead, probe.BranchRevision);
        Assert.Equal(fixture.MainHead, probe.MainRevision);
        fixture.AssertRestored();
        Assert.Equal(fixture.MainHead, Git(fixture.Repository, "rev-parse", "HEAD").Trim());
        Assert.Equal(mainStatus, Git(fixture.Repository, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(before, Git(fixture.Repository, "worktree", "list", "--porcelain"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AdditiveProbe_MasterOnlyTrunk_ResolvesIntegrationBranch(bool overlappingEdit, bool detachedHead)
    {
        using var fixture = overlappingEdit
            ? new AdditiveConflictGitFixture([new(WidgetPath, "class Old {}\n", "class Main {}\n", "class Goal {}\n")])
            : new AdditiveConflictGitFixture();
        Git(fixture.Repository, "branch", "-M", "master");
        Assert.Empty(Git(fixture.Repository, "for-each-ref", "refs/heads/main"));
        var masterHead = Git(fixture.Repository, "rev-parse", "refs/heads/master").Trim();
        Assert.Equal(fixture.MainHead, masterHead);
        if (detachedHead) Git(fixture.Repository, "checkout", "--detach", "master");
        var beforeRefs = Git(fixture.Repository, "for-each-ref");
        var beforeWorktrees = Git(fixture.Repository, "worktree", "list", "--porcelain");
        var beforeBranch = Git(fixture.Repository, "branch", "--show-current");
        var beforeStatus = Git(fixture.Repository, "status", "--porcelain=v1", "--untracked-files=all");
        var mergeTree = new WorkerGitContext()
            .ReadReviewerMergeTreeStatus(fixture.Worktree, masterHead, fixture.OriginalHead);
        Assert.False(mergeTree.IsClean);
        Assert.Equal(new[] { WidgetPath }, mergeTree.ConflictPaths);

        var probe = GoalWorktrees.ProbeAdditiveConflictMerge(fixture.Repository, fixture.GoalId, [], "master");

        Assert.Equal(overlappingEdit ? AdditiveConflictProbeOutcome.Unresolvable : AdditiveConflictProbeOutcome.Resolvable, probe.Outcome);
        Assert.Equal(fixture.OriginalHead, probe.BranchRevision);
        Assert.Equal(masterHead, probe.MainRevision);
        fixture.AssertRestored();
        Assert.Equal(masterHead, Git(fixture.Repository, "rev-parse", "HEAD").Trim());
        Assert.Equal(beforeBranch, Git(fixture.Repository, "branch", "--show-current"));
        Assert.Equal(beforeStatus, Git(fixture.Repository, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal(beforeRefs, Git(fixture.Repository, "for-each-ref"));
        Assert.Equal(beforeWorktrees, Git(fixture.Repository, "worktree", "list", "--porcelain"));
    }

    [Fact]
    public void UntrackedWorkerResult_MergesAdditiveConflictAndPreservesArtifact()
    {
        using var fixture = new AdditiveConflictGitFixture();
        var artifactPath = Path.Combine(fixture.Worktree, "WORKER_RESULT.md");
        File.WriteAllText(artifactPath, "worker evidence");
        Assert.Equal("?? WORKER_RESULT.md", Git(fixture.Worktree, "status", "--porcelain=v1",
            "--untracked-files=all").Trim());
        Assert.False(GitCli.IsWorktreeDirty(fixture.Worktree));
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
        Assert.Equal("additive-conflict-auto-merge", result.Detail);
        Assert.Equal(MergedText, fixture.Read());
        var parents = Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
        Assert.Equal(3, parents.Length);
        Assert.Equal(fixture.OriginalHead, parents[1]);
        Assert.Equal(fixture.MainHead, parents[2]);
        Assert.Equal("worker evidence", File.ReadAllText(artifactPath));
        Assert.Equal("?? WORKER_RESULT.md", Git(fixture.Worktree, "status", "--porcelain=v1",
            "--untracked-files=all").Trim());
        Assert.False(GitCli.IsWorktreeDirty(fixture.Worktree));
        Assert.Equal($"REBASE_CONFLICT_AUTOMERGE goal={fixture.GoalId.Value[..8]} result=merged " +
            $"files={WidgetPath} hunks=1 reason=none merge={parents[0]}", Assert.Single(events));
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public void AdditiveSourceConflict_KeepsMainThenGoal(bool bom, string lineEnding)
    {
        using var fixture = new AdditiveConflictGitFixture(bom: bom, lineEnding: lineEnding);
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
        Assert.Equal("additive-conflict-auto-merge", result.Detail);
        Assert.Empty(result.ConflictFiles);
        Assert.Equal(MergedText, fixture.Read());
        var parents = Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
        Assert.Equal(3, parents.Length);
        Assert.Equal(fixture.OriginalHead, parents[1]);
        Assert.Equal(fixture.MainHead, parents[2]);
        Assert.Equal("", Git(fixture.Worktree, "status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal($"Integrate main into {GoalWorktrees.BranchName(fixture.GoalId)} (additive conflict auto-merge)",
            Git(fixture.Worktree, "log", "-1", "--format=%s").Trim());
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Worktree, WidgetPath));
        Assert.Equal(bom, bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal(MergedText.ReplaceLineEndings(lineEnding),
            System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff'));
        Assert.Equal($"REBASE_CONFLICT_AUTOMERGE goal={fixture.GoalId.Value[..8]} result=merged " +
            $"files={WidgetPath} hunks=1 reason=none merge={parents[0]}", Assert.Single(events));
        Assert.Equal(fixture.MainHead, Git(fixture.Repository, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public void SameFixture_WithoutConductorOptions_PreservesBaselineConflict()
    {
        using var fixture = new AdditiveConflictGitFixture();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId);

        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, result.Status);
        Assert.Equal(new[] { WidgetPath }, result.ConflictFiles);
        fixture.AssertRestored();
        Assert.Equal(GoalText, fixture.Read());
    }

    [Fact]
    public void MultipleAdditiveHunks_KeepsBothSidesAndCountsEveryHunk()
    {
        var middle = string.Join('\n', Enumerable.Range(0, 15).Select(index => $"// context {index}")) + "\n";
        var baseline = "// start\n" + middle + "// end\n";
        var main = "// start\n// main first\n" + middle + "// main last\n// end\n";
        var goal = "// start\n// goal first\n" + middle + "// goal last\n// end\n";
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, baseline, main, goal)],
            attributes: "*.cs conflict-marker-size=10\n");
        var events = new List<string>();

        var result = GoalWorktrees.TryRebaseOntoMain(fixture.Repository, fixture.GoalId, new([], events.Add));

        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result.Status);
        Assert.Equal("// start\n// main first\n// goal first\n" + middle + "// main last\n// goal last\n// end\n", fixture.Read());
        Assert.Contains("hunks=2 reason=none", Assert.Single(events));
        Assert.Equal("", Git(fixture.Worktree, "status", "--porcelain=v1"));
    }
}
