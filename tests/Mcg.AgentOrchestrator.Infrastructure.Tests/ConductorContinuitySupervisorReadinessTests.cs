using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorReadinessTests
{
    [Xunit.Fact]
    public async Task ReadySuccessor_CompletesHandoffBeforeLoopStart()
    {
        var store = new RecordingRunEventStore();
        var conductEvents = new List<string>();
        var releaseReadinessTimeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoffRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorCancelled = false;
        var prepared = new ConductorPreparedSuccessor(
            "C:\\staged-run",
            "C:\\staged-run\\Mcg.AgentOrchestrator.App.dll",
            "repository-head",
            "staged-head",
            "LOOP_START selfCheck=true");
        var host = new ScriptedSupervisorProcessHost(
            (request, _, _) =>
            {
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, RestartRequested: true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 701));
            },
            async (request, _, cancellationToken) =>
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() =>
                {
                    successorCancelled = true;
                    cancelled.TrySetResult();
                });
                request.OnStandardOutputLine!(ConductorContinuitySupervisor.LoopReadyLinePrefix + "lock=acquired state=loaded");
                releaseReadinessTimeout.TrySetResult();
                var completed = await Task.WhenAny(handoffRecorded.Task, cancelled.Task)
                    .WaitAsync(TestContext.Current.CancellationToken);
                if (completed == cancelled.Task)
                {
                    return new ConductorSupervisorProcessResult(-1, 702, TerminationConfirmed: true);
                }

                Assert.False(cancellationToken.IsCancellationRequested);
                request.OnStandardOutputLine("LOOP_START tick=0");
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 2, 0, RestartRequested: false));
                return new ConductorSupervisorProcessResult(0, 703);
            },
            (request, _, _) =>
            {
                Assert.Null(request.CommandPrefix);
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 3, 0, RestartRequested: false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 704));
            });
        var supervisor = new ConductorContinuitySupervisor(
            host,
            store,
            delay: (_, _) => releaseReadinessTimeout.Task,
            stageSuccessor: _ => prepared,
            appendConductEvent: (_, _, detail) =>
            {
                conductEvents.Add(detail);
                if (detail.StartsWith("LOOP_HANDOFF ", StringComparison.Ordinal))
                {
                    handoffRecorded.TrySetResult();
                }
            },
            readinessTimeout: TimeSpan.FromSeconds(30));

        var exitCode = await supervisor.RunAsync(
            ["conduct", "--loop", "--max-duration", "60"],
            "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-continuity-ready-{Guid.NewGuid():N}"),
            "default",
            "default");

        // Against the pre-fix LOOP_START-only predicate, this event-gated timeout wins and records
        // LOOP_HANDOFF_FAILED phase=readiness before LOOP_START is released.
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(conductEvents, detail =>
            detail.Contains("LOOP_HANDOFF_FAILED", StringComparison.Ordinal));
        Assert.False(successorCancelled);
        Assert.Equal(2, host.Requests.Count);
        var handoff = Assert.Single(store.Events.Where(evt => evt.Operation == "handoff"));
        Assert.Equal("completed", handoff.Status);
        Assert.Contains("stagedSourceCommit=staged-head", handoff.Detail, StringComparison.Ordinal);
        Assert.Contains("repositoryHead=repository-head", handoff.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task SilentSuccessor_TimesOutAndFallsBack()
    {
        var store = new RecordingRunEventStore();
        var conductEvents = new List<string>();
        var stagedRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadinessTimeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new ConductorPreparedSuccessor(
            "C:\\staged-run",
            "C:\\staged-run\\Mcg.AgentOrchestrator.App.dll",
            "new-head",
            "new-head",
            "LOOP_START selfCheck=true");
        var host = new ScriptedSupervisorProcessHost(
            (request, _, _) =>
            {
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, RestartRequested: true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 711));
            },
            (_, _, cancellationToken) =>
            {
                stagedRequestSeen.TrySetResult();
                var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => stopped.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 712, TerminationConfirmed: true)));
                return stopped.Task;
            },
            (request, _, _) =>
            {
                Assert.Null(request.CommandPrefix);
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 2, 0, RestartRequested: false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 713));
            });
        var supervisor = new ConductorContinuitySupervisor(
            host,
            store,
            delay: (_, _) => releaseReadinessTimeout.Task,
            stageSuccessor: _ => prepared,
            appendConductEvent: (_, _, detail) => conductEvents.Add(detail),
            readinessTimeout: TimeSpan.FromSeconds(30));

        var supervisorTask = supervisor.RunAsync(
            ["conduct", "--loop", "--max-duration", "60"],
            "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-continuity-silent-{Guid.NewGuid():N}"),
            "default",
            "default");
        await stagedRequestSeen.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseReadinessTimeout.TrySetResult();

        Assert.Equal(0, await supervisorTask);
        Assert.Equal(3, host.Requests.Count);
        Assert.Contains(conductEvents, detail =>
            detail.Contains("LOOP_HANDOFF_FAILED", StringComparison.Ordinal) &&
            detail.Contains("phase=readiness", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void PreLoopReadiness_EmitsTimestampedStdoutAndJournalEvent()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var line = ConductorContinuitySupervisor.LoopReadyLinePrefix + "lock=acquired state=loaded goals=2";

        var stdout = CaptureConsole(() => CliPersistentStateRunner.EmitPreLoopReadiness(workspace, line));

        Assert.StartsWith(line + " ts=", stdout.Trim(), StringComparison.Ordinal);
        var conductEvent = Assert.Single(File.ReadAllLines(workspace.ConductEventsLogPath));
        Assert.Contains("\"eventKind\":\"loop-ready\"", conductEvent, StringComparison.Ordinal);
        Assert.Contains($"\"detail\":\"{line}\"", conductEvent, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void PreLoopStartup_HeldLeaseEmitsReadyBeforeSweep()
    {
        var observed = new List<string>();

        var result = CliPersistentStateRunner.RunConductPreLoopStartup(
            () =>
            {
                observed.Add("lease-held");
                return true;
            },
            () =>
            {
                observed.Add("state-loaded");
                return "loaded-state";
            },
            state => ConductorContinuitySupervisor.LoopReadyLinePrefix + state,
            observed.Add,
            state =>
            {
                observed.Add("startup-sweep");
                return state + "-swept";
            });

        Assert.Equal("loaded-state-swept", result);
        Assert.Equal(
            ["lease-held", "state-loaded", "LOOP_READY loaded-state", "startup-sweep"],
            observed);

        var unsafeObserved = new List<string>();
        Assert.Throws<InvalidOperationException>(() => CliPersistentStateRunner.RunConductPreLoopStartup(
            () =>
            {
                unsafeObserved.Add("lease-not-held");
                return false;
            },
            () =>
            {
                unsafeObserved.Add("state-loaded");
                return "unsafe-state";
            },
            state => ConductorContinuitySupervisor.LoopReadyLinePrefix + state,
            unsafeObserved.Add,
            state => state));
        Assert.Equal(["lease-not-held"], unsafeObserved);
    }

    private sealed class ScriptedSupervisorProcessHost : IConductorSupervisorProcessHost
    {
        private readonly Func<
            ConductorSupervisorProcessRequest,
            int,
            CancellationToken,
            Task<ConductorSupervisorProcessResult>>[] _steps;

        public ScriptedSupervisorProcessHost(
            params Func<
                ConductorSupervisorProcessRequest,
                int,
                CancellationToken,
                Task<ConductorSupervisorProcessResult>>[] steps)
        {
            _steps = steps;
        }

        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(request);
            return _steps[Math.Min(index, _steps.Length - 1)](request, index, cancellationToken);
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
