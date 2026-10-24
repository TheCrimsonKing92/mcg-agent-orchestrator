using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: pure readings, async-context overrides, and an injected supervisor host.
public sealed class ProcessJobTrapProbeTests
{
    [Fact]
    public void TrapRuleMatchesKillOnCloseWithoutBreakaway()
    {
        Assert.True(ProcessJobTrapProbe.Traps(new(true, 0x00002000)));
        Assert.True(ProcessJobTrapProbe.Traps(new(true, 0x00002008)));
        Assert.False(ProcessJobTrapProbe.Traps(new(true, 0x00002800)));
        Assert.False(ProcessJobTrapProbe.Traps(new(true, 0x00003000)));
        Assert.False(ProcessJobTrapProbe.Traps(new(true, 0x00000000)));
        Assert.False(ProcessJobTrapProbe.Traps(new(false, 0x00002000)));
        Assert.False(ProcessJobTrapProbe.Traps(ProcessJobReading.Unknown));
        Assert.False(ProcessJobTrapProbe.Traps(new(true, null)));
        Assert.False(ProcessJobTrapProbe.Traps(new(null, 0x00002000)));
    }

    [Theory]
    [InlineData(true, 0xABCDEF08u, "true", "0xABCDEF08")]
    [InlineData(false, null, "false", null)]
    [InlineData(null, null, "unknown", null)]
    public async Task SupervisorBuildRecordsScopedReading(bool? membership, uint? flags,
        string expectedMembership, string? expectedFlags)
    {
        using var reading = ProcessJobTrapProbe.UseSupervisorReading(new(membership, flags));
        var fixture = new SupervisorHandoffFixture();
        Assert.Equal(0, await fixture.RunAsync());
        var builds = fixture.ConductEvents.Where(detail => detail.StartsWith("SUPERVISOR_BUILD ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(builds);
        foreach (var detail in builds)
        {
            using var payload = JsonDocument.Parse(detail[detail.IndexOf('{')..]);
            Assert.Equal(expectedMembership, payload.RootElement.GetProperty("jobMembership").GetString());
            Assert.Equal(expectedFlags, payload.RootElement.GetProperty("innermostJobLimitFlags").GetString());
            Assert.Equal("commit-a", payload.RootElement.GetProperty("commitSha").GetString());
        }
    }

    [Fact]
    public async Task SupervisorReadingRestoresNestedScopesAndIsolatesAsyncContexts()
    {
        var outer = new ProcessJobReading(false, null);
        var inner = new ProcessJobReading(true, 0x2000);
        using var scope = ProcessJobTrapProbe.UseSupervisorReading(outer);
        using (ProcessJobTrapProbe.UseSupervisorReading(inner))
            Assert.Equal(inner, ProcessJobTrapProbe.ReadForSupervisor());
        Assert.Equal(outer, ProcessJobTrapProbe.ReadForSupervisor());

        await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            var reading = new ProcessJobReading(true, (uint)(0x2000 + index));
            using var childScope = ProcessJobTrapProbe.UseSupervisorReading(reading);
            await Task.Yield();
            Assert.Equal(reading, ProcessJobTrapProbe.ReadForSupervisor());
        })));
        Assert.Equal(outer, ProcessJobTrapProbe.ReadForSupervisor());
    }
}
