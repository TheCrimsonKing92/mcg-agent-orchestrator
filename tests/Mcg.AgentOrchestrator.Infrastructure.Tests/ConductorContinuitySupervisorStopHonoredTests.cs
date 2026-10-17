using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its files, clock and scripted process host.
public sealed class ConductorContinuitySupervisorStopHonoredTests
{
    [Xunit.Fact]
    public async Task StopFilePresent_NonZeroExit_DoesNotRespawn()
    {
        using var scenario = new SupervisorScenario(
            _ => new ConductorSupervisorProcessResult(1, 101),
            stopFilePresent: true);
        Assert.True(File.Exists(scenario.StopFilePath));

        var exitCode = await scenario.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Single(scenario.Host.Requests);
        Assert.Empty(scenario.Delays);
        var stopped = Assert.Single(scenario.Store.Events);
        Assert.Equal("stopped", stopped.Operation);
        Assert.Equal("completed", stopped.Status);
        Assert.DoesNotContain(scenario.Store.Events, evt => evt.Status == "unexpected");
        var line = Assert.Single(scenario.ConductEvents);
        Assert.Equal("loop-handoff", line.Kind);
        Assert.Null(line.GoalId);
        Assert.Equal(
            "SUPERVISOR_STOP_HONORED basis=stop-file-present exit=1 pid=101 attempt=1",
            line.Detail);
        Assert.StartsWith(line.Detail + " stdout=", stopped.Detail);
        Assert.True(File.Exists(scenario.StopFilePath));
    }

    [Xunit.Theory]
    [Xunit.InlineData("stop-file-during-sleep")]
    [Xunit.InlineData("stop-file")]
    public async Task StopReasonArtifact_NonZeroExit_DoesNotRespawn(string reason)
    {
        using var scenario = new SupervisorScenario(request =>
        {
            ConductorContinuityExitArtifact.Write(
                request.ExitArtifactPath,
                new ConductorContinuityExitArtifact(reason, 4, 0, RestartRequested: false));
            return new ConductorSupervisorProcessResult(1, 101);
        });
        Assert.False(File.Exists(scenario.StopFilePath));

        var exitCode = await scenario.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Single(scenario.Host.Requests);
        Assert.Empty(scenario.Delays);
        var stopped = Assert.Single(scenario.Store.Events);
        Assert.Equal("stopped", stopped.Operation);
        Assert.Equal("completed", stopped.Status);
        var line = Assert.Single(scenario.ConductEvents);
        Assert.Equal("loop-handoff", line.Kind);
        Assert.StartsWith($"SUPERVISOR_STOP_HONORED basis=exit-reason={reason} ", line.Detail);
        Assert.StartsWith(line.Detail + " stdout=", stopped.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("max-duration")]
    public async Task UnintendedExit_NoStopSignal_StillRespawns(string? reason)
    {
        using var scenario = new SupervisorScenario(request =>
        {
            if (reason is not null)
            {
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact(reason, 4, 0, RestartRequested: false));
            }
            return new ConductorSupervisorProcessResult(1, 101);
        });
        Assert.False(File.Exists(scenario.StopFilePath));

        var exitCode = await scenario.RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(2, scenario.Host.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], scenario.Delays);
        Assert.Equal(["unexpected", "completed"], scenario.Store.Events.Select(evt => evt.Status));
        Assert.DoesNotContain(scenario.ConductEvents, line =>
            line.Detail.StartsWith("SUPERVISOR_STOP_HONORED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void StopFileCheck_IoError_CountsAsNotPresent()
    {
        var parentFile = Path.Combine(Path.GetTempPath(), $"mcg-stop-parent-{Guid.NewGuid():N}");
        File.WriteAllText(parentFile, "parent is a file");
        try
        {
            string? basis = null;
            var exception = Record.Exception(() =>
            {
                basis = ConductorSupervisorStopDecision.Basis(
                    Path.Combine(parentFile, "child", ".conduct-stop"), null);
            });

            Assert.Null(exception);
            Assert.Null(basis);
        }
        finally
        {
            File.Delete(parentFile);
        }
    }

    private sealed class SupervisorScenario : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), $"mcg-stop-honored-{Guid.NewGuid():N}");
        private readonly ConductorContinuitySupervisor _supervisor;

        public SupervisorScenario(
            Func<ConductorSupervisorProcessRequest, ConductorSupervisorProcessResult> firstExit,
            bool stopFilePresent = false)
        {
            Directory.CreateDirectory(_root);
            StopFilePath = Path.Combine(_root, ".conduct-stop");
            if (stopFilePresent)
            {
                File.WriteAllText(StopFilePath, "operator stop");
            }
            Host = new ScriptedSupervisorProcessHost(firstExit, request =>
            {
                // A clean fallback makes the pre-change behavior finish with a second launch.
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 4, 0, RestartRequested: false));
                return new ConductorSupervisorProcessResult(0, 102);
            });
            var time = new ManualConductorTimeProviderForTests(
                new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            _supervisor = new ConductorContinuitySupervisor(
                Host,
                Store,
                time,
                (delay, _) =>
                {
                    Delays.Add(delay);
                    time.AdvanceForTests(delay);
                    return Task.CompletedTask;
                },
                appendConductEvent: (kind, goalId, detail) => ConductEvents.Add((kind, goalId, detail)),
                stopFilePath: StopFilePath);
        }

        public string StopFilePath { get; }
        public ScriptedSupervisorProcessHost Host { get; }
        public RecordingRunEventStore Store { get; } = new();
        public List<TimeSpan> Delays { get; } = [];
        public List<(string Kind, string? GoalId, string Detail)> ConductEvents { get; } = [];

        public Task<int> RunAsync() => _supervisor.RunAsync(
            ["conduct", "--loop", "--watch"],
            _root,
            Path.Combine(_root, "continuity"),
            "project-a",
            "tenant-a");

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedSupervisorProcessHost(
        params Func<ConductorSupervisorProcessRequest, ConductorSupervisorProcessResult>[] steps)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(request);
            return Task.FromResult(steps[Math.Min(index, steps.Length - 1)](request));
        }
    }

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];

        public Task<RunEventRecord> AppendAsync(
            RunEventAppend evt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(evt);
            return Task.FromResult(new RunEventRecord(
                Events.Count,
                evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType,
                evt.GoalId,
                evt.Operation,
                evt.Status,
                evt.Detail,
                evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(
            long afterSequence = 0,
            string? goalId = null,
            int maxCount = 500,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
