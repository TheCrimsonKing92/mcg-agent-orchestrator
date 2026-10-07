using Mcg.AgentOrchestrator.Core;

// Parallel-safe: the rule has no mutable state or I/O.
public sealed class PreTesterRedLoopRuleTests
{
    [Fact]
    public void Evaluate_ShrinkingMeasuredSets_ReturnsConvergingCounts()
    {
        var decision = PreTesterRedLoopRule.Evaluate(
        [
            ["A", "B", "C", "D", "E", "F", "G"],
            ["A", "B", "C", "D", "E", "H", "I"],
            ["D", "E"]
        ]);

        Assert.False(decision.Trip);
        Assert.Equal("converging", decision.Kind);
        Assert.Equal([7, 7, 2], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_ReorderedNewestSet_TripsRepeat()
    {
        var decision = PreTesterRedLoopRule.Evaluate([["A", "B"], ["A", "B"], ["B", "A"]]);

        Assert.True(decision.Trip);
        Assert.Equal("repeat", decision.Kind);
        Assert.Equal([2, 2, 2], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_LargerNewestSet_TripsGrew()
    {
        var decision = PreTesterRedLoopRule.Evaluate([["A"], ["B"], ["B", "C"]]);

        Assert.True(decision.Trip);
        Assert.Equal("grew", decision.Kind);
        Assert.Equal([1, 1, 2], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_ChangedMembershipAtSameCount_ReturnsConverging()
    {
        var decision = PreTesterRedLoopRule.Evaluate([["A"], ["B"], ["C"]]);

        Assert.False(decision.Trip);
        Assert.Equal("converging", decision.Kind);
        Assert.Equal([1, 1, 1], decision.FailingCounts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Evaluate_FewerThanThreeRuns_ReturnsBelowMinimum(int runs)
    {
        var sets = Enumerable.Repeat<IReadOnlyCollection<string>>(["A"], runs).ToArray();

        var decision = PreTesterRedLoopRule.Evaluate(sets);

        Assert.False(decision.Trip);
        Assert.Equal("below-minimum", decision.Kind);
        Assert.Equal(Enumerable.Repeat(1, runs), decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_FiveShrinkingRuns_TripsCapWithEveryCount()
    {
        var decision = PreTesterRedLoopRule.Evaluate(
        [
            ["A", "B", "C", "D", "E"], ["A", "B", "C", "D"],
            ["A", "B", "C"], ["A", "B"], ["A"]
        ]);

        Assert.Equal(5, PreTesterRedLoopRule.HardCap);
        Assert.True(decision.Trip);
        Assert.Equal("cap", decision.Kind);
        Assert.Equal([5, 4, 3, 2, 1], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_DuplicateMembers_CountsDistinctMembership()
    {
        var decision = PreTesterRedLoopRule.Evaluate([["A"], ["A", "A"], ["A"]]);

        Assert.True(decision.Trip);
        Assert.Equal("repeat", decision.Kind);
        Assert.Equal([1, 1, 1], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_CaseDifferentMembers_UsesOrdinalMembership()
    {
        var decision = PreTesterRedLoopRule.Evaluate([["A"], ["A"], ["a"]]);

        Assert.False(decision.Trip);
        Assert.Equal("converging", decision.Kind);
        Assert.Equal([1, 1, 1], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_FourShrinkingRuns_ReturnsConverging()
    {
        var decision = PreTesterRedLoopRule.Evaluate(
            [["A", "B", "C", "D"], ["A", "B", "C"], ["A", "B"], ["A"]]);

        Assert.False(decision.Trip);
        Assert.Equal("converging", decision.Kind);
        Assert.Equal([4, 3, 2, 1], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_AboveCapChangedMembership_TripsCapWithEveryCount()
    {
        var decision = PreTesterRedLoopRule.Evaluate([["A"], ["B"], ["C"], ["D"], ["E"], ["F"]]);

        Assert.True(decision.Trip);
        Assert.Equal("cap", decision.Kind);
        Assert.Equal([1, 1, 1, 1, 1, 1], decision.FailingCounts);
    }

    [Fact]
    public void Evaluate_NullList_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PreTesterRedLoopRule.Evaluate(null!));
    }
}
