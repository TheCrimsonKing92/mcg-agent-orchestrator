using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Fixture = ConductorInvariantObservationReaderTests.Fixture;

// Parallel-safe: real files and SQLite are confined to unique fixture roots; clocks are injected.
public sealed class ConductorProgramHealthMonitorInvariantTests
{
    private static ConductorInvariantObservation Idle(Fixture fixture) => new(fixture.Clock.Now,
        [new(fixture.Goals[0].Id.Value, fixture.Clock.Now.AddMinutes(-11))], fixture.Clock.Now.AddMinutes(-11), []);

    [Fact]
    public void Evaluate_NullHeadAndRestart_RaisesOnceThenClearsAttention()
    {
        using var fixture = new Fixture();
        var observation = Idle(fixture);
        fixture.Monitor().Evaluate(fixture.Goals, null, (_, _) => observation);
        var breach = Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        var record = fixture.Goals[0].Id.Value;
        var key = $"invariant-breach:gate-idle-while-ready:{record}";
        Assert.Equal("decision", breach.GetProperty("operator").GetString());
        Assert.Equal(record, breach.GetProperty("goalId").GetString());
        Assert.Matches($"^INVARIANT_BREACH kind=gate-idle-while-ready record={record} since=\\S+ evidence=\\S+$",
            breach.GetProperty("detail").GetString());
        var item = Assert.Single(fixture.OpenItems());
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal(key, item.CorrelationKey);
        var state = File.ReadAllText(fixture.State);
        fixture.Monitor().Evaluate(fixture.Goals, null, (_, _) => observation);
        Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        Assert.Single(fixture.OpenItems());
        Assert.Equal(state, File.ReadAllText(fixture.State));

        var recovered = observation with { Now = observation.Now.AddMinutes(1), GateReadyGoals = [] };
        var monitor = fixture.Monitor();
        monitor.Evaluate(fixture.Goals, null, (_, _) => recovered);
        monitor.Evaluate(fixture.Goals, null, (_, _) => recovered);
        var clear = Assert.Single(fixture.InvariantEvents("INVARIANT_CLEARED"));
        Assert.Equal($"INVARIANT_CLEARED kind=gate-idle-while-ready record={record}", clear.GetProperty("detail").GetString());
        Assert.Equal("outcome", clear.GetProperty("operator").GetString());
        Assert.Empty(fixture.OpenItems());
        Assert.Empty(JsonNode.Parse(File.ReadAllText(fixture.State))!["invariants"]!.AsObject());
    }

    [Fact]
    public void Evaluate_FailedAppend_CommitsNoBreachOrAttentionAndRetriesOnce()
    {
        using var fixture = new Fixture();
        var drains = 0;
        var writer = new ConductEventLogWriter(fixture.Log, beforeRequiredEventDrain: () =>
        {
            drains++;
            throw new IOException("injected append failure");
        });
        var observation = Idle(fixture);
        fixture.Monitor(writer).Evaluate(fixture.Goals, null, (_, _) => observation);
        Assert.True(drains > 0);
        Assert.Empty(fixture.Events());
        Assert.Empty(fixture.OpenItems());
        Assert.False(File.Exists(fixture.State));
        fixture.Monitor().Evaluate(fixture.Goals, null, (_, _) => observation);
        fixture.Monitor().Evaluate(fixture.Goals, null, (_, _) => observation);
        Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        Assert.Single(fixture.OpenItems());
    }

    [Fact]
    public void Evaluate_ThrowingObservation_WritesNothingAndDoesNotEscape()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var exception = Record.Exception(() => fixture.Monitor().Evaluate(fixture.Goals, null, (_, _) =>
        {
            calls++;
            throw new IOException("injected observation failure");
        }));
        Assert.Null(exception);
        Assert.Equal(1, calls);
        Assert.False(File.Exists(fixture.State));
        Assert.Empty(fixture.Events());
        Assert.Empty(fixture.OpenItems());
    }

    [Fact]
    public void Evaluate_LegacyFourKeyState_PreservesLandingAndActiveKey()
    {
        using var fixture = new Fixture();
        var landing = fixture.Clock.Now.AddHours(-3);
        File.WriteAllText(fixture.State, new JsonObject
        {
            ["lastHead"] = "main", ["lastLandingAt"] = landing,
            ["observedSince"] = landing.AddHours(-1), ["activeKey"] = "no-landing|verified-goals"
        }.ToJsonString());
        Assert.False(JsonNode.Parse(File.ReadAllText(fixture.State))!.AsObject().ContainsKey("invariants"));
        fixture.Monitor().Evaluate(fixture.Goals, null, (_, _) => Idle(fixture));
        var state = JsonNode.Parse(File.ReadAllText(fixture.State))!;
        Assert.Equal(landing, state["lastLandingAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("no-landing|verified-goals", state["activeKey"]!.GetValue<string>());
        Assert.Single(state["invariants"]!.AsObject());
        fixture.Monitor().Evaluate(fixture.Goals, "main", (_, _) => Idle(fixture));
        state = JsonNode.Parse(File.ReadAllText(fixture.State))!;
        Assert.Equal(landing, state["lastLandingAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("no-landing|verified-goals", state["activeKey"]!.GetValue<string>());
    }

    [Fact]
    public void Evaluate_ConcurrentConditions_PreservesProgramHealthFlow()
    {
        using var fixture = new Fixture();
        using var baseline = new Fixture();
        var observation = Idle(fixture);
        var monitor = fixture.Monitor();
        var original = baseline.Monitor();
        monitor.Evaluate(fixture.Goals, "main", (_, now) => observation with { Now = now });
        original.Evaluate(baseline.Goals, "main");
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(120);
        baseline.Clock.Now = fixture.Clock.Now;
        monitor.Evaluate(fixture.Goals, "main", (_, now) => observation with { Now = now });
        original.Evaluate(baseline.Goals, "main");
        monitor.Evaluate(fixture.Goals, "landed", (_, now) => observation with { Now = now });
        original.Evaluate(baseline.Goals, "landed");
        var health = fixture.Events().Where(row => row.GetProperty("eventKind").GetString() == "program-health")
            .Select(row => row.GetProperty("detail").GetString()).ToArray();
        Assert.Equal(2, health.Length);
        Assert.StartsWith("PROGRAM_HEALTH_ALERT ", health[0]);
        Assert.StartsWith("PROGRAM_HEALTH_CLEARED ", health[1]);
        Assert.Equal(baseline.Events().Select(row => row.GetProperty("detail").GetString()), health);
        Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        Assert.Empty(fixture.InvariantEvents("INVARIANT_CLEARED"));
        Assert.Single(fixture.OpenItems());
    }

    [Fact]
    public void Evaluate_ReceiptReaderFails_StillEvaluatesInvariants()
    {
        using var fixture = new Fixture();
        var called = false;
        fixture.Monitor(receipts: _ => { called = true; throw new IOException("receipt read failed"); })
            .Evaluate(fixture.Goals, "main", (_, _) => Idle(fixture));
        Assert.True(called);
        Assert.Single(fixture.InvariantEvents("INVARIANT_BREACH"));
        Assert.Single(fixture.OpenItems());
    }
}
