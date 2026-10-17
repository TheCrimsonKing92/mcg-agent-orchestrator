using Mcg.AgentOrchestrator.App.Orchestration;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: log, state, attention store and clock are isolated per replay.
public sealed class ConductorInvariantRelaunchReplayTests
{
    [Fact]
    public void Evaluate_SilentRelaunch_RaisesOnceThenAdoptionClears()
    {
        using var fixture = new Fixture();
        var start = DateTimeOffset.Parse("2026-10-16T22:39:00Z");
        fixture.Clock.Now = start;
        fixture.Schedule();
        for (var tick = 0; tick <= 30; tick++)
        {
            fixture.Clock.Now = start.AddSeconds(tick * 30);
            if (tick <= 10) fixture.Drain(0, tick);
            fixture.Tick(fixture.Clock.Now);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
        }
        fixture.Tick(start.AddMinutes(16));
        AssertBreach(fixture, "relaunch-without-activation", fixture.Goals[0].Id.Value);
        for (var minute = 17; minute <= 46; minute++)
        {
            fixture.Tick(start.AddMinutes(minute));
            Assert.Single(fixture.Events("INVARIANT_BREACH"));
            Assert.Single(fixture.OpenItems());
        }
        fixture.Append("ACTIVATION_ADOPTED adoptedCommit=main");
        fixture.Tick(start.AddMinutes(47));
        Assert.Equal($"INVARIANT_CLEARED kind=relaunch-without-activation record={fixture.Goals[0].Id.Value}",
            Assert.Single(fixture.Events("INVARIANT_CLEARED")).GetProperty("detail").GetString());
        Assert.Empty(fixture.OpenItems());
        fixture.Tick(start.AddMinutes(48));
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.Single(fixture.Events("INVARIANT_CLEARED"));
    }

    [Theory]
    [InlineData("ACTIVATION_ADOPTED adoptedCommit=main")]
    [InlineData("ACTIVATION_REVERTED reason=probe")]
    [InlineData("LOOP_HANDOFF_FAILED reason=probe")]
    public void Evaluate_OutcomeBeforeThreshold_PreventsSilentRelaunchBreach(string outcome)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        for (var minute = 0; minute <= 60; minute++)
        {
            fixture.Clock.Now = start.AddMinutes(minute);
            if (minute == 14) fixture.Append(outcome);
            fixture.Tick(fixture.Clock.Now);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
    }

    [Theory]
    [InlineData("ACTIVATION_REVERTED reason=self-check")]
    [InlineData("LOOP_HANDOFF_FAILED reason=exit")]
    public void Evaluate_FailedActivationAndOldBuild_RaisesBehindMainThenClears(string outcome)
    {
        using var fixture = new Fixture();
        var start = DateTimeOffset.Parse("2026-10-16T23:49:00Z");
        fixture.Clock.Now = start;
        fixture.Build(Fixture.BuildA);
        fixture.Schedule();
        for (var minute = 0; minute <= 30; minute++)
        {
            fixture.Clock.Now = start.AddMinutes(minute);
            if (minute == 2) fixture.Append(outcome);
            fixture.Tick(fixture.Clock.Now, Fixture.BuildB);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
        fixture.Tick(start.AddMinutes(31), Fixture.BuildB);
        AssertBreach(fixture, "generation-behind-main", Fixture.BuildB[..12]);
        fixture.Tick(start.AddMinutes(32), Fixture.BuildB);
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        fixture.Build(Fixture.BuildB);
        fixture.Tick(start.AddMinutes(33), Fixture.BuildB);
        Assert.Equal($"INVARIANT_CLEARED kind=generation-behind-main record={Fixture.BuildB[..12]}",
            Assert.Single(fixture.Events("INVARIANT_CLEARED")).GetProperty("detail").GetString());
        Assert.Empty(fixture.OpenItems());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Evaluate_CurrentBuildOrUnscheduledLanding_RemainsSilent(bool scheduled)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Build(Fixture.BuildA);
        if (scheduled) fixture.Schedule();
        fixture.Append("ACTIVATION_REVERTED reason=probe", start.AddMinutes(2));
        for (var minute = 0; minute <= 60; minute++)
        {
            fixture.Tick(start.AddMinutes(minute), scheduled ? Fixture.BuildA : Fixture.BuildB);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
    }

    [Theory]
    [InlineData(false, 15, "relaunch-without-activation")]
    [InlineData(true, 30, "generation-behind-main")]
    public void Evaluate_LiveGateDrain_AnchorsThresholdAtLastLiveLine(bool failure,
        int thresholdMinutes, string kind)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Build(Fixture.BuildA);
        fixture.Schedule();
        for (var tick = 0; tick <= 180; tick++)
        {
            fixture.Clock.Now = start.AddSeconds(tick * 30);
            fixture.Drain(1, tick); // Exercise the production DescribeProgress formatter.
            fixture.Tick(fixture.Clock.Now, Fixture.BuildB);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
        var lastLive = fixture.Clock.Now;
        for (var tick = 1; tick <= thresholdMinutes * 2; tick++)
        {
            fixture.Clock.Now = lastLive.AddSeconds(tick * 30);
            fixture.Drain(0, tick + 180);
            if (failure && tick == 1) fixture.Append("ACTIVATION_REVERTED reason=self-check");
            fixture.Tick(fixture.Clock.Now, Fixture.BuildB);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
        fixture.Tick(lastLive.AddMinutes(thresholdMinutes).AddSeconds(1), Fixture.BuildB);
        AssertBreach(fixture, kind, failure ? Fixture.BuildB[..12] : fixture.Goals[0].Id.Value);
        Assert.Contains($"since={lastLive:O}", Assert.Single(fixture.Events("INVARIANT_BREACH"))
            .GetProperty("detail").GetString());
    }

    [Fact]
    public void Evaluate_LateFailure_ClearsSilentBreachBeforeBehindMainThreshold()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Build(Fixture.BuildA);
        fixture.Schedule();
        fixture.Tick(start.AddMinutes(16), Fixture.BuildB);
        AssertBreach(fixture, "relaunch-without-activation", fixture.Goals[0].Id.Value);
        fixture.Append("LOOP_HANDOFF_FAILED reason=exit");
        fixture.Tick(start.AddMinutes(17), Fixture.BuildB);
        Assert.Single(fixture.Events("INVARIANT_CLEARED"));
        Assert.Empty(fixture.OpenItems());
        fixture.Tick(start.AddMinutes(30), Fixture.BuildB);
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        fixture.Tick(start.AddMinutes(30).AddSeconds(1), Fixture.BuildB);
        Assert.Equal(2, fixture.Events("INVARIANT_BREACH").Length);
        Assert.Equal($"invariant-breach:generation-behind-main:{Fixture.BuildB[..12]}",
            Assert.Single(fixture.OpenItems()).CorrelationKey);
    }

    private static void AssertBreach(Fixture fixture, string kind, string record)
    {
        var breach = Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.StartsWith($"INVARIANT_BREACH kind={kind} record={record} ",
            breach.GetProperty("detail").GetString());
        Assert.Equal($"invariant-breach:{kind}:{record}", Assert.Single(fixture.OpenItems()).CorrelationKey);
    }
}
