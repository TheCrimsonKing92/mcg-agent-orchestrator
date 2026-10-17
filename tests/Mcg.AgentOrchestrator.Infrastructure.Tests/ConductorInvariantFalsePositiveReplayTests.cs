using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: real logs, attempts and attention stores are fixture-local; all time is injected.
public sealed class ConductorInvariantFalsePositiveReplayTests
{
    [Fact]
    public void Replay_SoloLaneHeartbeats_SuppressesIncidentOneFalsePositive()
    {
        using var fixture = new Fixture();
        HoldAll(fixture, At("04:54:41"));
        var heartbeatStart = At("04:56:19");
        var path = LanePath(fixture, "operator", "operator-gate-20261017045619173-check");
        Replay(fixture, Reader(fixture), At("04:55:00"), At("05:40:00"), now =>
        {
            if (now >= heartbeatStart && OnInterval(now, heartbeatStart, 30)) WriteLane(path, now);
        }, observation =>
        {
            if (observation.Now >= heartbeatStart)
                Assert.Equal(LatestTick(observation.Now, heartbeatStart, 30), observation.LastGateProgressAt);
            AssertNoBreaches(fixture);
        });
    }

    [Fact]
    public void Replay_RunningTrainWithLaneAndInflight_SuppressesIncidentTwo()
    {
        using var fixture = new Fixture();
        HoldAll(fixture, At("05:20:40"), "Acceptance cohort gate owns this member");
        var attempt = fixture.NewAttempt();
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        Assert.Equal("Running", ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).Outcome);
        var start = At("05:21:00");
        var path = LanePath(fixture, fixture.Goals[0].Id.Value, "ea86e375-gate-20261017054001845-check");
        Replay(fixture, Reader(fixture), start, At("05:55:00"), now =>
        {
            if (OnInterval(now, start, 30)) WriteLane(path, now);
            if (OnInterval(now, start, 60)) Inflight(fixture);
        }, observation =>
        {
            Assert.Equal(observation.Now, observation.LastGateProgressAt);
            AssertNoBreaches(fixture);
        });
        Assert.Null(ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).ReconciledAt);
    }

    [Fact]
    public void Replay_OnlyInflightTicks_SuppressesIdleForFortyMinutes() => SingleSource("inflight");

    [Fact]
    public void Replay_OnlyLiveDrainTicks_SuppressesIdleForFortyMinutes() => SingleSource("drain");

    [Fact]
    public void Replay_OnlyLaneHeartbeats_SuppressesIdleForFortyMinutes() => SingleSource("lane");

    [Fact]
    public void Replay_ZeroLiveGates_RaisesExactlyOneIdleBreach()
    {
        using var fixture = new Fixture();
        var start = At("05:21:00");
        HoldAll(fixture, start);
        Replay(fixture, Reader(fixture), start, start.AddMinutes(40), now =>
        {
            if (OnInterval(now, start, 7)) Drain(fixture, 0);
        }, observation =>
        {
            Assert.Null(observation.LastGateProgressAt);
            var breaches = fixture.InvariantEvents("INVARIANT_BREACH");
            if (observation.Now <= start.AddMinutes(10)) Assert.Empty(breaches);
            else AssertBreach(breaches, "gate-idle-while-ready", start.AddMinutes(11));
        });
        Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        Assert.Single(fixture.OpenItems());
        Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
    }

    [Fact]
    public void Replay_InflightStops_RaisesAfterTenMinutesAndLaneClears()
    {
        using var fixture = new Fixture();
        var start = At("05:21:00");
        var stop = At("05:30:00");
        HoldAll(fixture, At("05:20:40"));
        var reader = Reader(fixture);
        Replay(fixture, reader, start, At("05:41:00"), now =>
        {
            if (now <= stop && OnInterval(now, start, 60)) Inflight(fixture);
        }, observation =>
        {
            Assert.Equal(observation.Now <= stop ? observation.Now : stop, observation.LastGateProgressAt);
            var breaches = fixture.InvariantEvents("INVARIANT_BREACH");
            if (observation.Now <= stop.AddMinutes(10)) Assert.Empty(breaches);
            else AssertBreach(breaches, "gate-idle-while-ready", At("05:41:00"));
        });
        Assert.Single(fixture.OpenItems());
        fixture.Clock.Now = At("05:42:00");
        WriteLane(LanePath(fixture, "operator", "operator-gate-recovery-check"), fixture.Clock.Now);
        var recovered = Evaluate(fixture, fixture.Monitor(), reader);
        Assert.Equal(fixture.Clock.Now, recovered.LastGateProgressAt);
        Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        AssertBreach(fixture.InvariantEvents("INVARIANT_BREACH"), "gate-idle-while-ready", At("05:41:00"));
        var cleared = Assert.Single(fixture.InvariantEvents("INVARIANT_CLEARED"));
        Assert.Contains("kind=gate-idle-while-ready ", cleared.GetProperty("detail").GetString());
        Assert.Equal(fixture.Clock.Now, cleared.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Empty(fixture.OpenItems());
    }

    [Fact]
    public void Replay_RefuseLoopWithStaleLane_PreservesBothBreachTimes()
    {
        using var fixture = new Fixture();
        fixture.ReadyAll();
        var stale = At("00:05:00");
        fixture.Heartbeat(stale);
        WriteLane(LanePath(fixture, fixture.Goals[0].Id.Value, "train-stale-check"), stale);
        var refused = 0;
        Replay(fixture, Reader(fixture), At("00:06:00"), At("00:45:00"), now =>
        {
            if (now.Second == 0 && now.Minute <= 42 && now.Minute % 2 == 0)
            {
                fixture.Refuse("main");
                refused++;
            }
        }, observation =>
        {
            Assert.Equal(stale, observation.LastGateProgressAt);
            Assert.Equal(refused, observation.Refusals.Count);
            var breaches = fixture.InvariantEvents("INVARIANT_BREACH");
            Assert.Equal(observation.Now < At("00:14:00") ? 0 : observation.Now < At("00:16:00") ? 1 : 2, breaches.Length);
            Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
        });
        Assert.Equal(19, refused);
        Assert.Equal(19, fixture.StopCalls);
        Assert.Equal(19, fixture.Coordinator.ReadAll().Count());
        var raised = fixture.InvariantEvents("INVARIANT_BREACH");
        Assert.Equal(2, raised.Length);
        AssertBreach(raised, "repeated-refusal", At("00:14:00"));
        AssertBreach(raised, "gate-idle-while-ready", At("00:16:00"));
        Assert.Equal(2, fixture.OpenItems().Count);
    }

    private static void SingleSource(string source)
    {
        using var fixture = new Fixture();
        var start = At("05:21:00");
        HoldAll(fixture, At("05:20:40"));
        var interval = source == "drain" ? 7 : source == "lane" ? 30 : 60;
        var path = LanePath(fixture, fixture.Goals[0].Id.Value, "train-only-lane-check");
        Replay(fixture, Reader(fixture), start, start.AddMinutes(40), now =>
        {
            if (!OnInterval(now, start, interval)) return;
            switch (source)
            {
                case "inflight": Inflight(fixture); break;
                case "drain": Drain(fixture, 1); break;
                case "lane": WriteLane(path, now); break;
                default: throw new ArgumentOutOfRangeException(nameof(source));
            }
        }, observation =>
        {
            Assert.Equal(LatestTick(observation.Now, start, interval), observation.LastGateProgressAt);
            AssertNoBreaches(fixture);
        });
    }

    private static void Replay(Fixture fixture, ConductorInvariantObservationReader reader,
        DateTimeOffset start, DateTimeOffset end, Action<DateTimeOffset> emit,
        Action<ConductorInvariantObservation> verify)
    {
        var monitor = fixture.Monitor();
        for (var now = start; now <= end; now = now.AddSeconds(1))
        {
            fixture.Clock.Now = now;
            emit(now);
            if (now.Second == 0) verify(Evaluate(fixture, monitor, reader));
        }
    }

    private static ConductorInvariantObservation Evaluate(Fixture fixture,
        ConductorProgramHealthMonitor monitor, ConductorInvariantObservationReader reader)
    {
        ConductorInvariantObservation? observed = null;
        monitor.Evaluate(fixture.Goals, null, (goals, now) => observed = reader.Read(goals, now));
        Assert.NotNull(observed); // A swallowed observation failure must never become a green replay.
        Assert.Equal(12, observed.GateReadyGoals.Count);
        return observed;
    }

    private static ConductorInvariantObservationReader Reader(Fixture fixture) => new(fixture.Log, fixture.AttemptRoot,
        now => GateHeartbeatArtifacts.ReadStableSlots(new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build-storage")), now),
        Path.Combine(fixture.Root, "acceptance-gate-attempts"));

    private static void HoldAll(Fixture fixture, DateTimeOffset ready, string reason = "acceptance width 1 reached")
    {
        fixture.Clock.Now = ready;
        for (var index = 0; index < fixture.Goals.Length; index++) fixture.Hold(index, reason);
    }

    private static string LanePath(Fixture fixture, string folder, string name) =>
        Path.Combine(fixture.Root, "acceptance-gate-attempts", folder, name + ".gate-heartbeat.json");

    private static void WriteLane(string path, DateTimeOffset now)
    {
        GateHeartbeatArtifacts.Write(path, new(null, "test", "lane", null, null, null, "running", now, now, now, 0, 0, 0));
        File.SetLastWriteTimeUtc(path, now.UtcDateTime);
    }

    private static void Inflight(Fixture fixture) => fixture.Writer.Append("acceptance-cohort", null,
        "ACCEPTANCE_COHORT_INFLIGHT members=a+b outcome=inflight elapsed_ms=60000");

    private static void Drain(Fixture fixture, int liveGates) => fixture.Writer.Append("loop", null,
        $"LOOP_RELAUNCH_DRAIN tick=9 active=1 admitting=false liveGates={liveGates}");

    private static void AssertNoBreaches(Fixture fixture)
    {
        Assert.Empty(fixture.InvariantEvents("INVARIANT_BREACH"));
        Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
        Assert.Empty(fixture.OpenItems());
    }

    private static void AssertBreach(JsonElement[] rows, string kind, DateTimeOffset at)
    {
        var row = Assert.Single(rows, item => item.GetProperty("detail").GetString()!
            .Contains("kind=" + kind + " ", StringComparison.Ordinal));
        Assert.Equal(at, row.GetProperty("timestamp").GetDateTimeOffset());
    }

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-10-17T" + time + "Z");
    private static bool OnInterval(DateTimeOffset now, DateTimeOffset start, int seconds) =>
        (now - start).Ticks % TimeSpan.FromSeconds(seconds).Ticks == 0;
    private static DateTimeOffset LatestTick(DateTimeOffset now, DateTimeOffset start, int seconds) =>
        now.AddTicks(-((now - start).Ticks % TimeSpan.FromSeconds(seconds).Ticks));
}
