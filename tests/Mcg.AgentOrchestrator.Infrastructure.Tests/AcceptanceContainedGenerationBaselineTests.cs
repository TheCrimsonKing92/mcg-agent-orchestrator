using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceContainedGenerationBaselineTests
{
    [Fact]
    public void Resolve_MergeBaseUnavailable_IsConservativelyUnresolved()
    {
        var gitInvoked = false;

        using var baseline = AcceptanceContainedGenerationBaseline.Resolve(
            "candidate",
            "goal",
            (_, arguments) => arguments[0] == "rev-parse" ? "bbbbbbbb" : null,
            (_, _) =>
            {
                gitInvoked = true;
                return new GitCli.GitResult(0, string.Empty, string.Empty);
            });

        Assert.False(baseline.IsResolved);
        Assert.Equal("merge-base-unresolved", baseline.UnresolvedReason);
        Assert.False(gitInvoked);
    }

    [Fact]
    public void Resolve_CandidateContainsObservedMain_ReusesObservedBaseline()
    {
        using var baseline = AcceptanceContainedGenerationBaseline.Resolve(
            "candidate",
            "goal",
            (_, _) => "aaaaaaaa",
            (_, _) => throw new InvalidOperationException("worktree creation must not run"));

        Assert.True(baseline.IsResolved);
        Assert.True(baseline.UseObservedBaseline);
        Assert.Null(baseline.WorktreePath);
    }

    [Fact]
    public void Resolve_StaleCandidate_OwnsAndRemovesDetachedWorktree()
    {
        var commands = new List<string[]>();
        var resolveCall = 0;
        var baseline = AcceptanceContainedGenerationBaseline.Resolve(
            "candidate",
            "goal",
            (_, _) => ++resolveCall == 1 ? "aaaaaaaa" : "bbbbbbbb",
            (_, arguments) =>
            {
                commands.Add(arguments);
                return new GitCli.GitResult(0, string.Empty, string.Empty);
            });

        Assert.True(baseline.IsResolved);
        Assert.False(baseline.UseObservedBaseline);
        Assert.NotNull(baseline.WorktreePath);

        baseline.Dispose();

        Assert.Collection(
            commands,
            add => Assert.Equal(["worktree", "add", "--detach", baseline.WorktreePath!, "aaaaaaaa"], add),
            remove => Assert.Equal(["worktree", "remove", "--force", baseline.WorktreePath!], remove));
    }

    [Fact]
    public void Resolve_StaleCandidate_TrimsRealGitShaOutput()
    {
        string? addedRevision = null;
        var resolveCall = 0;
        using var baseline = AcceptanceContainedGenerationBaseline.Resolve(
            "candidate",
            "goal",
            (_, _) => ++resolveCall == 1 ? "aaaaaaaa\r\n" : "bbbbbbbb\r\n",
            (_, arguments) =>
            {
                if (arguments.Count == 5 && arguments[0] == "worktree" && arguments[1] == "add")
                {
                    addedRevision = arguments[4];
                }

                return new GitCli.GitResult(0, string.Empty, string.Empty);
            });

        Assert.True(baseline.IsResolved);
        Assert.Equal("aaaaaaaa", baseline.ContainedMainSha);
        Assert.Equal("bbbbbbbb", baseline.ObservedMainSha);
        Assert.Equal("aaaaaaaa", addedRevision);
    }

    [Fact]
    public void Resolve_WorktreeCreationFails_IsConservativelyUnresolved()
    {
        var resolveCall = 0;
        using var baseline = AcceptanceContainedGenerationBaseline.Resolve(
            "candidate",
            "goal",
            (_, _) => ++resolveCall == 1 ? "aaaaaaaa" : "bbbbbbbb",
            (_, _) => new GitCli.GitResult(1, string.Empty, "failed"));

        Assert.False(baseline.IsResolved);
        Assert.Equal("worktree-add-failed", baseline.UnresolvedReason);
    }
}
