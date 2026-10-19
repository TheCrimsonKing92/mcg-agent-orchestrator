using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixed inputs to a stateless rule.
public sealed class PreTesterRedLoopRuleRunsTests
{
    [Theory]
    [InlineData("same", "same", true, "repeat")]
    [InlineData("old", "new", false, "converging")]
    [InlineData("same", "Same", false, "converging")]
    [InlineData(null, "new", true, "repeat")]
    [InlineData("old", null, true, "repeat")]
    [InlineData(null, null, true, "repeat")]
    public void EqualNames_CompareOnlyKnownSignatures(
        string? previous, string? newest, bool trip, string kind)
    {
        var result = PreTesterRedLoopRule.EvaluateRuns([Run("A", "seed"), Run("A", previous), Run("A", newest)]);

        Assert.Equal(trip, result.Trip);
        Assert.Equal(kind, result.Kind);
        Assert.Equal([1, 1, 1], result.FailingCounts);
    }

    [Fact]
    public void OneChangedSignatureAmongEqualNamesConverges()
    {
        var result = PreTesterRedLoopRule.EvaluateRuns([
            Run("seed", null),
            new(["A", "B"], new Dictionary<string, string> { ["A"] = "same", ["B"] = "old" }),
            new(["B", "A"], new Dictionary<string, string> { ["A"] = "same", ["B"] = "new" })]);

        Assert.False(result.Trip);
        Assert.Equal("converging", result.Kind);
        Assert.Equal([1, 2, 2], result.FailingCounts);
    }

    [Fact]
    public void SignaturesOutsideFailingNamesDoNotAffectRepeat()
    {
        var result = PreTesterRedLoopRule.EvaluateRuns([
            Run("A", "seed"),
            new(["A"], new Dictionary<string, string> { ["other"] = "old" }),
            new(["A"], new Dictionary<string, string> { ["other"] = "new" })]);

        Assert.True(result.Trip);
        Assert.Equal("repeat", result.Kind);
    }

    [Fact]
    public void LargerNewestSetTripsGrewDespiteDifferentSignatures()
    {
        var result = PreTesterRedLoopRule.EvaluateRuns([
            Run("A", "seed"), Run("A", "old"),
            new(["A", "B"], new Dictionary<string, string> { ["A"] = "new", ["B"] = "new" })]);

        Assert.True(result.Trip);
        Assert.Equal("grew", result.Kind);
        Assert.Equal([1, 1, 2], result.FailingCounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiveRunsTripCapBeforeSignatureComparison(bool different)
    {
        var result = PreTesterRedLoopRule.EvaluateRuns(Enumerable.Range(0, 5)
            .Select(i => Run("A", different ? i.ToString() : "same")).ToArray());

        Assert.Equal(5, PreTesterRedLoopRule.HardCap);
        Assert.True(result.Trip);
        Assert.Equal("cap", result.Kind);
        Assert.Equal([1, 1, 1, 1, 1], result.FailingCounts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FewerThanThreeRunsRemainBelowMinimum(int count)
    {
        var result = PreTesterRedLoopRule.EvaluateRuns(Enumerable.Repeat(Run("A", "same"), count).ToArray());

        Assert.False(result.Trip);
        Assert.Equal("below-minimum", result.Kind);
        Assert.Equal(Enumerable.Repeat(1, count), result.FailingCounts);
    }

    public static TheoryData<IReadOnlyList<IReadOnlyCollection<string>>> LegacyInputs => new()
    {
        { [["A", "B", "C", "D", "E", "F", "G"], ["A", "B", "C", "D", "E", "H", "I"], ["D", "E"]] },
        { [["A", "B"], ["A", "B"], ["B", "A"]] },
        { [["A"], ["B"], ["B", "C"]] },
        { [["A"], ["B"], ["C"]] },
        { [] },
        { [["A"]] },
        { [["A"], ["A"]] },
        { [["A", "B", "C", "D", "E"], ["A", "B", "C", "D"], ["A", "B", "C"], ["A", "B"], ["A"]] },
        { [["A"], ["A", "A"], ["A"]] },
        { [["A"], ["A"], ["a"]] },
        { [["A", "B", "C", "D"], ["A", "B", "C"], ["A", "B"], ["A"]] },
        { [["A"], ["B"], ["C"], ["D"], ["E"], ["F"]] }
    };

    [Theory]
    [MemberData(nameof(LegacyInputs))]
    public void EmptySignaturesPreserveEveryLegacyInput(IReadOnlyList<IReadOnlyCollection<string>> sets)
    {
        var expected = PreTesterRedLoopRule.Evaluate(sets);
        var actual = PreTesterRedLoopRule.EvaluateRuns(sets.Select(set =>
            new PreTesterRedRun(set, new Dictionary<string, string>(StringComparer.Ordinal))).ToArray());

        Assert.Equal(expected.Trip, actual.Trip);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.FailingCounts, actual.FailingCounts);
    }

    [Fact]
    public void NullRunsThrowArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => PreTesterRedLoopRule.EvaluateRuns(null!));

    private static PreTesterRedRun Run(string name, string? signature) => new([name], signature is null
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : new Dictionary<string, string>(StringComparer.Ordinal) { [name] = signature });
}
