using Xunit;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FollowerGateLiveBaseTests
{
    private const string Main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Tree = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Other = "dddddddddddddddddddddddddddddddddddddddd";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainUnchanged_AnyLetterCase_LeaderPending(bool uppercase)
    {
        Assert.Equal(FollowerLiveBaseState.LeaderPending, FollowerGateBindingRule.ClassifyLiveBase(
            Main, Tree, uppercase ? Main.ToUpperInvariant() : Main, null, null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExactLanding_AnyLetterCase_LeaderLandedExactly(bool uppercaseParent, bool uppercaseTree)
    {
        Assert.Equal(FollowerLiveBaseState.LeaderLandedExactly, FollowerGateBindingRule.ClassifyLiveBase(
            Main, Tree, Other, uppercaseParent ? Main.ToUpperInvariant() : Main,
            uppercaseTree ? Tree.ToUpperInvariant() : Tree));
    }

    [Theory]
    [InlineData(Other, Other, Tree)]
    [InlineData(Other, Main, Other)]
    [InlineData(Candidate, Other, Tree)]
    [InlineData(Other, null, Tree)]
    [InlineData(Other, Main, null)]
    public void LandingMismatch_Moved(string live, string? parent, string? tree)
    {
        Assert.Equal(FollowerLiveBaseState.Moved,
            FollowerGateBindingRule.ClassifyLiveBase(Main, Tree, live, parent, tree));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("abcdef0")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void InvalidLiveMain_Unresolved(string? live)
    {
        Assert.Equal(FollowerLiveBaseState.Unresolved,
            FollowerGateBindingRule.ClassifyLiveBase(Main, Tree, live, Main, Tree));
    }
}
