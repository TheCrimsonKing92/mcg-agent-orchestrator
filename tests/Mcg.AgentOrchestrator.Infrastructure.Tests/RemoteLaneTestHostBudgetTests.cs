using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneTestHostBudgetTests
{
    [Theory]
    [InlineData(2400, 2100)]
    [InlineData(1800, 1500)]
    [InlineData(780, 480)]
    [InlineData(360, 60)]
    [InlineData(120, 60)]
    [InlineData(30, 30)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(0.5, null)]
    public void Resolve_HostBudget_ReservesPreambleWithinTotal(double seconds, int? expected) =>
        Assert.Equal(expected, RemoteLaneTestHostBudget.Resolve(TimeSpan.FromSeconds(seconds)));
}
