using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorContinuitySupervisorHandoffTests
{
    [Xunit.Fact]
    public async Task AdoptedBuildTransfersSupervisorRoleBeforeNextChildLaunch()
    {
        var fixture = new SupervisorHandoffFixture();

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Equal(["dotnet-test", fixture.BuildB.AppDllPath], fixture.Seam.Launches.Single().CommandPrefix);
        Assert.Equal(fixture.Seam.Successor, fixture.Seam.Owner);
        Assert.Contains(fixture.Events.Events, evt => evt.Operation == "supervisor-handoff" &&
            evt.Status == "completed" && evt.Detail.Contains("SUPERVISOR_HANDOFF", StringComparison.Ordinal));
        Assert.Contains(fixture.ConductEvents, evt => evt.StartsWith("SUPERVISOR_BUILD ", StringComparison.Ordinal) &&
            evt.Contains("\"commitSha\":\"commit-a\"", StringComparison.Ordinal) &&
            evt.Contains("\"runDir\":\"C:\\\\run-a\"", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task SameRunDirectoryDoesNotLaunchAnotherSupervisor()
    {
        var fixture = new SupervisorHandoffFixture(ownRunDirectory: "C:\\run-b");

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Empty(fixture.Seam.Launches);
        Assert.Equal(3, fixture.Host.Requests.Count);
    }

    [Xunit.Fact]
    public async Task AdoptedChildRemainsRunningWhileSupervisorLeaseTransfers()
    {
        var fixture = new SupervisorHandoffFixture(holdAdoptedChild: true);
        var run = fixture.RunAsync();

        await fixture.Host.AdoptedChildStarted;
        await fixture.Seam.TransferObserved;
        Assert.Equal(fixture.Seam.Successor, fixture.Seam.Owner);
        Assert.False(run.IsCompleted);
        Assert.Equal(2, fixture.Host.Requests.Count);

        fixture.Host.ReleaseAdoptedChild();
        Assert.Equal(0, await run);
    }
}

internal sealed class SupervisorHandoffFixture
{
    internal readonly ConductorPreparedSuccessor BuildB = new(
        "C:\\run-b", "C:\\run-b\\Mcg.AgentOrchestrator.App.dll",
        "commit-b", "commit-b", "self-check=true");
    private readonly AdvancingClock _clock = new();
    private readonly string _ownRunDirectory;
    private int _stageCalls;

    internal SupervisorHandoffFixture(string ownRunDirectory = "C:\\run-a", int stopAtIndex = 2,
        bool holdAdoptedChild = false)
    {
        _ownRunDirectory = ownRunDirectory;
        Seam = new FakeHandoffSeam(new ConductorSupervisorBuildIdentity("commit-a", ownRunDirectory));
        Host = new ScriptedHost(stopAtIndex, holdAdoptedChild);
    }

    internal FakeHandoffSeam Seam { get; }
    internal ScriptedHost Host { get; }
    internal RecordingEvents Events { get; } = new();
    internal List<string> ConductEvents { get; } = [];

    internal Task<int> RunAsync()
    {
        var supervisor = new ConductorContinuitySupervisor(Host, Events,
            timeProvider: _clock,
            delay: (duration, _) => { _clock.Advance(duration); return Task.CompletedTask; },
            stageSuccessor: _ => ++_stageCalls == 1 ? BuildB : BuildAfterB(_stageCalls),
            appendConductEvent: (_, _, detail) => ConductEvents.Add(detail),
            dotnetPath: "dotnet-test",
            supervisorHandoff: new ConductorSupervisorHandoffOptions(Seam,
                new ConductorSupervisorBuildIdentity("commit-a", _ownRunDirectory),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        return supervisor.RunAsync(["conduct", "--loop", "--max-duration", "60"],
            "C:\\repo", Path.Combine(Path.GetTempPath(), $"mcg-handoff-{Guid.NewGuid():N}"),
            "default", "default");
    }

    private static ConductorPreparedSuccessor BuildAfterB(int stageNumber)
    {
        var letter = (char)('a' + stageNumber);
        var runDirectory = $"C:\\run-{letter}";
        return new ConductorPreparedSuccessor(runDirectory,
            Path.Combine(runDirectory, "Mcg.AgentOrchestrator.App.dll"),
            $"commit-{letter}", $"commit-{letter}", "self-check=true");
    }

    internal sealed class ScriptedHost(int stopAtIndex, bool holdAdoptedChild) : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        private readonly TaskCompletionSource _adoptedChildStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAdoptedChild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task AdoptedChildStarted => _adoptedChildStarted.Task;
        internal void ReleaseAdoptedChild() => _releaseAdoptedChild.TrySetResult();

        public async Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(request);
            request.OnProcessStarted?.Invoke(500 + index);
            if (index > 0)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                for (var tick = 1; tick <= 3; tick++)
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            }
            if (holdAdoptedChild && index == 1)
            {
                _adoptedChildStarted.TrySetResult();
                await _releaseAdoptedChild.Task.WaitAsync(cancellationToken);
            }
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact(index == stopAtIndex ? "stop-file" : "max-duration",
                    3, 1, RestartRequested: index != stopAtIndex));
            return new ConductorSupervisorProcessResult(0, 500 + index);
        }
    }

    internal sealed class AdvancingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    internal sealed class FakeHandoffSeam(ConductorSupervisorBuildIdentity ownBuild)
        : IConductorSupervisorHandoffSeam
    {
        internal readonly ConductorSupervisorProcessIdentity Successor =
            new(900, new DateTimeOffset(2026, 9, 26, 0, 1, 0, TimeSpan.Zero));
        private readonly TaskCompletionSource _transferObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task TransferObserved => _transferObserved.Task;
        private ConductorSupervisorHandoffRecord? _record;
        internal bool ThrowOnLaunch { get; set; }
        internal bool SuppressReadiness { get; set; }
        internal List<ConductLoopLaunchRequest> Launches { get; } = [];
        internal ConductorSupervisorProcessIdentity? Owner { get; private set; }
        public string? IncomingRecordPath { get; set; }
        public ConductorSupervisorProcessIdentity Self { get; } =
            new(800, new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));

        public void Acquire(ConductorSupervisorBuildIdentity build)
        {
            Assert.Equal(ownBuild, build);
            Owner = Self;
        }
        public bool IsOwner(ConductorSupervisorProcessIdentity process) => Owner == process;
        public void Transfer(ConductorSupervisorProcessIdentity from,
            ConductorSupervisorProcessIdentity to, ConductorSupervisorBuildIdentity build)
        {
            Assert.Equal(Self, from);
            Assert.Equal(Successor, to);
            Owner = to;
            _transferObserved.TrySetResult();
        }
        public string WriteRecord(ConductorSupervisorHandoffRecord record)
        {
            _record = record;
            return "record.json";
        }
        public ConductorSupervisorHandoffRecord ReadRecord(string path) =>
            _record ?? throw new InvalidOperationException("No handoff record.");
        public void WriteReady(string path, ConductorSupervisorReadyRecord ready) { }
        public ConductorSupervisorReadyRecord? ReadReady(string path) =>
            SuppressReadiness || _record is null ? null :
                new ConductorSupervisorReadyRecord(_record.Token, Successor,
                    new ConductorSupervisorBuildIdentity(_record.AdoptedBuild.CommitSha,
                        _record.AdoptedBuild.RunDirectory));
        public ConductLoopLaunchResult Launch(ConductLoopLaunchRequest request)
        {
            Launches.Add(request);
            if (ThrowOnLaunch) throw new InvalidOperationException("launch failed");
            return new ConductLoopLaunchResult(Successor.ProcessId, request.StdoutPath, request.StderrPath);
        }
        public bool IsAlive(ConductorSupervisorProcessIdentity process) => true;
        public bool IsRunning(int processId) => true;
        public ConductorSupervisorProcessIdentity Identify(int processId) =>
            new(processId, new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        public Task<ConductorSupervisorProcessResult> ObserveChildAsync(
            ConductorSupervisorActiveChild child, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No active child in this fixture.");
        public void StopPending(int processId) { }
    }

    internal sealed class RecordingEvents : IRunEventStore
    {
        internal List<RunEventAppend> Events { get; } = [];
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt,
            CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.FromResult(new RunEventRecord(Events.Count,
                evt.EventId ?? Guid.NewGuid().ToString("N"), evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
