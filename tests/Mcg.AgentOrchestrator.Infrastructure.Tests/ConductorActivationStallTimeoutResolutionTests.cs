using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure resolution of a configured value; no environment, clock, file or process.
// 2026-10-16 20:14Z and 20:18Z: a full board's first tick ran past the old fixed 2-minute activation stall
// timeout, so both the new build and the restored build were reverted (ACTIVATION_FAILED_BOTH, TickStall).
public sealed class ConductorActivationStallTimeoutResolutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("100000")]
    public void MissingOrInvalidSetting_UsesTheTenMinuteDefault(string? configured) =>
        Assert.Equal(TimeSpan.FromMinutes(10), ConductorContinuitySupervisor.ResolveActivationStallTimeout(configured));

    [Theory]
    [InlineData("2", 2)]
    [InlineData("4.5", 4.5)]
    [InlineData("30", 30)]
    public void PositiveMinutes_AreHonoured(string configured, double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), ConductorContinuitySupervisor.ResolveActivationStallTimeout(configured));

    [Fact]
    public void DefaultExceedsTheSlowestFirstTickMeasuredOnTheIncidentDay() =>
        Assert.True(ConductorContinuitySupervisor.DefaultActivationStallTimeout > TimeSpan.FromMinutes(4));
}
