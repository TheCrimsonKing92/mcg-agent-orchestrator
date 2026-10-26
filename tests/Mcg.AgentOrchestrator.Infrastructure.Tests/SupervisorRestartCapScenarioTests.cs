using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class SupervisorRestartCapScenarioTests
{
    [Xunit.Fact]
    public async Task FourRelaunchHandoffsInsideTheWindowNeverEscalate()
    {
        using var fixture = new Fixture(children: 5);
        fixture.Seam.FalseSelfOnTransfers.UnionWith([3, 4]);

        var exit = await fixture.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
        Assert.Equal(5, fixture.Host.Requests.Count);
        Assert.Equal(1, fixture.Host.MaxInFlight);
        Assert.InRange(fixture.Clock.Elapsed.TotalSeconds, 0, 599);
        Assert.Equal(["C:\\run-b", "C:\\run-c", "C:\\run-d", "C:\\run-e"],
            fixture.Host.Requests.Skip(1).Select(request => Path.GetDirectoryName(request.CommandPrefix![1])));
        var failures = fixture.Events.Items.Where(e => e.Operation == "supervisor-handoff" && e.Status == "failed").ToArray();
        Assert.Equal(4, failures.Length);
        Assert.All(failures, e =>
        {
            Assert.Contains("IOException", e.Detail);
            Assert.Contains("\"phase\":\"transfer\"", e.Detail);
            Assert.Contains("\"hresult\":-2147024864", e.Detail);
        });
        Assert.Equal(2, failures.Count(e => e.Detail!.Contains("\"leaseOwner\":\"self\"", StringComparison.Ordinal)));
        Assert.Equal(2, failures.Count(e => e.Detail!.Contains("\"leaseOwner\":\"unknown\"", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.Events.Items,
            e => e.Operation == "restart" && e.Status is "unexpected" or "escalated");
        Assert.DoesNotContain(fixture.ConductEvents, IsRestartEscalation);
        Assert.Empty(fixture.Attention);
    }

    [Xunit.Fact]
    public async Task FourCrashesInsideTheWindowEscalateWithEachFailure()
    {
        using var fixture = new Fixture(children: 4, crashes: 4);

        Assert.Equal(1, await fixture.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken));

        Assert.Equal(4, fixture.Host.Requests.Count);
        var line = Assert.Single(fixture.ConductEvents);
        Assert.StartsWith("SUPERVISOR_ESCALATED reason=restart-cap count=4 max=3 windowSeconds=600 starts=[", line);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Contains($"attempt={attempt} pid={499 + attempt} exit=1", line);
            Assert.Contains($"fixture crash {attempt}", line);
        }
        var escalated = Assert.Single(fixture.Events.Items, e => e.Operation == "restart" && e.Status == "escalated");
        Assert.StartsWith(line, escalated.Detail);
        var attention = Assert.Single(fixture.Attention);
        Assert.Equal("Continuity supervisor escalated: restart-cap", attention.Title);
        Assert.Contains(line, attention.Body);
        Assert.Contains("No running conductor is supervised.", attention.Body);
        Assert.StartsWith("conductor-supervisor-escalated:restart-cap:", attention.Key);
    }

    [Xunit.Fact]
    public async Task ThreeCrashesThenAFailedSupervisionHandoffDoNotEscalate()
    {
        using var fixture = new Fixture(children: 5, crashes: 3);
        fixture.Seam.FalseSelfOnTransfers.Add(1);

        Assert.Equal(0, await fixture.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken));

        Assert.Equal(5, fixture.Host.Requests.Count);
        Assert.Equal(1, fixture.Host.MaxInFlight);
        Assert.Equal(3, fixture.Events.Items.Count(e => e.Operation == "restart" && e.Status == "unexpected"));
        var failure = Assert.Single(fixture.Events.Items, e => e.Operation == "supervisor-handoff" && e.Status == "failed");
        Assert.Contains("\"leaseOwner\":\"unknown\"", failure.Detail);
        Assert.DoesNotContain(fixture.ConductEvents, IsRestartEscalation);
        Assert.Empty(fixture.Attention);
    }

    [Xunit.Fact]
    public async Task TransferThatCommittedBeforeThrowingKeepsSuccessorRunning()
    {
        using var fixture = new Fixture(children: 2);
        fixture.Seam.CommitBeforeThrow = true;
        fixture.Seam.AutoReleaseChild = false;
        var run = fixture.RunAsync();
        try
        {
            await fixture.Seam.TransferObserved.Task.WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken);
            fixture.Host.Release(1);
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken));

            Assert.Empty(fixture.Seam.Stopped);
            Assert.Equal(fixture.Seam.Successor, fixture.Seam.Owner);
            Assert.Equal(2, fixture.Host.Requests.Count);
            var completed = Assert.Single(fixture.Events.Items, e => e.Operation == "supervisor-handoff" && e.Status == "completed");
            Assert.Contains("\"recoveredFrom\":\"IOException\"", completed.Detail);
            Assert.Contains("\"leaseOwner\":\"successor\"", completed.Detail);
            Assert.DoesNotContain(fixture.Events.Items, e => e.Operation == "restart" && e.Status == "unexpected");
        }
        finally { fixture.Host.Release(1); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task InboundAbandonmentDistinguishesIncumbentExitFromDeadline(bool incumbentAlive)
    {
        using var fixture = new Fixture(children: 0);
        fixture.Seam.PrepareInbound(incumbentAlive);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken));

        var abandoned = Assert.Single(fixture.Events.Items, e => e.Operation == "supervisor-handoff" && e.Status == "abandoned");
        Assert.Contains(incumbentAlive ? "deadline timeoutSeconds=5" : "incumbent-exited pid=700", abandoned.Detail);
        if (!incumbentAlive) Assert.DoesNotContain("deadline", abandoned.Detail);
        Assert.Empty(fixture.Host.Requests);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, "self")]
    [Xunit.InlineData(true, "unknown")]
    public async Task ChildIdentityFailure_RecordsDiagnosticsAndKeepsWatching(
        bool ownerProbeThrows, string expectedLeaseOwner)
    {
        // Parallel-safe: the host, lease seam, clock and temporary root belong to this fixture.
        using var fixture = new Fixture(children: 2);
        var error = new IOException("fixture child identity failure", unchecked((int)0x80070020));
        fixture.Seam.ChildIdentityFailure = error;
        fixture.Seam.OwnerProbeThrowsAfterIdentityFailure = ownerProbeThrows;

        Assert.Equal(0, await fixture.RunAsync().WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken));

        Assert.Equal(1, fixture.Seam.ChildIdentityFailures);
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Equal(1, fixture.Host.MaxInFlight);
        Assert.False(fixture.Seam.TransferObserved.Task.IsCompleted);
        Assert.Empty(fixture.Seam.Stopped);
        var failure = Assert.Single(fixture.Events.Items,
            e => e.Operation == "supervisor-handoff" && e.Status == "failed");
        using var payload = System.Text.Json.JsonDocument.Parse(failure.PayloadJson!);
        var detail = payload.RootElement;
        Assert.Equal("identity", detail.GetProperty("phase").GetString());
        Assert.Equal(nameof(IOException), detail.GetProperty("exceptionType").GetString());
        Assert.Equal(error.Message, detail.GetProperty("message").GetString());
        Assert.Equal(error.HResult, detail.GetProperty("hresult").GetInt32());
        Assert.Equal(expectedLeaseOwner, detail.GetProperty("leaseOwner").GetString());
        Assert.Equal($"child-identity:IOException:{error.Message}", detail.GetProperty("reason").GetString());
        Assert.Equal(1, detail.GetProperty("consecutiveFailures").GetInt32());
        Assert.Contains($"SUPERVISOR_HANDOFF_FAILED {failure.PayloadJson}", fixture.ConductEvents);
        Assert.DoesNotContain(fixture.Events.Items,
            e => e.Operation == "restart" && e.Status is "unexpected" or "escalated");
        Assert.DoesNotContain(fixture.ConductEvents, IsRestartEscalation);
        Assert.Empty(fixture.Attention);
    }

    private static bool IsRestartEscalation(string line) => line.StartsWith("SUPERVISOR_ESCALATED ", StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        private int _stage;
        private readonly bool _handoffEnabled;
        internal Clock Clock { get; } = new();
        internal ScriptedHost Host { get; }
        internal FakeSeam Seam { get; }
        internal RecordingEvents Events { get; } = new();
        internal List<string> ConductEvents { get; } = [];
        internal List<(string Title, string Body, string Key)> Attention { get; } = [];

        internal Fixture(int children, int crashes = 0)
        {
            Directory.CreateDirectory(_root);
            Host = new ScriptedHost(children, crashes);
            Seam = new FakeSeam(Host);
            _handoffEnabled = children != crashes || children == 0;
        }

        internal Task<int> RunAsync()
        {
            var supervisor = new ConductorContinuitySupervisor(Host, Events, Clock,
                delay: (duration, _) => { Clock.Advance(duration); return Task.CompletedTask; },
                stageSuccessor: _ =>
                {
                    var letter = (char)('a' + ++_stage);
                    var directory = $"C:\\run-{letter}";
                    return new ConductorPreparedSuccessor(directory,
                        Path.Combine(directory, "Mcg.AgentOrchestrator.App.dll"),
                        $"commit-{letter}", $"commit-{letter}", "self-check=true");
                },
                inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
                readinessTimeout: TimeSpan.FromSeconds(5),
                activationHealthyTicks: 3,
                appendConductEvent: (_, _, detail) => ConductEvents.Add(detail),
                supervisorHandoff: _handoffEnabled ? new ConductorSupervisorHandoffOptions(Seam,
                    new ConductorSupervisorBuildIdentity("commit-a", "C:\\run-a"),
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)) : null,
                raiseEscalationAttention: (title, body, key) => Attention.Add((title, body, key)));
            return supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", _root, "fixture", "fixture",
                cancellationToken: Xunit.TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Host.ReleaseAll();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScriptedHost(int children, int crashes) : IConductorSupervisorProcessHost
    {
        private readonly TaskCompletionSource[] _releases = Enumerable.Range(0, children)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int _inFlight;
        internal int MaxInFlight { get; private set; }
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        internal void Release(int index) => _releases[index].TrySetResult();
        internal void ReleaseAll() { foreach (var signal in _releases) signal.TrySetResult(); }

        public async Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref _inFlight));
            try
            {
                if (index >= children) throw new InvalidOperationException("Unexpected extra child launch.");
                request.OnProcessStarted?.Invoke(500 + index);
                if (index < crashes)
                {
                    File.WriteAllText(request.StderrPath, $"fixture crash {index + 1}\n\n");
                    return new ConductorSupervisorProcessResult(1, 500 + index);
                }
                if (index > crashes)
                {
                    request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    for (var tick = 1; tick <= 3; tick++)
                        request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                    // Hang detector only: child completion is driven by the fake transfer, never elapsed time.
                    await _releases[index].Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
                var stopped = index == children - 1;
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact(stopped ? "stop-file" : "self-relaunch-handoff",
                        3, 0, RestartRequested: !stopped, LandedGoals: 1));
                return new ConductorSupervisorProcessResult(0, 500 + index);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private sealed class FakeSeam(ScriptedHost host) : IConductorSupervisorHandoffSeam
    {
        private ConductorSupervisorHandoffRecord? _record;
        private bool _falseSelfOnce;
        private bool _ready;
        private bool _incumbentAlive;
        private int _transfers;
        internal HashSet<int> FalseSelfOnTransfers { get; } = [];
        internal bool CommitBeforeThrow { get; set; }
        internal bool AutoReleaseChild { get; set; } = true;
        internal Exception? ChildIdentityFailure { get; set; }
        internal bool OwnerProbeThrowsAfterIdentityFailure { get; set; }
        internal int ChildIdentityFailures { get; private set; }
        internal TaskCompletionSource TransferObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<ConductorSupervisorProcessIdentity> Stopped { get; } = [];
        internal ConductorSupervisorProcessIdentity? Owner { get; private set; }
        internal ConductorSupervisorProcessIdentity Successor { get; } = new(900,
            new DateTimeOffset(2026, 10, 25, 0, 1, 0, TimeSpan.Zero));
        public ConductorSupervisorProcessIdentity Self { get; } = new(800,
            new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        public string? IncomingRecordPath { get; private set; }
        public void Acquire(ConductorSupervisorBuildIdentity build) => Owner = Self;
        public bool IsOwner(ConductorSupervisorProcessIdentity identity)
        {
            if (OwnerProbeThrowsAfterIdentityFailure && ChildIdentityFailures > 0)
                throw new IOException("fixture lease unreadable");
            if (identity == Self && _falseSelfOnce)
            {
                _falseSelfOnce = false;
                return false;
            }
            return Owner == identity;
        }
        public void Transfer(ConductorSupervisorProcessIdentity from,
            ConductorSupervisorProcessIdentity to, ConductorSupervisorBuildIdentity build)
        {
            Assert.Equal(Self, from);
            Assert.Equal(Successor, to);
            _transfers++;
            _falseSelfOnce = FalseSelfOnTransfers.Contains(_transfers);
            if (CommitBeforeThrow) Owner = to;
            TransferObserved.TrySetResult();
            if (AutoReleaseChild) host.Release(_record!.ActiveChild!.Attempt - 1);
            throw new IOException("fixture lease sharing violation", unchecked((int)0x80070020));
        }
        public string WriteRecord(ConductorSupervisorHandoffRecord record)
        {
            _record = record;
            return "outbound.json";
        }
        public ConductorSupervisorHandoffRecord ReadRecord(string path) => _record!;
        public void WriteReady(string path, ConductorSupervisorReadyRecord ready) => _ready = true;
        public ConductorSupervisorReadyRecord? ReadReady(string path) => new(_record!.Token, Successor,
            new ConductorSupervisorBuildIdentity(_record.AdoptedBuild.CommitSha, _record.AdoptedBuild.RunDirectory));
        public ConductLoopLaunchResult Launch(ConductLoopLaunchRequest request) =>
            new(Successor.ProcessId, request.StdoutPath, request.StderrPath, StartedAt: Successor.StartedAt);
        public bool IsAlive(ConductorSupervisorProcessIdentity identity) =>
            identity.ProcessId != 700 || !_ready || _incumbentAlive;
        public bool IsRunning(int processId) => true;
        public ConductorSupervisorProcessIdentity Identify(int processId)
        {
            if (ChildIdentityFailure is { } error)
            {
                ChildIdentityFailures++;
                host.Release(processId - 500);
                throw error;
            }
            return new(processId, Self.StartedAt);
        }
        public Task<ConductorSupervisorProcessResult> ObserveChildAsync(
            ConductorSupervisorActiveChild child, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This scenario launches its children through the host.");
        public void StopPending(ConductorSupervisorProcessIdentity identity) => Stopped.Add(identity);

        internal void PrepareInbound(bool incumbentAlive)
        {
            IncomingRecordPath = "inbound.json";
            _incumbentAlive = incumbentAlive;
            Owner = new(700, Self.StartedAt.AddMinutes(-1));
            _record = new ConductorSupervisorHandoffRecord("inbound-token", Owner,
                new ConductorSupervisorBuildIdentity("commit-prior", "C:\\run-prior"),
                new ConductorSupervisorBuildSnapshot("commit-a", "C:\\run-a", "C:\\run-a\\Mcg.AgentOrchestrator.App.dll"),
                null, 0, null, 0, "ready.json");
        }
    }

    private sealed class Clock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        internal TimeSpan Elapsed => _now - Start;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        internal List<RunEventAppend> Items { get; } = [];
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Items.Add(evt);
            return Task.FromResult(new RunEventRecord(Items.Count,
                evt.EventId ?? Guid.NewGuid().ToString("N"), evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
