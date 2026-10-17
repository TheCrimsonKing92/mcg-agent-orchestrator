using System.Text.Json.Nodes;
using Fixture = ConductorInvariantSliceTwoWiringTests.Fixture;

// Parallel-safe: the workspace, real log, attempt stores, database and clock are fixture-owned.
public sealed class ConductorInvariantSliceTwoScenarioTests
{
    [Fact]
    public void Evaluate_WorkspaceIncidentTimeline_PersistsDedupAcrossSuccessor()
    {
        using var fixture = new Fixture();
        var finishedAt = fixture.Clock.Now;
        var attempt = fixture.Attempt();
        fixture.Save(attempt);
        Assert.Equal(attempt.AttemptId, Assert.Single(
            fixture.Reader.Read(fixture.Goals, finishedAt).FinishedAttempts!).AttemptId);
        for (var minute = 0; minute <= 5; minute++) fixture.Tick(finishedAt.AddMinutes(minute));
        Assert.Empty(fixture.Events("INVARIANT_BREACH"));
        fixture.Tick(finishedAt.AddMinutes(6));
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.Single(fixture.OpenItems());
        Assert.Single(JsonNode.Parse(File.ReadAllText(fixture.State))!["invariants"]!.AsObject());

        fixture.Restart(); // Reopen writer, store and monitor while an invariant is persisted.
        fixture.Tick(finishedAt.AddMinutes(7));
        Assert.Single(fixture.Events("INVARIANT_BREACH"));
        Assert.Single(fixture.OpenItems());

        var scheduledAt = DateTimeOffset.Parse("2026-10-16T22:39:00Z");
        fixture.Clock.Now = scheduledAt;
        fixture.Schedule();
        for (var tick = 0; tick <= 30; tick++)
        {
            fixture.Clock.Now = scheduledAt.AddSeconds(tick * 30);
            if (tick <= 10) fixture.Drain(0, tick);
            fixture.Tick(fixture.Clock.Now);
            Assert.Single(fixture.Events("INVARIANT_BREACH"));
        }
        fixture.Tick(scheduledAt.AddMinutes(16));
        var raised = fixture.Events("INVARIANT_BREACH");
        Assert.Equal(2, raised.Length);
        Assert.Equal(2, fixture.OpenItems().Count);
        Assert.Equal(2, fixture.Attention.ListAsync().GetAwaiter().GetResult().Count);
        Assert.Equal(new[] { "finished-unreconciled", "relaunch-without-activation" },
            raised.Select(row => row.GetProperty("detail").GetString()!.Split(' ')[1]["kind=".Length..]));
        Assert.All(raised, row => Assert.Equal("decision", row.GetProperty("operator").GetString()));

        fixture.Save(attempt with { ReconciledAt = fixture.Clock.Now });
        fixture.Append("ACTIVATION_ADOPTED adoptedCommit=main");
        fixture.Tick(scheduledAt.AddMinutes(17));
        Assert.Empty(fixture.OpenItems());
        var cleared = fixture.Events("INVARIANT_CLEARED");
        Assert.Equal(2, cleared.Length);
        Assert.Equal(new[] { $"INVARIANT_CLEARED kind=finished-unreconciled record={attempt.AttemptId}",
            $"INVARIANT_CLEARED kind=relaunch-without-activation record={fixture.Goals[0].Id.Value}" },
            cleared.Select(row => row.GetProperty("detail").GetString()));
        Assert.All(cleared, row => Assert.Equal("outcome", row.GetProperty("operator").GetString()));
        Assert.Empty(JsonNode.Parse(File.ReadAllText(fixture.State))!["invariants"]!.AsObject());
        fixture.Tick(scheduledAt.AddMinutes(18));
        Assert.Equal(2, fixture.Events("INVARIANT_BREACH").Length);
        Assert.Equal(2, fixture.Events("INVARIANT_CLEARED").Length);
        Assert.Equal(2, fixture.Attention.ListAsync().GetAwaiter().GetResult().Count);
        Assert.Empty(fixture.OpenItems());
    }
}
