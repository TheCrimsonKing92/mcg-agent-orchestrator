using System.Text.Json.Nodes;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: workspace reader, event writer, SQLite and clock use a unique fixture root.
public sealed class ConductorInvariantProbeWindowScenarioTests
{
    [Fact]
    public void Evaluate_WorkspaceProbeAcrossRestart_RemainsQuietAfterAdoption()
    {
        // Fixture constructs ForWorkspace(workspace, storage) and the real health monitor.
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        for (var minute = 0; minute <= 80; minute++)
        {
            fixture.Clock.Now = start.AddMinutes(minute);
            if (minute == 1) Handoff(fixture);
            if (minute is >= 2 and <= 18)
            {
                if (minute > 2) Progress(fixture, fixture.Clock.Now.AddSeconds(-30));
                Progress(fixture, fixture.Clock.Now);
            }
            if (minute == 10) fixture.Restart();
            if (minute == 20) fixture.Writer.Append("activation-adopted", null,
                "ACTIVATION_ADOPTED adoptedCommit=d367dc3f8", fixture.Clock.Now);
            fixture.Tick(fixture.Clock.Now);
            Assert.Empty(fixture.Events("INVARIANT_BREACH"));
            Assert.Empty(fixture.OpenItems());
        }
        Assert.Empty(JsonNode.Parse(File.ReadAllText(fixture.State))!["invariants"]!.AsObject());
    }

    [Fact]
    public void Evaluate_WorkspaceSilentSuccessor_RaisesOneOperatorDecision()
    {
        using var fixture = new Fixture();
        var start = fixture.Clock.Now;
        fixture.Schedule();
        for (var minute = 0; minute <= 47; minute++)
        {
            fixture.Clock.Now = start.AddMinutes(minute);
            if (minute == 1) Handoff(fixture);
            if (minute == 10) fixture.Restart();
            fixture.Tick(fixture.Clock.Now);
            if (minute <= 16)
            {
                Assert.Empty(fixture.Events("INVARIANT_BREACH"));
                Assert.Empty(fixture.OpenItems());
            }
            else
            {
                var breach = Assert.Single(fixture.Events("INVARIANT_BREACH"));
                Assert.StartsWith($"INVARIANT_BREACH kind=relaunch-without-activation record={fixture.Goals[0].Id.Value} ",
                    breach.GetProperty("detail").GetString());
                Assert.Equal("decision", breach.GetProperty("operator").GetString());
                Assert.Equal($"invariant-breach:relaunch-without-activation:{fixture.Goals[0].Id.Value}",
                    Assert.Single(fixture.OpenItems()).CorrelationKey);
                Assert.Single(JsonNode.Parse(File.ReadAllText(fixture.State))!["invariants"]!.AsObject());
            }
        }
    }

    private static void Handoff(Fixture fixture) => fixture.Writer.Append("loop-handoff", null,
        "LOOP_HANDOFF attempt=18 stagedSourceCommit=d367dc3f8 repositoryHead=d367dc3f8 runDir=run", fixture.Clock.Now);

    private static void Progress(Fixture fixture, DateTimeOffset at) => fixture.Writer.Append("gate-progress", null,
        "PHASE_PROGRESS goal=9c9f8266 phase=Tests elapsed_ms=30000 target=x child_pid=1 output_bytes=0 heartbeat=h", at);
}
