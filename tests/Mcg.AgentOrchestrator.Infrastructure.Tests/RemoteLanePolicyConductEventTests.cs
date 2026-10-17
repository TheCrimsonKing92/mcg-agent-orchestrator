// Parallel safe: scenario-owned roots, overrides, event sinks and leased build slots.
public sealed class RemoteLanePolicyConductEventTests
{
    [Fact]
    public async Task EveryPolicyDecisionReachesConductSinkExactlyOnce()
    {
        using var scenario = new RemoteSlotInstrumentationScenarioTests.Scenario(2);
        Assert.True((await scenario.RunAsync()).Passed);
        var progress = scenario.Progress.Where(line => line.StartsWith("REMOTE_LANE_POLICY ")).ToArray();
        Assert.Equal(2, progress.Length);
        Assert.Equal(progress.Order(), scenario.Events.Where(line => line.StartsWith("REMOTE_LANE_POLICY ")).Order());
        Assert.DoesNotContain(scenario.OutcomeEvents, line => line.StartsWith("REMOTE_LANE_POLICY "));
    }

    [Fact]
    public async Task LongPolicyIsTruncatedOnlyForConductSink()
    {
        using var scenario = new RemoteSlotInstrumentationScenarioTests.Scenario(1, keepLocal: true, nameSuffix: new string('x', 600));
        Assert.True((await scenario.RunAsync()).Passed);
        var full = Assert.Single(scenario.Progress, line => line.StartsWith("REMOTE_LANE_POLICY "));
        Assert.True(full.Length > 600);
        Assert.Equal(full[..512], Assert.Single(scenario.Events, line => line.StartsWith("REMOTE_LANE_POLICY ")));
        Assert.DoesNotContain(scenario.OutcomeEvents, line => line.StartsWith("REMOTE_LANE_POLICY "));
    }

    [Fact]
    public async Task ThrowingSinkCannotChangeGateVerdict()
    {
        using var scenario = new RemoteSlotInstrumentationScenarioTests.Scenario(1) { ThrowSink = true };
        Assert.True((await scenario.RunAsync()).Passed);
        Assert.Single(scenario.Events, line => line.StartsWith("REMOTE_LANE_POLICY "));
    }
}
