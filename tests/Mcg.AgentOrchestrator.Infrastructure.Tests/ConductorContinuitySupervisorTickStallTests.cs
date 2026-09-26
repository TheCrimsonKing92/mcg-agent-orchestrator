using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorTickStallTests
{
    [Xunit.Fact]
    public async Task StalledSuccessorAfterAdoptionIsRecordedDumpedStoppedAndRelaunchedOnSameBuild()
    {
        var run = await RunStalledSuccessor("captured", terminationConfirmed: true);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(3, run.Host.Requests.Count);
        Assert.Equal(new[] { "dump", "stop" }, run.Actions);
        Assert.Equal(new[] { "dotnet-test", "C:\\next\\Mcg.AgentOrchestrator.App.dll" },
            run.Host.Requests[2].CommandPrefix);
        var stall = Assert.Single(run.Events.Events.Where(e =>
            e.Operation == "tick-stall" && e.Status == "detected"));
        using var payload = JsonDocument.Parse(stall.PayloadJson!);
        Assert.Equal(5, payload.RootElement.GetProperty("lastTick").GetInt32());
        Assert.Equal("2026-09-26T06:00:00+00:00",
            payload.RootElement.GetProperty("lastTickEndAt").GetDateTimeOffset().ToString("O"));
        Assert.Equal(4242, payload.RootElement.GetProperty("processId").GetInt32());
        Assert.True(payload.RootElement.GetProperty("dump").GetProperty("captured").GetBoolean());
        Assert.Equal("head-next", payload.RootElement.GetProperty("build").GetProperty("commitSha").GetString());
        Assert.Equal("C:\\next", payload.RootElement.GetProperty("build").GetProperty("stagedBuildId").GetString());
        Assert.Contains(run.Events.Events, e => e.Operation == "restart" && e.Status == "unexpected" &&
            e.Detail.Contains("tick-stall lastTick=5", StringComparison.Ordinal));
        Assert.All(run.Delays, d => Assert.Equal(TimeSpan.FromMinutes(2), d));
    }

    [Xunit.Fact]
    public async Task MissingDumpToolStillStopsAndRelaunchesAndRecordsNoDump()
    {
        var run = await RunStalledSuccessor("tool-missing", terminationConfirmed: true);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(3, run.Host.Requests.Count);
        Assert.Equal(new[] { "dump", "stop" }, run.Actions);
        var stall = Assert.Single(run.Events.Events.Where(e =>
            e.Operation == "tick-stall" && e.Status == "detected"));
        using var payload = JsonDocument.Parse(stall.PayloadJson!);
        Assert.False(payload.RootElement.GetProperty("dump").GetProperty("captured").GetBoolean());
        Assert.Equal("tool-missing", payload.RootElement.GetProperty("dump").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("dump").GetProperty("path").ValueKind);
        Assert.Contains("dump=not-captured reason=tool-missing", stall.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task ThrowingDumpCaptureStillStopsAndRelaunches()
    {
        var run = await RunStalledSuccessor("throw", terminationConfirmed: true);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(3, run.Host.Requests.Count);
        Assert.Equal(new[] { "dump", "stop" }, run.Actions);
        var stall = Assert.Single(run.Events.Events.Where(e =>
            e.Operation == "tick-stall" && e.Status == "detected"));
        using var payload = JsonDocument.Parse(stall.PayloadJson!);
        Assert.False(payload.RootElement.GetProperty("dump").GetProperty("captured").GetBoolean());
        Assert.Equal("error=InvalidOperationException",
            payload.RootElement.GetProperty("dump").GetProperty("reason").GetString());
    }

    [Xunit.Fact]
    public async Task UnconfirmedTerminationAfterTickStallEscalatesWithoutRelaunch()
    {
        var run = await RunStalledSuccessor("tool-missing", terminationConfirmed: false);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(2, run.Host.Requests.Count);
        Assert.Contains(run.Events.Events, e => e.Operation == "tick-stall" && e.Status == "escalated");
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ProgressingSuccessorWhoseTicksKeepEndingIsNeverStopped(bool repeatTickNumber)
    {
        var events = new RecordingEvents();
        var delays = new List<TimeSpan>();
        var stops = 0;
        var captures = 0;
        var tick = 3;
        ConductorSupervisorProcessRequest? active = null;
        TaskCompletionSource<ConductorSupervisorProcessResult>? childExit = null;
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
            }

            Assert.Equal(1, index);
            active = request;
            request.OnProcessStarted?.Invoke(4242);
            request.OnStandardOutputLine!("LOOP_READY lock=acquired");
            request.OnStandardOutputLine("LOOP_START tick=0");
            for (var i = 1; i <= 3; i++) request.OnStandardOutputLine($"TICK_END tick={i} activation=true");
            childExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => Interlocked.Increment(ref stops));
            return childExit.Task;
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            timeProvider: new FixedClock(), delay: (_, _) => Task.CompletedTask,
            stageSuccessor: _ => Prepared(), dotnetPath: "dotnet-test",
            activationDelay: (_, _) => throw new InvalidOperationException("Already healthy"),
            tickStallBudget: TimeSpan.FromMinutes(2),
            tickStallDelay: (duration, _) =>
            {
                delays.Add(duration);
                if (++tick <= 9)
                {
                    active!.OnStandardOutputLine!($"TICK_END tick={(repeatTickNumber ? 7 : tick)} activation=true");
                }
                else
                {
                    ConductorContinuityExitArtifact.Write(active!.ExitArtifactPath,
                        new("stop-file", tick, 0, false));
                    childExit!.TrySetResult(new ConductorSupervisorProcessResult(0, 4242));
                }

                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            },
            dumpCapture: new ScriptedDump((_, _, _) =>
            {
                captures++;
                return Task.FromResult(ConductorDumpCaptureResult.NotCaptured("unused"));
            }));

        var exitCode = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", ArtifactDirectory(),
            "default", "default").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, stops);
        Assert.Equal(0, captures);
        Assert.DoesNotContain(events.Events, e => e.Operation == "tick-stall");
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMinutes(2), d));
    }

    [Xunit.Fact]
    public async Task PlainLaunchIsWatchedAndRelaunched()
    {
        var events = new RecordingEvents();
        var stopped = false;
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index == 0)
            {
                request.OnProcessStarted?.Invoke(8181);
                request.OnStandardOutputLine!("LOOP_START tick=0");
                request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                var exit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() =>
                {
                    stopped = true;
                    exit.TrySetResult(new(-1, 8181));
                });
                return exit.Task;
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 8182));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            timeProvider: new FixedClock(), delay: (_, _) => Task.CompletedTask,
            tickStallBudget: TimeSpan.FromMinutes(2),
            tickStallDelay: (_, _) => Task.CompletedTask,
            dumpCapture: new ScriptedDump((_, _, _) =>
                Task.FromResult(ConductorDumpCaptureResult.NotCaptured("tool-missing"))));

        var exitCode = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", ArtifactDirectory(),
            "default", "default").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(stopped);
        Assert.Equal(2, host.Requests.Count);
        Assert.Null(host.Requests[1].CommandPrefix);
        Assert.Contains(events.Events, e => e.Operation == "tick-stall" && e.Status == "detected");
    }

    [Xunit.Fact]
    public void DefaultTickStallBudgetCoversLongestInlineAcceptancePhase()
    {
        Assert.True(ConductorContinuitySupervisor.ResolveTickStallBudget(null) >=
            AcceptanceCheckTimeouts.DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(45), ConductorContinuitySupervisor.ResolveTickStallBudget("45"));
    }

    private static async Task<ScenarioResult> RunStalledSuccessor(string dumpReason, bool terminationConfirmed)
    {
        var events = new RecordingEvents();
        var actions = new List<string>();
        var delays = new List<TimeSpan>();
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
            }

            if (index == 1)
            {
                request.OnProcessStarted?.Invoke(4242);
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                for (var tick = 1; tick <= 5; tick++)
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() =>
                {
                    actions.Add("stop");
                    stopped.TrySetResult(new(-1, 4242, TerminationConfirmed: terminationConfirmed));
                });
                return stopped.Task;
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
        });
        var dump = new ScriptedDump((pid, _, _) =>
        {
            Assert.Equal(4242, pid);
            actions.Add("dump");
            if (dumpReason == "throw") throw new InvalidOperationException("diagnostic tool failed");
            return Task.FromResult(dumpReason == "captured"
                ? new ConductorDumpCaptureResult(true, "x.dmp", "captured")
                : ConductorDumpCaptureResult.NotCaptured(dumpReason));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            timeProvider: new FixedClock(), delay: (_, _) => Task.CompletedTask,
            stageSuccessor: _ => Prepared(), dotnetPath: "dotnet-test",
            activationDelay: (_, _) => throw new InvalidOperationException("Already healthy"),
            tickStallBudget: TimeSpan.FromMinutes(2),
            tickStallDelay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            dumpCapture: dump);
        var exit = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", ArtifactDirectory(),
            "default", "default").WaitAsync(TestContext.Current.CancellationToken);
        return new(exit, host, events, actions, delays);
    }

    private static ConductorPreparedSuccessor Prepared() =>
        new("C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll", "head-next", "head-next",
            "LOOP_START selfCheck=true");

    private static string ArtifactDirectory() =>
        Path.Combine(Path.GetTempPath(), $"mcg-tick-stall-{Guid.NewGuid():N}");

    private sealed record ScenarioResult(int ExitCode, ScriptedHost Host, RecordingEvents Events,
        List<string> Actions, List<TimeSpan> Delays);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
    }

    private sealed class ScriptedDump(
        Func<int?, string, CancellationToken, Task<ConductorDumpCaptureResult>> capture)
        : IConductorDiagnosticDumpCapture
    {
        public Task<ConductorDumpCaptureResult> CaptureAsync(int? pid, string outputDirectory,
            CancellationToken cancellationToken) => capture(pid, outputDirectory, cancellationToken);
    }

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken,
            Task<ConductorSupervisorProcessResult>> step) : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request,
            CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, cancellationToken);
        }
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.FromResult(new RunEventRecord(Events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation,
                evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
