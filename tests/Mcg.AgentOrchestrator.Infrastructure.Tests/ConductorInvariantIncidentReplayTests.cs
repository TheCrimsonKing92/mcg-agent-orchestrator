using Mcg.AgentOrchestrator.App.Orchestration;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: real state is fixture-local; time, process liveness, stop and launch are injected.
public sealed class ConductorInvariantIncidentReplayTests
{
    [Fact]
    public void Replay_NineteenRefusals_RaisesTwoBreachesAndClearsOnProgress()
    {
        using var fixture = new Fixture();
        fixture.ReadyAll();
        fixture.Heartbeat(DateTimeOffset.Parse("2026-10-17T00:05:00Z"));
        var monitor = fixture.Monitor();
        var refusals = 0;
        for (var minute = 6; minute <= 45; minute++)
        {
            fixture.Clock.Now = DateTimeOffset.Parse("2026-10-17T00:00:00Z").AddMinutes(minute);
            if (minute <= 42 && minute % 2 == 0)
            {
                fixture.Refuse("main");
                refusals++;
            }
            var observation = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now);
            Assert.Equal(12, observation.GateReadyGoals.Count);
            Assert.Equal(refusals, observation.Refusals.Count);
            Assert.Equal(DateTimeOffset.Parse("2026-10-17T00:05:00Z"), observation.LastGateProgressAt);
            if (minute == 30) monitor = fixture.Monitor();
            monitor.Evaluate(fixture.Goals, null, fixture.Reader.Read);
            var breaches = fixture.InvariantEvents("INVARIANT_BREACH");
            Assert.Equal(minute < 14 ? 0 : minute < 16 ? 1 : 2, breaches.Length);
            if (minute < 14) Assert.Empty(fixture.OpenItems());
            Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
        }
        Assert.Equal(19, refusals);
        Assert.Equal(19, fixture.StopCalls);
        Assert.Equal(19, fixture.Coordinator.ReadAll().Count());
        var raised = fixture.InvariantEvents("INVARIANT_BREACH");
        var repeated = Assert.Single(raised, row => row.GetProperty("detail").GetString()!
            .Contains("kind=repeated-refusal ", StringComparison.Ordinal));
        var idle = Assert.Single(raised, row => row.GetProperty("detail").GetString()!
            .Contains("kind=gate-idle-while-ready ", StringComparison.Ordinal));
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T00:14:00Z"), repeated.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T00:16:00Z"), idle.GetProperty("timestamp").GetDateTimeOffset());
        Assert.All(raised, row => Assert.Equal("decision", row.GetProperty("operator").GetString()));
        Assert.Equal(2, fixture.OpenItems().Count);

        fixture.Clock.Now = DateTimeOffset.Parse("2026-10-17T00:46:00Z");
        fixture.Heartbeat(fixture.Clock.Now);
        monitor.Evaluate(fixture.Goals, null, fixture.Reader.Read);
        monitor.Evaluate(fixture.Goals, null, fixture.Reader.Read);
        Assert.Equal(2, fixture.InvariantEvents("INVARIANT_BREACH").Length);
        Assert.Equal(2, fixture.InvariantEvents("INVARIANT_CLEARED").Length);
        Assert.All(fixture.InvariantEvents("INVARIANT_CLEARED"),
            row => Assert.Equal("outcome", row.GetProperty("operator").GetString()));
        Assert.Empty(fixture.OpenItems());
    }

    [Fact]
    public void Replay_NinetyMinuteProgressingGate_RaisesNoBreaches()
    {
        using var fixture = new Fixture();
        fixture.ReadyAll();
        var running = fixture.NewAttempt();
        ConductorGroupedGateAttemptCoordinator.Save(running);
        Assert.Equal("Running", ConductorGroupedGateAttemptCoordinator.Read(running.MetadataPath).Outcome);
        var start = fixture.Clock.Now;
        var monitor = fixture.Monitor();
        for (var minute = 0; minute <= 90; minute++)
        {
            fixture.Clock.Now = start.AddMinutes(minute);
            if (minute % 2 == 0) fixture.Heartbeat(fixture.Clock.Now);
            var observation = fixture.Reader.Read(fixture.Goals, fixture.Clock.Now);
            Assert.Equal(12, observation.GateReadyGoals.Count);
            Assert.Empty(observation.Refusals);
            Assert.Equal(start.AddMinutes(minute - minute % 2), observation.LastGateProgressAt);
            monitor.Evaluate(fixture.Goals, null, fixture.Reader.Read);
            Assert.Empty(fixture.InvariantEvents("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
        Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
        Assert.Equal(0, fixture.StopCalls);
    }
}
