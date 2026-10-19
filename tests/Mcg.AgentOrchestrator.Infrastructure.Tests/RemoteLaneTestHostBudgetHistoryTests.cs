using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure decisions with fixed inputs and no shared state.
public sealed class RemoteLaneTestHostBudgetHistoryTests
{
    [Theory]
    [InlineData(100, 3, 600)]
    [InlineData(500, 3, 1500)]
    [InlineData(333.4, 5, 1001)]
    [InlineData(1000, 3, 2100)]
    [InlineData(double.MaxValue, 3, 2100)]
    public void Decide_MeasuredHistory_RecordsClampedBudget(double median, int samples, int expected)
    {
        var decision = RemoteLaneTestHostBudget.Decide(TimeSpan.FromSeconds(2400), History(samples, median));

        Assert.Equal(new RemoteLaneTestHostBudgetDecision(expected, "remote-history", 2100,
            median, samples, 3, 600), decision);
    }

    [Theory]
    [InlineData(2, 500)]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    [InlineData(3, double.NaN)]
    [InlineData(3, double.PositiveInfinity)]
    [InlineData(3, double.NegativeInfinity)]
    public void Decide_UnusableHistory_KeepsHostBudget(int samples, double median)
    {
        var timeout = TimeSpan.FromSeconds(2400);
        var decision = RemoteLaneTestHostBudget.Decide(timeout, History(samples, median));

        Assert.Equal(RemoteLaneTestHostBudget.Resolve(timeout), decision.Seconds);
        Assert.Equal("host-budget", decision.Source);
        Assert.Equal(new RemoteLaneTestHostBudgetDecision(2100, "host-budget", 2100,
            double.IsFinite(median) ? median : null, samples, 3, 600), decision);
    }

    [Theory]
    [InlineData(2400)]
    [InlineData(1800)]
    [InlineData(780)]
    [InlineData(360)]
    [InlineData(120)]
    [InlineData(30)]
    [InlineData(0)]
    public void Decide_NoHistory_EqualsResolvedHostBudget(int seconds)
    {
        var timeout = TimeSpan.FromSeconds(seconds);
        var decision = RemoteLaneTestHostBudget.Decide(timeout, null);

        Assert.Equal(RemoteLaneTestHostBudget.Resolve(timeout), decision.Seconds);
        Assert.Equal(new RemoteLaneTestHostBudgetDecision(RemoteLaneTestHostBudget.Resolve(timeout),
            "host-budget", RemoteLaneTestHostBudget.Resolve(timeout), null, 0, 3, 600), decision);
    }

    [Fact]
    public void Decide_SmallHostBudget_CapsHistoryBelowFloor()
    {
        var decision = RemoteLaneTestHostBudget.Decide(TimeSpan.FromSeconds(120), History(5, 1000));

        Assert.Equal(new RemoteLaneTestHostBudgetDecision(60, "remote-history", 60, 1000, 5, 3, 600), decision);
    }

    [Fact]
    public void Decide_NoHostBudget_RetainsHistoryInputsWithoutTimeout()
    {
        var decision = RemoteLaneTestHostBudget.Decide(TimeSpan.Zero, History(5, 1000));

        Assert.Equal(new RemoteLaneTestHostBudgetDecision(null, "host-budget", null, 1000, 5, 3, 600), decision);
    }

    private static RemoteExecutorLaneSeconds History(int samples, double median) =>
        new("lane", samples, median, median, null, null);
}
