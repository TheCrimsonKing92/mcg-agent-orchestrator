using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns unique state, event-log and ledger paths.
public sealed class ConductorHostHealthMonitorTests
{
    [Fact]
    public void TransitionsEmitOnceWithOperatorTagsAndSurviveRestart()
    {
        using var fixture = new Fixture();
        fixture.Append(60, 55, 70, 58, 62, 200, 210, 230);
        fixture.Monitor().Evaluate();
        fixture.Monitor().Evaluate(); // A new instance exercises the persisted state, not an in-memory flag.
        var degraded = Assert.Single(fixture.Events());
        Assert.Equal("host-health", degraded.GetProperty("eventKind").GetString());
        Assert.Equal("decision", degraded.GetProperty("operator").GetString());
        var detail = degraded.GetProperty("detail").GetString()!;
        Assert.StartsWith("HOST_HEALTH_DEGRADED ", detail);
        Assert.Contains("launch_ms=230 baseline_ms=55 ratio=4.182 paged_pool_mb=11800 consecutive=3", detail);
        fixture.Append(60);
        var monitor = fixture.Monitor();
        monitor.Evaluate();
        monitor.Evaluate();
        fixture.Monitor().Evaluate();
        var events = fixture.Events();
        Assert.Equal(2, events.Length);
        var recovered = events[1];
        Assert.Equal("outcome", recovered.GetProperty("operator").GetString());
        Assert.StartsWith("HOST_HEALTH_RECOVERED ", recovered.GetProperty("detail").GetString());
        fixture.Append(200, 210, 230);
        fixture.Monitor().Evaluate();
        fixture.Monitor().Evaluate();
        Assert.Equal(3, fixture.Events().Length);
    }

    [Fact]
    public void RestartAfterEventAppendBeforeStateCommitDoesNotDuplicateDecision()
    {
        using var fixture = new Fixture();
        fixture.Append(60, 55, 70, 58, 62, 200, 210, 230);
        var reachedAppend = 0;
        fixture.Monitor(afterEventAppend: () =>
        {
            reachedAppend++;
            throw new IOException("simulated failure before state commit");
        }).Evaluate();
        Assert.Equal(1, reachedAppend);
        Assert.Single(fixture.Events());
        Assert.Contains("pending", File.ReadAllText(fixture.State));
        fixture.Monitor().Evaluate();
        fixture.Monitor().Evaluate();
        Assert.Single(fixture.Events());
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.State));
        Assert.True(state.RootElement.GetProperty("isDegraded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("pending").ValueKind);
    }

    [Fact]
    public void FailedRequiredEventAppendRetriesSamePendingTransition()
    {
        using var fixture = new Fixture();
        fixture.Append(60, 55, 70, 58, 62, 200, 210, 230);
        var failing = true;
        var writer = new ConductEventLogWriter(fixture.Log, beforeRequiredEventDrain: () =>
        {
            if (failing) throw new IOException("injected event write failure");
        });
        var monitor = fixture.Monitor(writer);
        monitor.Evaluate();
        Assert.Empty(fixture.Events());
        var pendingState = File.ReadAllText(fixture.State);
        failing = false;
        monitor.Evaluate();
        fixture.Monitor().Evaluate();
        var emitted = Assert.Single(fixture.Events());
        using var pending = JsonDocument.Parse(pendingState);
        Assert.Equal("host-health:" + pending.RootElement.GetProperty("pending").GetProperty("id").GetString(),
            emitted.GetProperty("event_id").GetString());
    }

    [Fact]
    public void MissingLedgerOrMalformedStateCannotManufactureRecovery()
    {
        using var fixture = new Fixture();
        fixture.Append(60, 55, 70, 58, 62, 200, 210, 230);
        fixture.Monitor().Evaluate();
        File.Delete(fixture.Ledger);
        fixture.Monitor().Evaluate();
        Assert.Single(fixture.Events());
        fixture.Append(60);
        File.WriteAllText(fixture.Ledger, "malformed-ledger");
        fixture.Monitor().Evaluate();
        Assert.Single(fixture.Events());
        fixture.Append(60);
        File.WriteAllText(fixture.State, "malformed");
        fixture.Monitor().Evaluate();
        Assert.Single(fixture.Events());
    }

    [Fact]
    public void StateWriteFailurePreventsPublishingTransition()
    {
        using var fixture = new Fixture();
        fixture.Append(60, 55, 70, 58, 62, 200, 210, 230);
        var blockedParent = Path.Combine(fixture.Root, "blocked");
        File.WriteAllText(blockedParent, "file instead of directory");
        var monitor = new ConductorHostHealthMonitor(fixture.Ledger,
            Path.Combine(blockedParent, "state.json"), new ConductEventLogWriter(fixture.Log));
        monitor.Evaluate();
        Assert.Empty(fixture.Events());
        fixture.Monitor().Evaluate();
        Assert.Single(fixture.Events());
    }

    [Theory]
    [InlineData("host-health", "HOST_HEALTH_DEGRADED launch_ms=230", "decision")]
    [InlineData("host-health", "HOST_HEALTH_RECOVERED", "outcome")]
    [InlineData("host-health", "HOST_HEALTH_DEGRADED_EXTRA", null)]
    [InlineData("host-health", "prefix HOST_HEALTH_DEGRADED", null)]
    [InlineData("other", "HOST_HEALTH_DEGRADED", null)]
    public void ClassifierMatchesOnlyExactTransitionToken(string kind, string detail, string? expected) =>
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify(kind, detail));

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "host-health-monitor", Guid.NewGuid().ToString("N"));
        internal string Ledger => GateHostHealthLedger.ResolveStorePath(Root);
        internal string State => Path.Combine(Root, ".orchestrator", ConductorHostHealthMonitor.StateFileName);
        internal string Log => Path.Combine(Root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        internal void Append(params double[] launches)
        {
            foreach (var launch in launches)
                GateHostHealthLedger.Append(Ledger, new(DateTimeOffset.UnixEpoch, Guid.NewGuid().ToString("N"), launch, 11800));
        }
        internal ConductorHostHealthMonitor Monitor(ConductEventLogWriter? writer = null, Action? afterEventAppend = null) =>
            new(Ledger, State, writer ?? new ConductEventLogWriter(Log), afterEventAppend);
        internal JsonElement[] Events() => File.Exists(Log)
            ? File.ReadAllLines(Log).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray()
            : [];
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
