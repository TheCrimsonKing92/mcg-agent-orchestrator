using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted hosts, injected time, and unique artifact directories; no real processes.
public sealed class ConductorGenerationTerminationRecorderTests
{
    internal static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-10-19T08:18:07Z");
    internal static readonly DateTimeOffset TerminatedAt = DateTimeOffset.Parse("2026-10-19T08:37:23Z");

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Revert_ConfirmedForcedStop_RecordsOrderedPayloadBeforeRestore()
    {
        using var root = new ArtifactRoot();
        var events = new RecordingEvents();
        var beforeRestore = false;
        var result = await RunRevertAsync(events, new FixedClock(), root.Path,
            beforeRestore: () => beforeRestore = events.Events.Any(evt => evt.Operation == "generation-terminated"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, result.Launches);
        Assert.True(beforeRestore);
        var terminated = Assert.Single(events.Events.Where(evt => evt.Operation == "generation-terminated"));
        Assert.Equal(RunEventTypes.ConductorSupervision, terminated.EventType);
        Assert.Null(terminated.GoalId);
        Assert.Equal("forced-stop", terminated.Status);
        using var payload = JsonDocument.Parse(terminated.PayloadJson!);
        Assert.Equal(new[] { "processId", "processStartedAt", "terminatedAt", "cause", "reason", "commitSha" },
            payload.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(4242, payload.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(StartedAt.ToString("O"), payload.RootElement.GetProperty("processStartedAt").GetString());
        Assert.Equal(TerminatedAt.ToString("O"), payload.RootElement.GetProperty("terminatedAt").GetString());
        Assert.Equal("activation-reverted", payload.RootElement.GetProperty("cause").GetString());
        Assert.Equal("AdmissionStalled", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("head-next", payload.RootElement.GetProperty("commitSha").GetString());
        Assert.Equal($"CONDUCTOR_GENERATION_TERMINATED pid=4242 startedAt={StartedAt:O} terminatedAt={TerminatedAt:O} cause=activation-reverted reason=AdmissionStalled commit=head-next", terminated.Detail);
        Assert.True(events.Events.FindIndex(evt => evt.Status == "reverted") < events.Events.IndexOf(terminated));
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("not-forced")]
    [Xunit.InlineData("unconfirmed")]
    [Xunit.InlineData("natural-exit")]
    [Xunit.InlineData("append-throws")]
    public async Task Revert_WithoutWritableProof_PreservesBaselineEventList(string mode)
    {
        using var root = new ArtifactRoot();
        var baseline = new RecordingEvents();
        var candidate = new RecordingEvents { ThrowTermination = mode == "append-throws" };
        var confirmed = mode != "unconfirmed";
        var natural = mode == "natural-exit";
        var expected = await RunRevertAsync(baseline, new FixedClock(), root.Path, forced: false, confirmed: confirmed, natural: natural);
        var actual = await RunRevertAsync(candidate, new FixedClock(), root.Path,
            forced: mode is "unconfirmed" or "append-throws", confirmed: confirmed, natural: natural);
        Assert.Equal(expected, actual);
        // Transport artifact names contain fresh GUIDs; all semantic event fields stay equal.
        Assert.Equal(baseline.Events.Select(NormalizeTransportIds), candidate.Events.Select(NormalizeTransportIds));
        Assert.DoesNotContain(candidate.Events, evt => evt.Operation == "generation-terminated");
        if (mode == "append-throws")
        {
            Assert.Equal(1, candidate.TerminationAttempts);
            Assert.Equal(3, actual.Launches);
            var revert = Assert.Single(candidate.Events.Where(evt => evt.Status == "reverted"));
            Assert.StartsWith("ACTIVATION_REVERTED ", revert.Detail);
            Assert.Equal(Assert.Single(baseline.Events.Where(evt => evt.Status == "reverted")).PayloadJson, revert.PayloadJson);
        }
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task TickStall_ConfirmedForcedStop_RecordsBeforeRelaunch()
    {
        using var root = new ArtifactRoot();
        var events = new RecordingEvents();
        var launches = 0;
        var beforeRelaunch = false;
        var host = new ScriptedHost((request, token) =>
        {
            if (launches++ == 0)
            {
                request.OnProcessStarted?.Invoke(4242);
                request.OnStandardOutputLine!("LOOP_START tick=0");
                request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => stopped.TrySetResult(new(-1, 4242, ProcessStartedAt: StartedAt, ForcedStop: true)));
                return stopped.Task;
            }
            beforeRelaunch = events.Events.Any(evt => evt.Operation == "generation-terminated");
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 4243));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events, timeProvider: new FixedClock(),
            delay: (_, _) => Task.CompletedTask, tickStallBudget: TimeSpan.FromMinutes(2),
            tickStallDelay: (_, _) => Task.CompletedTask, dumpCapture: new NoDump());
        Assert.Equal(0, await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", root.Path, "default", "default")
            .WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, launches);
        Assert.True(beforeRelaunch);
        var terminated = Assert.Single(events.Events.Where(evt => evt.Operation == "generation-terminated"));
        using var payload = JsonDocument.Parse(terminated.PayloadJson!);
        Assert.Equal("tick-stall", payload.RootElement.GetProperty("cause").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("reason").ValueKind);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, true, true)]
    [Xunit.InlineData(4242, false, true)]
    [Xunit.InlineData(4242, true, false)]
    public void Record_InsufficientTerminationAuthority_DoesNotAppend(int pid, bool forced, bool confirmed)
    {
        var events = new RecordingEvents();
        ConductorGenerationTerminationRecorder.Record(events, new FixedClock(),
            new(-1, pid, TerminationConfirmed: confirmed, ForcedStop: forced), "tick-stall", null, "commit");
        Assert.Empty(events.Events);
        Assert.Equal(0, events.TerminationAttempts);
    }

    internal static async Task<(int ExitCode, int Launches)> RunRevertAsync(IRunEventStore events,
        TimeProvider clock, string artifacts, bool forced = true, bool confirmed = true, bool natural = false,
        Action? beforeRestore = null, int pid = 4242, DateTimeOffset? startedAt = null)
    {
        var launches = 0;
        var stageCount = 0;
        var host = new ScriptedHost((request, token) =>
        {
            var index = launches++;
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
            }
            request.OnStandardOutputLine!("LOOP_READY lock=acquired");
            request.OnStandardOutputLine("LOOP_START tick=0");
            if (index == 1)
            {
                if (natural) return Task.FromResult(new ConductorSupervisorProcessResult(1, pid));
                for (var tick = 1; tick <= ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks; tick++)
                {
                    request.OnStandardOutputLine(new ActivationAdmissionSample(tick, false, 3, 13, 0).Format());
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                }
                var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => stopped.TrySetResult(new(-1, pid, TerminationConfirmed: confirmed,
                    ProcessStartedAt: startedAt ?? StartedAt, ForcedStop: forced)));
                return stopped.Task;
            }
            Assert.Equal(2, index);
            beforeRestore?.Invoke();
            for (var tick = 1; tick <= 3; tick++) request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 3, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 102));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events, timeProvider: clock,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
            stageSuccessor: _ => stageCount++ == 0 ? new("C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll", "head-next", "head-next", "LOOP_START selfCheck=true") : null!,
            appendConductEvent: (_, _, _) => { }, raiseActivationAttention: _ => { },
            activationHealthyTicks: 3, activationDelay: (_, _) => throw new InvalidOperationException("Host must provide a completed decision"),
            verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));
        var exit = await supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", "120"],
            "C:\\repo", artifacts, "default", "default").WaitAsync(TestContext.Current.CancellationToken);
        return (exit, launches);
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => TerminatedAt; }
    private static string NormalizeTransportIds(RunEventAppend evt) =>
        System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(evt), "[0-9a-f]{32}", "<transport-id>");
    private sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, CancellationToken, Task<ConductorSupervisorProcessResult>> run) : IConductorSupervisorProcessHost
    {
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token) => run(request, token);
    }
    private sealed class NoDump : IConductorDiagnosticDumpCapture
    {
        public Task<ConductorDumpCaptureResult> CaptureAsync(int? pid, string outputDirectory, CancellationToken token) =>
            Task.FromResult(ConductorDumpCaptureResult.NotCaptured("fixture"));
    }
    private sealed class ArtifactRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "generation-supervisor", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
    private sealed class RecordingEvents : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];
        public bool ThrowTermination { get; init; }
        public int TerminationAttempts { get; private set; }
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            if (evt.Operation == "generation-terminated")
            {
                TerminationAttempts++;
                if (ThrowTermination) throw new IOException("fixture append failure");
            }
            Events.Add(evt);
            return Task.FromResult(new RunEventRecord(Events.Count, "fixture", evt.OccurredAt!.Value,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0, string? goalId = null,
            int maxCount = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
