using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temporary roots, injected readers and a fixed clock; no environment mutation.
public sealed class ConductorHostHealthSandboxConditionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("false")]
    public void OffThenOnEmitsEachTransitionOnce(string? rawValue)
    {
        using var fixture = new Fixture();
        fixture.Sandbox.Reading = WorkerSandboxReading.Available(false, rawValue);
        fixture.Evaluate(3);
        var off = Assert.Single(fixture.Events());
        Assert.Equal("HOST_HEALTH_WORKER_SANDBOX_OFF variable=MCG_WORKER_SANDBOX value=" +
            (rawValue ?? "unset") + " remedy=set MCG_WORKER_SANDBOX=1 at User scope, then restart the conductor", Detail(off));
        Assert.Equal("decision", off.GetProperty("operator").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow(), off.GetProperty("timestamp").GetDateTimeOffset());

        fixture.Sandbox.Reading = WorkerSandboxReading.Available(true, "TRUE");
        fixture.Evaluate(3);
        var events = fixture.Events();
        Assert.Equal(2, events.Length);
        Assert.Equal("HOST_HEALTH_WORKER_SANDBOX_ON", Detail(events[1]));
        Assert.Equal("outcome", events[1].GetProperty("operator").GetString());
        Assert.Equal(6, fixture.Sandbox.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableSandboxKeepsStateByteIdentical(bool active)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.State, active
            ? "{\"isDegraded\":false,\"pending\":null,\"workerSandboxOff\":true}"
            : LegacyState);
        var before = File.ReadAllBytes(fixture.State);
        fixture.Sandbox.Reading = WorkerSandboxReading.Unavailable("injected-error");
        fixture.Evaluate(3);
        Assert.Empty(fixture.Events());
        Assert.Equal(3, fixture.Sandbox.Calls);
        Assert.Equal(before, File.ReadAllBytes(fixture.State));
    }

    [Fact]
    public void NullReadersSkipActiveConditionsWithoutRecovery()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.State,
            "{\"isDegraded\":false,\"pending\":null,\"workerSandboxOff\":true,\"repositoryLowWritable\":true}");
        var before = File.ReadAllBytes(fixture.State);
        fixture.Signals = new(fixture.Foreground, fixture.Clock);
        fixture.Evaluate(3);
        Assert.Empty(fixture.Events());
        Assert.Equal(0, fixture.Sandbox.Calls);
        Assert.Equal(0, fixture.Repository.Calls);
        Assert.Equal(before, File.ReadAllBytes(fixture.State));
    }

    [Fact]
    public void LegacyStateDefaultsBothNewConditionsToFalse()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.State, LegacyState);
        var before = File.ReadAllBytes(fixture.State);
        fixture.Sandbox.Reading = WorkerSandboxReading.Available(true, "1");
        fixture.Evaluate(3);
        Assert.Empty(fixture.Events());
        Assert.Equal(before, File.ReadAllBytes(fixture.State));

        fixture.Sandbox.Reading = WorkerSandboxReading.Available(false, null);
        fixture.Repository.Reading = RepositoryIntegrityReading.Available(["."]);
        fixture.Evaluate(3);
        var events = fixture.Events();
        Assert.Equal(2, events.Length);
        Assert.StartsWith("HOST_HEALTH_WORKER_SANDBOX_OFF ", Detail(events[0]));
        Assert.StartsWith("HOST_HEALTH_REPOSITORY_LOW_WRITABLE ", Detail(events[1]));
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.State));
        foreach (var field in new[] { "isDegraded", "foregroundLockArmed", "pagedPoolHigh",
            "workerSandboxOff", "repositoryLowWritable" })
            Assert.True(state.RootElement.GetProperty(field).GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("pending").ValueKind);
    }

    [Theory]
    [InlineData(26100)]
    [InlineData(26200)]
    public void UnchangedOrUnsupportedForegroundDoesNotSkipNewConditions(int build)
    {
        using var fixture = new Fixture();
        fixture.Foreground.Reading = ForegroundLockReading.Available(0, build);
        fixture.Sandbox.Reading = WorkerSandboxReading.Available(false, null);
        fixture.Repository.Reading = RepositoryIntegrityReading.Available(["."]);
        fixture.Evaluate(3);
        Assert.Equal(2, fixture.Events().Length);
        Assert.Equal(3, fixture.Repository.Calls);
        Assert.Equal(3, fixture.Sandbox.Calls);
    }

    [Fact]
    public void AllFiveConditionsCommitInExistingOrderWithExactText()
    {
        using var fixture = new Fixture();
        foreach (var launch in new[] { 60, 55, 70, 58, 62, 200, 210, 230 })
            GateHostHealthLedger.Append(fixture.Ledger,
                new(fixture.Clock.GetUtcNow(), Guid.NewGuid().ToString("N"), launch, 11800));
        fixture.Foreground.Reading = ForegroundLockReading.Available(2147483647, 26200);
        fixture.Sandbox.Reading = WorkerSandboxReading.Available(false, null);
        fixture.Repository.Reading = RepositoryIntegrityReading.Available(["."]);
        fixture.Evaluate(1);
        var events = fixture.Events();
        Assert.Equal(new[]
        {
            "HOST_HEALTH_DEGRADED launch_ms=230 baseline_ms=55 ratio=4.182 paged_pool_mb=11800 consecutive=3",
            "HOST_HEALTH_PAGED_POOL_HIGH paged_pool_mb=11800 rise_mb=0 records=5 " +
                "remedy=.orchestrator/operator-tools/Set-ForegroundLockTimeout.ps1 (run from a focused console); " +
                "leaked pool is reclaimed only by a reboot",
            "HOST_HEALTH_FOREGROUND_LOCK_ARMED timeout_ms=2147483647 build=26200 " +
                "remedy=.orchestrator/operator-tools/Set-ForegroundLockTimeout.ps1 (run from a focused console)",
            "HOST_HEALTH_WORKER_SANDBOX_OFF variable=MCG_WORKER_SANDBOX value=unset " +
                "remedy=set MCG_WORKER_SANDBOX=1 at User scope, then restart the conductor",
            "HOST_HEALTH_REPOSITORY_LOW_WRITABLE paths=. remedy=report-only; relabeling needs owner approval"
        }, events.Select(Detail).ToArray());
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.State));
        foreach (var field in new[] { "isDegraded", "foregroundLockArmed", "pagedPoolHigh",
            "workerSandboxOff", "repositoryLowWritable" })
            Assert.True(state.RootElement.GetProperty(field).GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("pending").ValueKind);
        Assert.Equal(1, fixture.Sandbox.Calls);
        Assert.Equal(1, fixture.Repository.Calls);

        var committedState = File.ReadAllBytes(fixture.State);
        fixture.Evaluate(2);
        Assert.Equal(events.Select(record => record.GetRawText()),
            fixture.Events().Select(record => record.GetRawText()));
        Assert.Equal(committedState, File.ReadAllBytes(fixture.State));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedPublishReplaysNewConditionsOnce(bool repository, bool afterAppend)
    {
        using var fixture = new Fixture();
        if (repository) fixture.Repository.Reading = RepositoryIntegrityReading.Available(["."]);
        else fixture.Sandbox.Reading = WorkerSandboxReading.Available(false, null);
        var fail = true;
        var writer = new ConductEventLogWriter(fixture.Log, beforeRequiredEventDrain: () =>
        {
            if (fail && !afterAppend) throw new IOException("injected event write failure");
        });
        fixture.Monitor(writer, () =>
        {
            if (fail && afterAppend) throw new IOException("injected failure before state commit");
        }).Evaluate();
        if (afterAppend) Assert.Single(fixture.Events());
        else Assert.Empty(fixture.Events());
        using var pending = JsonDocument.Parse(File.ReadAllText(fixture.State));
        var transition = pending.RootElement.GetProperty("pending");
        Assert.Equal(repository ? "repository-integrity" : "worker-sandbox", transition.GetProperty("condition").GetString());
        Assert.Equal(fixture.Clock.GetUtcNow(), transition.GetProperty("observedAt").GetDateTimeOffset());
        Assert.False(pending.RootElement.GetProperty(repository ? "repositoryLowWritable" : "workerSandboxOff").GetBoolean());
        var eventId = "host-health:" + transition.GetProperty("id").GetString();
        fail = false;
        fixture.Evaluate(3);
        Assert.Equal(eventId, Assert.Single(fixture.Events()).GetProperty("event_id").GetString());
        using var committed = JsonDocument.Parse(File.ReadAllText(fixture.State));
        Assert.Equal(JsonValueKind.Null, committed.RootElement.GetProperty("pending").ValueKind);
        Assert.True(committed.RootElement.GetProperty(repository ? "repositoryLowWritable" : "workerSandboxOff").GetBoolean());
    }

    private const string LegacyState =
        "{\"isDegraded\":true,\"pending\":null,\"foregroundLockArmed\":true,\"pagedPoolHigh\":true}";
    private static string Detail(JsonElement record) => record.GetProperty("detail").GetString()!;

    private sealed class FakeSandboxReader : IWorkerSandboxReader
    {
        internal WorkerSandboxReading Reading { get; set; } = WorkerSandboxReading.Unavailable("not-configured");
        internal int Calls { get; private set; }
        public WorkerSandboxReading Read() { Calls++; return Reading; }
    }

    private sealed class FakeRepositoryProbe : IRepositoryIntegrityProbe
    {
        internal RepositoryIntegrityReading Reading { get; set; } = RepositoryIntegrityReading.Available([]);
        internal int Calls { get; private set; }
        public RepositoryIntegrityReading Read() { Calls++; return Reading; }
    }

    private sealed class FakeForegroundReader : IForegroundLockReader
    {
        internal ForegroundLockReading Reading { get; set; } = ForegroundLockReading.Unavailable("not-configured");
        public ForegroundLockReading Read() => Reading;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(123);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "host-health-sandbox", Guid.NewGuid().ToString("N"));
        internal string Ledger => Path.Combine(Root, "host-health.jsonl");
        internal string State => Path.Combine(Root, ConductorHostHealthMonitor.StateFileName);
        internal string Log => Path.Combine(Root, "events.log");
        internal FakeSandboxReader Sandbox { get; } = new();
        internal FakeRepositoryProbe Repository { get; } = new();
        internal FakeForegroundReader Foreground { get; } = new();
        internal FixedClock Clock { get; } = new();
        internal ConductorHostHealthSignals Signals { get; set; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Signals = new(Foreground, Clock, Sandbox, Repository);
        }
        internal ConductorHostHealthMonitor Monitor(ConductEventLogWriter? writer = null, Action? afterAppend = null) =>
            new(Ledger, State, writer ?? new ConductEventLogWriter(Log), afterAppend, Signals);
        internal void Evaluate(int count)
        {
            for (var index = 0; index < count; index++) Monitor().Evaluate();
        }
        internal JsonElement[] Events() => File.Exists(Log)
            ? File.ReadAllLines(Log).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray() : [];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
