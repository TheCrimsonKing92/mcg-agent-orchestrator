using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure rules and a fixture-local clock, with no I/O or process state.
public sealed class ConductorInvariantWatchdogTests
{
    [Fact]
    public void IdleWindow_StrictThreshold_RaisesOnceAcrossOneHundredTicks()
    {
        var clock = new OwnerConsoleTestClock();
        var since = clock.Now;
        var ready = Enumerable.Range(0, 12).Select(index =>
            new ConductorInvariantReadyGoal($"goal-{index}", since.AddSeconds(index))).Reverse().ToArray();
        clock.Now = since.Add(ConductorInvariantThresholds.GateIdleWhileReady);
        ConductorInvariantObservation Observe() => new(clock.Now, ready, since, []);
        var result = ConductorInvariantWatchdog.Evaluate(Observe(), new Dictionary<string, DateTimeOffset>());
        Assert.Empty(result.Raise);
        clock.Now = clock.Now.AddSeconds(1);
        result = ConductorInvariantWatchdog.Evaluate(Observe(), result.Active);
        var breach = Assert.Single(result.Raise);
        Assert.Equal(ConductorInvariantThresholds.GateIdleWhileReadyKind, breach.Kind);
        Assert.Equal("goal-0", breach.Record);
        Assert.Equal(since, breach.Since);
        for (var tick = 0; tick < 100; tick++)
        {
            clock.Now = clock.Now.AddMinutes(1);
            result = ConductorInvariantWatchdog.Evaluate(Observe(), result.Active);
            Assert.Empty(result.Raise);
            Assert.Empty(result.Clear);
            Assert.Equal(since, Assert.Single(result.Active).Value);
        }
    }

    [Fact]
    public void RefusalRun_FourFiveAndNineteen_RaisesOnce()
    {
        var clock = new OwnerConsoleTestClock();
        var refusals = new List<ConductorInvariantRefusal>();
        IReadOnlyDictionary<string, DateTimeOffset> active = new Dictionary<string, DateTimeOffset>();
        var raised = new List<ConductorInvariantBreach>();
        for (var count = 1; count <= 19; count++)
        {
            clock.Now = clock.Now.AddMinutes(1);
            refusals.Add(new("members", "identity-changed field=main", clock.Now));
            var result = ConductorInvariantWatchdog.Evaluate(new(clock.Now, [], null, refusals), active);
            if (count < ConductorInvariantThresholds.RepeatedRefusalCount) Assert.Empty(result.Raise);
            if (count == ConductorInvariantThresholds.RepeatedRefusalCount) Assert.Single(result.Raise);
            raised.AddRange(result.Raise);
            active = result.Active;
        }
        var breach = Assert.Single(raised);
        Assert.Equal(ConductorInvariantThresholds.RepeatedRefusalKind, breach.Kind);
        Assert.Equal("members", breach.Record);
        Assert.Equal(refusals[0].At, breach.Since);
    }

    [Theory]
    [InlineData("records")]
    [InlineData("reasons")]
    [InlineData("progress")]
    public void RefusalRun_ChangedIdentityOrProgress_DoesNotRaise(string control)
    {
        var now = DateTimeOffset.UnixEpoch;
        var refusals = Enumerable.Range(0, control == "progress" ? 5 : 10).Select(index =>
            new ConductorInvariantRefusal(control == "records" ? $"record-{index % 2}" : "record",
                control == "reasons" ? $"reason-{index % 2}" : "reason", now.AddMinutes(index))).ToArray();
        var result = ConductorInvariantWatchdog.Evaluate(new(now.AddHours(1), [],
            control == "progress" ? now.AddSeconds(1) : null, refusals), new Dictionary<string, DateTimeOffset>());
        Assert.Empty(result.Raise);
        Assert.Empty(result.Active);
    }

    [Fact]
    public void IdleWindow_NoReadyGoals_DoesNotRaiseAndUsesNamedThresholds()
    {
        var now = DateTimeOffset.UnixEpoch;
        var result = ConductorInvariantWatchdog.Evaluate(new(now, [], now.AddHours(-3), []),
            new Dictionary<string, DateTimeOffset>());
        Assert.Empty(result.Raise);
        Assert.Equal(TimeSpan.FromMinutes(10), ConductorInvariantThresholds.GateIdleWhileReady);
        Assert.Equal(5, ConductorInvariantThresholds.RepeatedRefusalCount);
    }

    [Fact]
    public void IdleWindow_NewlyReadyGoal_DoesNotInheritOldGateIdleness()
    {
        var now = DateTimeOffset.UnixEpoch;
        var result = ConductorInvariantWatchdog.Evaluate(new(now, [new("goal", now)], now.AddHours(-3), []),
            new Dictionary<string, DateTimeOffset>());
        Assert.Empty(result.Raise);
    }
}
