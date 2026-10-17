using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorConflictIntegrationRecordTests;

// Parallel-safe: each test owns an isolated real merge and journal; builds are injected.
public sealed class ConductorConflictIntegrationValidateTests
{
    [Fact]
    public void Validate_CommittedResolution_CompletesRoundAfterBuild()
    {
        using var fixture = new Fixture();
        fixture.Admit();
        var head = fixture.Resolve();
        Assert.Equal(new[] { head, fixture.GitFixture.OriginalHead, fixture.GitFixture.MainHead },
            Git(fixture.GitFixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' '));
        Assert.Null(fixture.Coordinator.Validate(fixture.Goal));
        Assert.Equal(1, fixture.BuildCalls);
        Assert.Equal(GoalOperationStatus.Completed, fixture.Entry.Status);
        Assert.False(File.Exists(MarkerPath(fixture.GitFixture.Worktree)));
        Assert.Null(fixture.Coordinator.Validate(fixture.Goal));
        Assert.Equal(1, fixture.BuildCalls);
    }

    [Fact]
    public void Validate_NoRecord_DoesNotBuild()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Coordinator.Validate(fixture.Goal));
        Assert.Equal(0, fixture.BuildCalls);
        fixture.GitFixture.AssertRestored();
    }

    [Theory]
    [InlineData("outside", "src/Other.cs", 0)]
    [InlineData("marker", "conflict marker", 0)]
    [InlineData("build", "CS0103", 1)]
    [InlineData("uncommitted", "merge still in progress", 0)]
    [InlineData("wrong parents", "merge parent check", 0)]
    public void Validate_InvalidResolution_RestoresAndFailsRound(string kind, string detail, int builds)
    {
        using var fixture = new Fixture(failBuild: kind == "build");
        fixture.Admit();
        if (kind == "wrong parents")
        {
            Git(fixture.GitFixture.Worktree, "merge", "--abort");
            File.WriteAllText(Path.Combine(fixture.GitFixture.Worktree, WidgetPath), "main\ngoal\n");
            Commit(fixture.GitFixture.Worktree, "Ordinary commit is not an integration merge");
        }
        else if (kind != "uncommitted") fixture.Resolve(outside: kind == "outside", marker: kind == "marker");
        var reason = fixture.Coordinator.Validate(fixture.Goal);
        Assert.StartsWith("pre-landing rebase conflict (src/Widget.cs)", reason);
        Assert.DoesNotContain("workspace rebase", reason);
        Assert.Contains(detail, reason);
        fixture.GitFixture.AssertRestored();
        Assert.Equal(builds, fixture.BuildCalls);
        Assert.Equal(GoalOperationStatus.Failed, fixture.Entry.Status);
        Assert.False(File.Exists(MarkerPath(fixture.GitFixture.Worktree)));
    }

    internal static string MarkerPath(string worktree) => Path.GetFullPath(
        Git(worktree, "rev-parse", "--git-path", "mcg-conductor-integration.json").Trim(), worktree);
}
