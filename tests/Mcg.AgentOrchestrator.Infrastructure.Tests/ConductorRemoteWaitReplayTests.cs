using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: each replay owns its workspace, logs, database and injected clock.
public sealed class ConductorRemoteWaitReplayTests
{
    [Fact]
    public void Replay_RunningAttemptEveryMinute_SuppressesIdleForNinetyMinutes() => Replay("running");

    [Fact]
    public void Replay_StaleAndCompletedAttempts_BreachAtMissingHeartbeatBaseline()
    {
        var baseline = Replay("missing");
        Assert.NotNull(baseline);
        Assert.Equal(baseline, Replay("stale"));
        Assert.Equal(baseline, Replay("completed"));
    }

    [Theory]
    [InlineData("acceptance-gate-attempts", true)]
    [InlineData("other-attempts", false)]
    public void ForWorkspace_AttemptFolder_OnlyAcceptanceRootContributes(string folder, bool expected)
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var now = fixture.Clock.Now;
        Write(Path.Combine(workspace.OrchestratorDirectory, folder, "goal", "attempt.heartbeat.json"),
            "running", now);
        var reader = ConductorInvariantObservationReader.ForWorkspace(workspace,
            new DotnetBuildStorageRoot(Path.Combine(fixture.Root, "build-storage")));
        Assert.Equal(expected ? now : (DateTimeOffset?)null, reader.Read([], now).LastGateProgressAt);
    }

    private static DateTimeOffset? Replay(string mode)
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        for (var index = 0; index < fixture.Goals.Length; index++)
            fixture.Hold(index, "acceptance width 1 reached");
        var root = Path.Combine(fixture.Root, "acceptance-gate-attempts");
        var path = Path.Combine(root, fixture.Goals[0].Id.Value, "attempt.heartbeat.json");
        var reader = new ConductorInvariantObservationReader(fixture.Log, fixture.AttemptRoot, _ => [], root);
        var monitor = fixture.Monitor();
        for (var minute = 0; minute <= 90; minute++)
        {
            fixture.Clock.Now = start.AddMinutes(minute);
            if (mode is "running" or "completed" || mode == "stale" && minute == 0)
                Write(path, mode == "completed" ? "completed" : "running", fixture.Clock.Now);
            ConductorInvariantObservation? observation = null;
            monitor.Evaluate(fixture.Goals, null, (goals, now) => observation = reader.Read(goals, now));
            Assert.NotNull(observation); // Observation failures swallowed by the monitor must fail the replay.
            Assert.Equal(12, observation.GateReadyGoals.Count);
            Assert.All(observation.GateReadyGoals, goal => Assert.Equal(start, goal.ReadySince));
            var expectedProgress = mode == "running" ? fixture.Clock.Now :
                mode == "stale" && minute <= 10 ? start : (DateTimeOffset?)null;
            Assert.Equal(expectedProgress, observation.LastGateProgressAt);
            var breaches = fixture.InvariantEvents("INVARIANT_BREACH");
            if (mode == "running" || minute <= 10)
            {
                Assert.Empty(breaches);
                Assert.Empty(fixture.OpenItems());
            }
            else
            {
                var breach = Assert.Single(breaches);
                Assert.Contains("kind=gate-idle-while-ready ", breach.GetProperty("detail").GetString());
                Assert.Equal(start.AddMinutes(11), breach.GetProperty("timestamp").GetDateTimeOffset());
                Assert.Single(fixture.OpenItems());
            }
        }
        Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
        return mode == "running" ? null : Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"))
            .GetProperty("timestamp").GetDateTimeOffset();
    }

    private static void Write(string path, string state, DateTimeOffset now)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { kind = "solo", pid = 123,
            state, lastProgressAt = now, lastObservedAt = now }));
        File.SetLastWriteTimeUtc(path, now.UtcDateTime);
    }
}
