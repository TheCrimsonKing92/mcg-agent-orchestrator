using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure values, no shared state or I/O.
public sealed class GateLaneSlowdownDiffTests
{
    [Fact]
    public void Compute_SlowLane_OrdersGrowthAndSeparatesNewTests()
    {
        var previous = new Dictionary<string, double>
        {
            ["Git.Commit"] = 4000, ["Git.Status"] = 3000, ["Git.Clone"] = 2000,
            ["Git.Fetch"] = 1000, ["Other.One"] = 2000, ["Other.Two"] = 3000
        };
        var current = previous.ToDictionary(pair => pair.Key,
            pair => pair.Value * (pair.Key.StartsWith("Git.", StringComparison.Ordinal) ? 3 : 1.2));
        current["GoalWorktreeTestsGitCommitLaunchCount.Run"] = 52000;
        var result = GateLaneSlowdownDiff.Compute(480000, new(current), History(previous));
        Assert.Equal("slow", result.Status);
        Assert.Equal(2, result.Ratio);
        Assert.Equal(240000, result.MedianWallMs);
        Assert.Equal(6, result.TestsCompared);
        Assert.Equal(4, result.TestsGrown);
        Assert.Equal(new[] { "Git.Commit", "Git.Status", "Git.Clone", "Git.Fetch" }, result.Grown!.Select(test => test.Test));
        Assert.All(result.Grown!, test => Assert.Equal(previous[test.Test], test.MedianMs));
        var fresh = Assert.Single(result.NewAboveFloor!);
        Assert.Equal("GoalWorktreeTestsGitCommitLaunchCount.Run", fresh.Test);
        Assert.Equal(52000, fresh.CurrentMs);
        Assert.Equal(1, result.TestsNewAboveFloor);
    }

    [Fact]
    public void Compute_ManyChanges_CapsListsAfterCountingAndBreaksTies()
    {
        var previous = Enumerable.Range(0, 14).ToDictionary(index => $"Git.Test{index:00}", _ => 2000d);
        var current = previous.ToDictionary(pair => pair.Key, _ => 6000d);
        foreach (var index in Enumerable.Range(0, 12)) current[$"New.Test{index:00}"] = 52000 + index;
        var result = GateLaneSlowdownDiff.Compute(480000, new(current), History(previous));
        Assert.Equal(14, result.TestsCompared);
        Assert.Equal(14, result.TestsGrown);
        Assert.Equal(10, result.Grown!.Count);
        Assert.Equal(previous.Keys.Order(StringComparer.Ordinal).Take(10), result.Grown.Select(test => test.Test));
        Assert.Equal(12, result.TestsNewAboveFloor);
        Assert.Equal(10, result.NewAboveFloor!.Count);
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(index => $"New.Test{index:00}"), result.NewAboveFloor.Select(test => test.Test));
    }

    [Theory]
    [InlineData(336000)]
    [InlineData(360000)]
    public void Compute_AtOrBelowThreshold_ReturnsNotSlowWithoutTests(double wall)
    {
        var result = GateLaneSlowdownDiff.Compute(wall, new(new Dictionary<string, double>()), History(new()));
        Assert.Equal("not-slow", result.Status);
        Assert.Empty(result.Grown ?? []);
        Assert.Empty(result.NewAboveFloor ?? []);
    }

    [Fact]
    public void Compute_TwoRuns_ReportsInsufficientHistory()
    {
        var result = GateLaneSlowdownDiff.Compute(480000, new(new Dictionary<string, double>()), History(new(), 2));
        Assert.Equal("insufficient-history", result.Status);
        Assert.Equal(2, result.HistoryRuns);
    }

    [Fact]
    public void Compute_IneligibleGrowth_ExcludesSingleSampleAndSmallIncrease()
    {
        var history = History(new() { ["Small.Growth"] = 400 });
        history[0] = history[0] with { Tests = new Dictionary<string, double> { ["Small.Growth"] = 400, ["Once.Seen"] = 1000 } };
        var result = GateLaneSlowdownDiff.Compute(480000,
            new(new Dictionary<string, double> { ["Small.Growth"] = 899, ["Once.Seen"] = 3000 }), history.Select(run =>
                new GateLaneBaselineStore.Run(DateTimeOffset.UnixEpoch, 240000, run.Tests)).ToArray());
        Assert.Equal("slow", result.Status);
        Assert.Equal(1, result.TestsCompared);
        Assert.Equal(0, result.TestsGrown);
        Assert.Empty(result.Grown!);
        Assert.Empty(result.NewAboveFloor!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("trx-missing:fixture.trx")]
    public void Compute_EmptySnapshot_ReportsNoTestData(string? reason)
    {
        var result = GateLaneSlowdownDiff.Compute(480000, new(new Dictionary<string, double>(), reason), History(new()));
        Assert.Equal("no-test-data", result.Status);
        Assert.Equal(2, result.Ratio);
        Assert.NotNull(result.UnavailableReason);
    }

    [Fact]
    public void Compute_EvenHistory_UsesMeanOfMiddleSamples()
    {
        var history = new[] { 200000d, 220000, 260000, 300000 }.Select((wall, index) =>
            new GateLaneBaselineStore.Run(DateTimeOffset.UnixEpoch, wall,
                new Dictionary<string, double> { ["Git.Commit"] = (index + 1) * 1000 })).ToArray();
        var result = GateLaneSlowdownDiff.Compute(480000,
            new(new Dictionary<string, double> { ["Git.Commit"] = 6000 }), history);
        Assert.Equal(240000, result.MedianWallMs);
        Assert.Equal(2500, Assert.Single(result.Grown!).MedianMs);
    }

    private static GateLaneBaselineStore.Run[] History(Dictionary<string, double> tests, int count = 5) =>
        Enumerable.Range(0, count).Select(index => new GateLaneBaselineStore.Run(DateTimeOffset.UnixEpoch.AddMinutes(index),
            240000, new Dictionary<string, double>(tests))).ToArray();
}
