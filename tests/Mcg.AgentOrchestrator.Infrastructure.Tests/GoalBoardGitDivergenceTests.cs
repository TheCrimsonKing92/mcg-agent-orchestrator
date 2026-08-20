using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalBoardGitDivergenceTests
{
    [Xunit.Fact]
    public void ParseMapsAheadThenBehindFromThreeTokens()
    {
        var snapshot = GoalBoardGitDivergence.Parse("goal/aaaaaaaa 3 7\n");

        var inspection = snapshot["goal/aaaaaaaa"];
        Xunit.Assert.True(inspection.Succeeded);
        Xunit.Assert.Equal(3, inspection.Ahead);
        Xunit.Assert.Equal(7, inspection.Behind);
    }

    [Xunit.Fact]
    public void ParseIgnoresMalformedLinesWithoutDroppingValid()
    {
        var snapshot = GoalBoardGitDivergence.Parse(
            """
            goal/only-one
            goal/valid 4 9
            goal/four-tokens 1 2 extra
            goal/not-ints a b
            """);

        Xunit.Assert.Equal(4, snapshot["goal/valid"].Ahead);
        Xunit.Assert.Equal(9, snapshot["goal/valid"].Behind);
        Xunit.Assert.False(snapshot.ContainsKey("goal/only-one"));
        Xunit.Assert.False(snapshot.ContainsKey("goal/four-tokens"));
        Xunit.Assert.False(snapshot.ContainsKey("goal/not-ints"));
    }

    [Xunit.Fact]
    public void CaptureNonZeroExitReturnsEmptySnapshot()
    {
        var calls = 0;
        var snapshot = GoalBoardGitDivergence.Capture(
            ".",
            (_, args) =>
            {
                calls++;
                Xunit.Assert.Equal("for-each-ref", args[0]);
                Xunit.Assert.Equal("--format=%(refname:short) %(ahead-behind:main)", args[1]);
                Xunit.Assert.Equal("refs/heads/goal/", args[2]);
                return new GitCli.GitResult(128, "goal/aaaaaaaa 3 7", "unknown field ahead-behind");
            });

        Xunit.Assert.Equal(1, calls);
        Xunit.Assert.Empty(snapshot);
    }
}
