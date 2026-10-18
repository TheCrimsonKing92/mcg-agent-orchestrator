using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorSuccessorExitRetryPolicyTests
{
    [Fact]
    public void FirstFailedExitBeforeReadinessRetriesConfiguredDelay()
    {
        var heads = new HashSet<string>();
        var retry = Assert.IsType<ConductorSuccessorExitRetryPolicy.Retry>(
            ConductorSuccessorExitRetryPolicy.Decide(true, 1, false, "head", heads, TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(60), retry.Delay);
        Assert.Empty(heads); // The supervisor owns recording the consumed retry.
    }

    [Theory]
    [InlineData(true, 0, false, false, true)]
    [InlineData(true, 1, true, false, true)]
    [InlineData(true, 1, false, true, true)]
    [InlineData(true, 1, false, false, false)]
    [InlineData(false, 1, false, false, true)]
    [InlineData(true, null, false, false, true)]
    public void OtherOutcomesDoNotRetry(bool beforeReadiness, int? exit, bool stop, bool alreadyRetried, bool enabled)
    {
        var heads = new HashSet<string>();
        if (alreadyRetried) heads.Add("HEAD");
        var decision = ConductorSuccessorExitRetryPolicy.Decide(beforeReadiness, exit, stop, "head", heads,
            enabled ? TimeSpan.FromSeconds(60) : null);
        Assert.NotEmpty(Assert.IsType<ConductorSuccessorExitRetryPolicy.NoRetry>(decision).Reason);
    }
}
