using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorConflictIntegrationRecordTests;

// Parallel-safe: each case owns its repository, marker and kernel; git dates are command-scoped.
public sealed class IntegrationMergeChangedPathsTests
{
    [Fact]
    public void Read_RecordedMerge_ProvidesPathsAndRelevantCommitEvidence()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        ResolveAndCommit(git, dispatchedAt);
        AssertOnlyMergeAfter(git.Worktree, dispatchedAt);

        var expected = Git(git.Worktree, "diff", "--name-only", git.OriginalHead, "HEAD")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(WidgetPath, expected);
        Assert.Equal(expected, Read(git.Worktree));
        var inspection = new DispatchWorktreeCommitter().InspectGoalWorktree(git.Worktree, fixture.Goal.Id, dispatchedAt);
        Assert.True(inspection.IsAvailable, inspection.GitReceipt);
        Assert.Equal(1, inspection.Evidence.CommitsAfterDispatch);
        Assert.Equal(expected, inspection.Evidence.ChangedPaths);
        Assert.True(inspection.Evidence.HasRelevantCommitAfterDispatch);
    }

    [Fact]
    public void Read_NoMarker_ReturnsNullWithoutCallingInjectedGit()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var result = IntegrationMergeChangedPaths.Read(fixture.GitFixture.Worktree, (_, _) =>
        {
            calls++;
            return new(0, "", "");
        });
        Assert.Null(result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, 1, "")]
    [InlineData(true, 0, "")]
    [InlineData(true, 1, WidgetPath)]
    public void Inspect_OrdinaryEvidence_KeepsTheFiveGitCalls(bool marker, int commits, string paths)
    {
        using var fixture = new Fixture();
        if (marker) fixture.Admit();
        var git = fixture.GitFixture;
        var calls = new List<string>();
        var committer = new DispatchWorktreeCommitter(runGit: (_, args) =>
        {
            calls.Add(string.Join(' ', args));
            return new(0, args[0] switch
            {
                "branch" => GoalWorktreeLayout.BranchName(fixture.Goal.Id),
                "rev-parse" => git.OriginalHead,
                "status" => "",
                "log" when args[1] == "--format=%H" => commits == 0 ? "" : git.OriginalHead,
                "log" => paths,
                _ => throw new InvalidOperationException("Unexpected fallback git call")
            }, "");
        });
        var inspection = committer.InspectGoalWorktree(git.Worktree, fixture.Goal.Id, DateTimeOffset.UtcNow);
        Assert.True(inspection.IsAvailable, inspection.GitReceipt);
        Assert.Equal(commits, inspection.Evidence.CommitsAfterDispatch);
        Assert.Equal(paths.Length > 0, inspection.Evidence.HasRelevantCommitAfterDispatch);
        Assert.Equal(5, calls.Count);
        Assert.StartsWith("log --name-only --format= --since=", calls[4]);
    }

    [Fact]
    public void Read_WrongSecondParent_ReturnsNull()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        ResolveAndCommit(git, DateTimeOffset.UtcNow.AddMinutes(1));
        ConductorConflictIntegrationMarker.Write(fixture.Developer, git.Worktree, git.OriginalHead,
            Git(git.Worktree, "rev-parse", git.MainHead + "^").Trim());
        Assert.Null(Read(git.Worktree));
    }

    [Fact]
    public void Read_ThirdParent_ReturnsNull()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        Git(git.Worktree, "merge", "--abort");
        var third = Git(git.Worktree, "rev-parse", git.MainHead + "^").Trim();
        var tree = Git(git.Worktree, "rev-parse", "HEAD^{tree}").Trim();
        var octopus = Git(git.Worktree, "commit-tree", tree, "-p", git.OriginalHead,
            "-p", git.MainHead, "-p", third, "-m", "Three parents").Trim();
        Git(git.Worktree, "reset", "--hard", octopus);
        Assert.Equal(4, Git(git.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);
        Assert.Null(Read(git.Worktree));
    }

    [Fact]
    public void Read_NonMergeCommit_LeavesOrdinaryPathsIntact()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        Git(git.Worktree, "merge", "--abort");
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        File.WriteAllText(Path.Combine(git.Worktree, WidgetPath), "ordinary change\n");
        Git(git.Worktree, "add", "--", WidgetPath);
        DatedGit(git.Worktree, dispatchedAt.AddMinutes(1), "commit", "-m", "Ordinary change");
        Assert.Null(Read(git.Worktree));
        var ordinary = Git(git.Worktree, "log", "--name-only", "--format=", $"--since={dispatchedAt:O}")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { WidgetPath }, ordinary);
        var calls = new List<string>();
        var inspection = new DispatchWorktreeCommitter(runGit: (path, args) =>
        {
            calls.Add(string.Join(' ', args));
            return GitCli.Run(path, args);
        }).InspectGoalWorktree(git.Worktree, fixture.Goal.Id, dispatchedAt);
        Assert.True(inspection.IsAvailable, inspection.GitReceipt);
        Assert.True(inspection.Evidence.HasRelevantCommitAfterDispatch);
        Assert.Equal(ordinary, inspection.Evidence.ChangedPaths);
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public void Read_DescendantOfRecordedMerge_DiffsFromOriginalHead()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        ResolveAndCommit(git, dispatchedAt);
        File.WriteAllText(Path.Combine(git.Worktree, "src/After.cs"), "after\n");
        Commit(git.Worktree, "Follow-up");
        Assert.Equal(new[] { "src/After.cs", WidgetPath }, Read(git.Worktree));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("empty-head")]
    [InlineData("git-failure")]
    [InlineData("drain-timeout")]
    [InlineData("exception")]
    [InlineData("diff-failure")]
    public void Read_UnreadableEvidence_ReturnsNull(string failure)
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var git = fixture.GitFixture;
        ResolveAndCommit(git, DateTimeOffset.UtcNow.AddMinutes(1));
        var marker = ConductorConflictIntegrationValidateTests.MarkerPath(git.Worktree);
        if (failure == "malformed") File.WriteAllText(marker, "{");
        if (failure == "empty-head")
            ConductorConflictIntegrationMarker.Write(fixture.Developer, git.Worktree, "", git.MainHead);
        var calls = 0;
        Assert.Null(IntegrationMergeChangedPaths.Read(git.Worktree, (path, args) =>
        {
            calls++;
            return failure switch
            {
                "git-failure" => new(1, "", "failed"),
                "drain-timeout" => new(0, "", "") { DrainTimedOut = true },
                "exception" => throw new IOException("read failed"),
                "diff-failure" when args[0] == "diff" => new(1, "", "diff failed"),
                _ => GitCli.Run(path, args)
            };
        }));
        Assert.Equal(failure is "malformed" or "empty-head" ? 0 : failure == "diff-failure" ? 2 : 1, calls);
    }

    internal static IReadOnlyList<string>? Read(string worktree) =>
        IntegrationMergeChangedPaths.Read(worktree, (path, args) => GitCli.Run(path, args));

    internal static string ResolveAndCommit(AdditiveConflictGitFixture git, DateTimeOffset dispatchedAt,
        bool marker = false, bool outside = false)
    {
        File.WriteAllText(Path.Combine(git.Worktree, WidgetPath), marker ? "main\n||||||| base\ngoal\n" : "main\ngoal\n");
        Git(git.Worktree, "add", "--", WidgetPath);
        if (outside)
        {
            File.WriteAllText(Path.Combine(git.Worktree, "src/Other.cs"), "outside\n");
            Git(git.Worktree, "add", "--", "src/Other.cs");
        }
        DatedGit(git.Worktree, dispatchedAt.AddMinutes(1), "commit", "-m", "Resolve integration");
        return Git(git.Worktree, "rev-parse", "HEAD").Trim();
    }

    internal static void AssertOnlyMergeAfter(string worktree, DateTimeOffset dispatchedAt)
    {
        Assert.Equal("", Git(worktree, "log", "--name-only", "--format=", $"--since={dispatchedAt:O}"));
        Assert.Equal(new[] { Git(worktree, "rev-parse", "HEAD").Trim() },
            Git(worktree, "log", "--format=%H", $"--since={dispatchedAt:O}")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    internal static void DatedGit(string worktree, DateTimeOffset at, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(worktree, args, new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = at.ToString("O"),
            ["GIT_COMMITTER_DATE"] = at.ToString("O")
        });
        Assert.False(result.TimedOut, $"git {string.Join(' ', args)} did not exit: {result}");
        Assert.True(result.ExitCode == 0, result.StandardError);
    }
}
