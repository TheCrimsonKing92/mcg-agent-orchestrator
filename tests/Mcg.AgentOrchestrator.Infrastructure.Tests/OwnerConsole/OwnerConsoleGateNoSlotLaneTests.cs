using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel safe: immutable events and an injected clock.
public sealed class OwnerConsoleGateNoSlotLaneTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-30)]
    [InlineData(-20)]
    public void NoSlotQueuesUntilLocalPhaseThenShowsLocalSlot(int noSlotTime)
    {
        var events = Offered().Append(Event(noSlotTime, "remote-lane",
            "REMOTE_LANE executor=none lane=\"infrastructure tests: A\" attempt=1 outcome=offer-skipped-no-executor reason=no-idle-slot")).ToArray();
        var queued = Row(events);
        Assert.Equal(OwnerConsoleGateLaneState.Queued, queued.State);
        Assert.Equal("no executor slot", queued.Location);
        var running = Row(events.Append(Event(-10, "gate-progress",
            "ACCEPTANCE_PROGRESS phase=verification-check target=\"infrastructure tests: A\" heartbeat=build-slots/activity-2.heartbeat.json")));
        Assert.Equal(OwnerConsoleGateLaneState.Running, running.State);
        Assert.Equal("local slot 2", running.Location);
    }

    [Fact]
    public void OfferWithoutNoSlotRemainsRunningRemote()
    {
        var row = Row(Offered());
        Assert.Equal(OwnerConsoleGateLaneState.Running, row.State);
        Assert.Equal("remote", row.Location);
    }

    private static OwnerConductEvent[] Offered() => [
        Event(-40, "acceptance", "ACCEPTANCE result=started"),
        Event(-30, "remote-lane", "REMOTE_LANE_POLICY lane=\"infrastructure tests: A\" decision=offer reason=eligible")];
    private static OwnerConductEvent Event(int seconds, string kind, string detail) => new(Now.AddSeconds(seconds), kind, "11111111", detail);
    private static OwnerConsoleGateLane Row(IEnumerable<OwnerConductEvent> events) =>
        Assert.Single(OwnerConsoleGateProgress.Build(Now, events, [new("A", 100)], []).Lanes, row => row.Name == "A");
}
