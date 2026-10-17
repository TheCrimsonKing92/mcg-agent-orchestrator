using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure resolution; no environment, file or process state.
public sealed class ConductorSelfCheckTimeoutResolutionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("601")]
    [InlineData("-5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Missing_or_invalid_seconds_use_thirty_seconds(string? value) =>
        Assert.Equal(TimeSpan.FromSeconds(30), ConductorSelfCheckProbe.ResolveTimeout(value));

    [Theory]
    [InlineData("10", 10)]
    [InlineData("45", 45)]
    [InlineData("600", 600)]
    [InlineData("12.5", 12.5)]
    public void Valid_seconds_are_honoured(string value, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ConductorSelfCheckProbe.ResolveTimeout(value));

    [Fact]
    public void Default_remains_thirty_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(30), ConductorSelfCheckProbe.DefaultTimeout);
}
