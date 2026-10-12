using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;

// Parallel-safe: each fact owns isolated repositories/worktrees and an AsyncLocal launch counter.
public sealed class AdditiveConflictGitFixtureLaunchTests
{
    [Xunit.Fact]
    public void Constructor_WarmDefault_LaunchesSixGitProbes()
    {
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture();
        Assert.Equal(6, launches.Count);
    }

    [Xunit.Fact]
    public void Constructor_TwoFilesAndAttributes_LaunchesSixAndPreservesCommits()
    {
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture(
            files: [
                new(WidgetPath, BaseText, MainText, GoalText),
                new("src/Other Widget.cs", "base\n", "main\n", "goal\n")
            ],
            attributes: "*.cs text\n");
        Assert.Equal(6, launches.Count);
        // Only construction is measured; the independent history probes are outside the counter.
        launches.Dispose();

        Assert.Equal("3", Git(fixture.Worktree, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal("3", Git(fixture.Repository, "rev-list", "--count", "HEAD").Trim());
    }

    [Xunit.Fact]
    public void Constructor_Default_MatchesGitRepositoryState()
    {
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture();
        Assert.Equal(6, launches.Count);
        launches.Dispose();

        Assert.Equal(fixture.OriginalHead, Git(fixture.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(fixture.MainHead, Git(fixture.Repository, "rev-parse", "HEAD").Trim());
        foreach (var root in new[] { fixture.Repository, fixture.Worktree })
        {
            Assert.Equal("false", Git(root, "config", "--local", "--get", "core.autocrlf").Trim());
            Assert.Equal("false", Git(root, "config", "--local", "--get", "commit.gpgSign").Trim());
            Assert.Empty(Git(root, "status", "--porcelain=v1"));
        }
        Assert.Equal(GoalWorktrees.BranchName(fixture.GoalId),
            Git(fixture.Worktree, "symbolic-ref", "--short", "HEAD").Trim());
    }

    [Xunit.Fact]
    public void Constructor_IgnoredPath_ForceStagesFixtureInput()
    {
        const string ignoredPath = ".orchestrator/Widget.cs";
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture(
            files: [new(ignoredPath, BaseText, MainText, GoalText)]);
        Assert.Equal(6, launches.Count);
        launches.Dispose();

        Assert.Equal(ignoredPath, Git(fixture.Repository, "check-ignore", "--no-index", "--", ignoredPath).Trim());
        Assert.Contains(ignoredPath,
            Git(fixture.Worktree, "ls-tree", "-r", "--name-only", "HEAD")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }
}
