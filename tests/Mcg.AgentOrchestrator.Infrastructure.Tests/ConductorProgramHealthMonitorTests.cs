using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every test instance owns unique state and event paths and an injected clock.
public sealed class ConductorProgramHealthMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "program-health-monitor", Guid.NewGuid().ToString("N"));
    private readonly OwnerConsoleTestClock _clock = new();
    private string State => Path.Combine(_root, ConductorProgramHealthMonitor.StateFileName);
    private string Log => Path.Combine(_root, "events.jsonl");
    private readonly (GoalId Id, GoalStatus Status)[] _goals = [(new("goal-a"), GoalStatus.Verified)];
    private ConductorProgramHealthMonitor Monitor(Func<GoalId, IReadOnlyList<ProgramHealthReceiptObservation>>? reader = null,
        Func<string?>? policy = null, ConductEventLogWriter? writer = null) =>
        new(State, policy ?? (() => null), writer ?? new ConductEventLogWriter(Log), _clock, reader ?? (_ => []));
    private JsonElement[] Events() => File.Exists(Log) ? File.ReadAllLines(Log).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray() : [];

    [Fact]
    public void OneHundredUnchangedTicksWriteOneAlert()
    {
        var monitor = Monitor();
        monitor.Evaluate(_goals, "main");
        Assert.True(File.Exists(State));
        Assert.Empty(Events());
        _clock.Now = _clock.Now.AddMinutes(120);
        for (var i = 0; i < 100; i++)
        {
            monitor.Evaluate(_goals, "main");
            _clock.Now = _clock.Now.AddMinutes(1);
        }
        var alert = Assert.Single(Events());
        Assert.StartsWith("PROGRAM_HEALTH_ALERT conditions=no-landing ", alert.GetProperty("detail").GetString());
        Assert.Equal("program-health", alert.GetProperty("eventKind").GetString());
        Assert.StartsWith("program-health:", alert.GetProperty("event_id").GetString());
        Assert.Equal("decision", alert.GetProperty("operator").GetString());
    }

    [Fact]
    public void RestartDeduplicatesAndDirectHeadChangeClearsOnce()
    {
        Monitor().Evaluate(_goals, "main");
        _clock.Now = _clock.Now.AddMinutes(120);
        Monitor().Evaluate(_goals, "main");
        var activeState = File.ReadAllText(State);
        Monitor().Evaluate(_goals, "main");
        Monitor().Evaluate(_goals, null);
        Assert.Single(Events());
        Assert.Equal(activeState, File.ReadAllText(State));
        Monitor().Evaluate(_goals, "direct-commit");
        Monitor().Evaluate(_goals, "direct-commit");
        var events = Events();
        Assert.Equal(2, events.Length);
        Assert.Equal("outcome", events[1].GetProperty("operator").GetString());
        Assert.Equal("PROGRAM_HEALTH_CLEARED previous=no-landing|verified-goals minutesSinceLanding=0 currentReceipts=0 verifiedGoals=1",
            events[1].GetProperty("detail").GetString());
        using var state = JsonDocument.Parse(File.ReadAllText(State));
        Assert.Equal("direct-commit", state.RootElement.GetProperty("lastHead").GetString());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("activeKey").ValueKind);
        Assert.Equal(_clock.Now, state.RootElement.GetProperty("lastLandingAt").GetDateTimeOffset());
    }

    [Fact]
    public void NullHeadDoesNotReadOrSeed()
    {
        Monitor(_ => throw new InvalidOperationException("must not read")).Evaluate(_goals, null);
        Assert.False(File.Exists(State));
        Assert.Empty(Events());
    }

    [Fact]
    public void ReceiptReadSetIncludesOnlyStatusesThatCanBeHeld()
    {
        var goals = Enum.GetValues<GoalStatus>().Select(status => (new GoalId(status.ToString()), status)).ToArray();
        var read = new List<string>();
        Monitor(id => { read.Add(id.Value); return []; }).Evaluate(goals, "main");
        Assert.Equal(new[] { "Verifying", "Verified", "WaitingForHuman", "Parked", "AcceptanceFailed" }.Order(), read.Order());
        Assert.Empty(Events());
    }

    [Fact]
    public void PolicyIsReadEveryTickAndOverridesHeldThreshold()
    {
        var policy = (string?)null;
        var reads = 0;
        var receipt = new ProgramHealthReceiptObservation("held", _clock.Now.AddMinutes(-10), "main", ["goal-a"]);
        var monitor = Monitor(_ => [receipt], () => { reads++; return policy; });
        monitor.Evaluate(_goals, "main");
        Assert.Empty(Events());
        policy = "{ \"programHealth\": { \"heldReceiptMinutes\": 10 } }";
        monitor.Evaluate(_goals, "main");
        Assert.Equal(2, reads);
        Assert.Contains("conditions=held-receipt topBlocker=held", Assert.Single(Events()).GetProperty("detail").GetString());
    }

    [Fact]
    public void ThrowingReceiptReaderWritesNoEventOrPendingHeadState()
    {
        var calls = 0;
        var exception = Record.Exception(() => Monitor(_ =>
        {
            calls++;
            throw new IOException("injected receipt read failure");
        }).Evaluate(_goals, "main"));
        Assert.Null(exception);
        Assert.Equal(1, calls);
        Assert.Empty(Events());
        Assert.False(File.Exists(State));
    }

    [Fact]
    public void FailedAppendDoesNotCommitActiveState()
    {
        Monitor().Evaluate(_goals, "main");
        var previous = File.ReadAllText(State);
        _clock.Now = _clock.Now.AddMinutes(120);
        var writer = new ConductEventLogWriter(Log, beforeRequiredEventDrain: () => throw new IOException("injected append failure"));
        Monitor(writer: writer).Evaluate(_goals, "main");
        Assert.Empty(Events());
        Assert.Equal(previous, File.ReadAllText(State));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("[]")]
    [InlineData("{\"lastHead\":\"main\",\"observedSince\":false}")]
    public void CorruptStateReseedsWithoutInventingLanding(string contents)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(State, contents);
        Monitor().Evaluate(_goals, "main");
        using var state = JsonDocument.Parse(File.ReadAllText(State));
        Assert.Equal(_clock.Now, state.RootElement.GetProperty("observedSince").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("lastLandingAt").ValueKind);
        Assert.Empty(Events());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
