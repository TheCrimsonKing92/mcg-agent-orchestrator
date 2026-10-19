using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: each replay owns its real log, SQLite attention store and injected clock.
public sealed class ConductorInvariantProbeWindowReplayTests
{
    [Fact]
    public void Evaluate_HealthyProbeWindow_StaysQuietUntilAndAfterAdoption()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        Handoff(fixture, start.AddMinutes(1));
        var nextProgress = start.AddMinutes(2).AddSeconds(20);
        for (var minute = 0; minute <= 30; minute++)
        {
            var now = start.AddMinutes(minute);
            ProgressThrough(fixture, ref nextProgress, now, start.AddMinutes(20).AddSeconds(30));
            fixture.Tick(now);
            AssertQuiet(fixture);
        }

        // Delay delivery to prove the pending window itself stays quiet through T+30.
        fixture.Append("ACTIVATION_ADOPTED adoptedCommit=d367dc3f8", start.AddMinutes(21));
        for (var minute = 31; minute <= 90; minute++)
        {
            fixture.Tick(start.AddMinutes(minute));
            AssertQuiet(fixture);
        }
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(10, 26)]
    public void Evaluate_SilentOrStoppedSuccessor_RaisesOnceAfterSilence(
        int lastProgressMinute, int firstBreachMinute)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        Handoff(fixture, start.AddMinutes(1));
        var nextProgress = start.AddMinutes(2);
        for (var minute = 0; minute <= firstBreachMinute + 30; minute++)
        {
            var now = start.AddMinutes(minute);
            ProgressThrough(fixture, ref nextProgress, now, start.AddMinutes(lastProgressMinute));
            fixture.Tick(now);
            if (minute < firstBreachMinute) AssertQuiet(fixture);
            else
            {
                AssertBreach(fixture);
                var since = start.AddMinutes(lastProgressMinute == 0 ? 1 : lastProgressMinute);
                Assert.Contains($"since={since:O}", fixture.Events("INVARIANT_BREACH")[0]
                    .GetProperty("detail").GetString());
            }
        }
    }

    [Fact]
    public void Evaluate_EndlessProgress_RaisesAtCeilingThenRevertClears()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        var handoff = start.AddMinutes(1);
        fixture.Schedule();
        Handoff(fixture, handoff);
        var nextProgress = start.AddMinutes(2);
        for (var minute = 0; minute <= 77; minute++)
        {
            var now = start.AddMinutes(minute);
            ProgressThrough(fixture, ref nextProgress, now, now);
            fixture.Tick(now);
            if (minute <= 76) AssertQuiet(fixture);
            else
            {
                AssertBreach(fixture);
                var detail = fixture.Events("INVARIANT_BREACH")[0].GetProperty("detail").GetString();
                Assert.Contains($"since={handoff.AddMinutes(60):O}", detail);
                Assert.Contains($"handoffAt:{handoff:O}", detail);
            }
        }
        fixture.Append("ACTIVATION_REVERTED reason=probe", start.AddMinutes(78));
        fixture.Tick(start.AddMinutes(78));
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.Equal($"INVARIANT_CLEARED kind=relaunch-without-activation record={fixture.Goals[0].Id.Value}",
            Assert.Single(fixture.Events("INVARIANT_CLEARED")).GetProperty("detail").GetString());
        Assert.Empty(fixture.OpenItems());
    }

    [Fact]
    public void Evaluate_ProgressWithoutHandoff_DoesNotDelayBreach()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        var nextProgress = start.AddSeconds(30);
        for (var minute = 0; minute <= 16; minute++)
        {
            var now = start.AddMinutes(minute);
            ProgressThrough(fixture, ref nextProgress, now, now);
            fixture.Tick(now);
            if (minute <= 15) AssertQuiet(fixture);
            else AssertBreach(fixture);
        }
        var detail = fixture.Events("INVARIANT_BREACH")[0].GetProperty("detail").GetString();
        Assert.Contains($"since={start:O}", detail);
        Assert.Contains("lastLiveGateAt:none", detail);
    }

    [Fact]
    public void Evaluate_LiveDrainWithoutHandoff_RemainsQuietForNinetyMinutes()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        for (var tick = 0; tick <= 180; tick++)
        {
            fixture.Clock.Now = start.AddSeconds(tick * 30);
            fixture.Drain(1, tick); // Uses ConductorSelfRelaunchDrainHold.DescribeProgress.
            fixture.Tick(fixture.Clock.Now);
            AssertQuiet(fixture);
        }
    }

    private static void Handoff(Fixture fixture, DateTimeOffset at) => fixture.Writer.Append(
        "loop-handoff", null,
        "LOOP_HANDOFF attempt=18 stagedSourceCommit=d367dc3f8 repositoryHead=d367dc3f8 runDir=run", at);

    private static void ProgressThrough(Fixture fixture, ref DateTimeOffset next,
        DateTimeOffset now, DateTimeOffset last)
    {
        while (next <= now && next <= last)
        {
            // Production formatters are private; use the documented single-goal wire shape.
            fixture.Writer.Append("gate-progress", null,
                "PHASE_PROGRESS goal=9c9f8266 phase=Tests elapsed_ms=30000 target=x child_pid=1 output_bytes=0 heartbeat=h", next);
            next = next.AddSeconds(30);
        }
    }

    private static void AssertQuiet(Fixture fixture)
    {
        Assert.Empty(fixture.Events("INVARIANT_BREACH"));
        Assert.Empty(fixture.OpenItems());
    }

    private static void AssertBreach(Fixture fixture)
    {
        Assert.StartsWith($"INVARIANT_BREACH kind=relaunch-without-activation record={fixture.Goals[0].Id.Value} ",
            Assert.Single(fixture.Events("INVARIANT_BREACH")).GetProperty("detail").GetString());
        Assert.Equal($"invariant-breach:relaunch-without-activation:{fixture.Goals[0].Id.Value}",
            Assert.Single(fixture.OpenItems()).CorrelationKey);
    }
}
