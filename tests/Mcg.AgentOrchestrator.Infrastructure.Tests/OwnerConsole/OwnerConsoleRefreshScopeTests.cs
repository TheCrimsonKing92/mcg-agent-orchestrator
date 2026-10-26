using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleRefreshScopeTests
{
    private static readonly string[] Board =
        ["abcdef01111111111111111111111111", "abcdef02222222222222222222222222", "abcdef03333333333333333333333333"];

    [Fact(DisplayName = "All resolves every board goal")]
    public void All_ResolvesEveryBoardGoal() =>
        Assert.Equal(Board, OwnerConsoleRefreshScope.All.Resolve(Board));

    [Fact(DisplayName = "None resolves no board goals")]
    public void None_ResolvesNothing() => Assert.Empty(OwnerConsoleRefreshScope.None.Resolve(Board));

    [Fact(DisplayName = "An uppercase record prefix resolves the full board id")]
    public void For_MatchesPrefixIgnoringCase() =>
        Assert.Equal([Board[0]], OwnerConsoleRefreshScope.For(["ABCDEF01"]).Resolve(Board));

    [Fact(DisplayName = "A goal outside the board resolves no files")]
    public void For_UnknownGoalResolvesNothing() =>
        Assert.Empty(OwnerConsoleRefreshScope.For(["deadbeef"]).Resolve(Board));

    [Fact(DisplayName = "Two named goals resolve exactly their board ids")]
    public void For_ResolvesExactlyTwoNamedGoals() =>
        Assert.Equal([Board[0], Board[2]], OwnerConsoleRefreshScope.For([Board[0], Board[2][..8]]).Resolve(Board));

    [Fact(DisplayName = "Empty ids cannot widen a named scope to all goals")]
    public void For_IgnoresUnusableIds() =>
        Assert.Empty(OwnerConsoleRefreshScope.For([null!, "", " "]).Resolve(Board));

    [Fact(DisplayName = "Repeated prefixes and full ids read a board goal only once")]
    public void For_DeduplicatesResolvedBoardGoals() =>
        Assert.Equal([Board[0]], OwnerConsoleRefreshScope.For(["abcdef01", "ABCDEF01", Board[0]]).Resolve(Board));
}
