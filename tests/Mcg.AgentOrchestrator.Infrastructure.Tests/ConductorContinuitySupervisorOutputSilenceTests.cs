using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorOutputSilenceTests
{
    [Xunit.Fact]
    public async Task SilentAdoptedSuccessorIsRecordedAsOutputSilenceDumpedStoppedAndRelaunchedOnSameBuild()
    {
        var clock = new AdvancingClock();
        var events = new RecordingEvents();
        var actions = new List<string>();
        var delays = new List<TimeSpan>();
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index == 0) return DeliberateExit(request, "max-duration");
            if (index == 1)
            {
                request.OnProcessStarted?.Invoke(4242);
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                var exit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() =>
                {
                    actions.Add("stop");
                    exit.TrySetResult(new(-1, 4242));
                });
                return exit.Task;
            }
            return DeliberateExit(request, "stop-file");
        });
        var supervisor = NewSupervisor(host, events, clock,
            (duration, _) =>
            {
                delays.Add(duration);
                clock.Advance(duration);
                return Task.CompletedTask;
            },
            (_, _, _) =>
            {
                actions.Add("dump");
                return Task.FromResult(new ConductorDumpCaptureResult(true, "x.dmp", "captured"));
            },
            activationHealthyTicks: 1);

        var exitCode = await Run(supervisor);

        Assert.Equal(0, exitCode);
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(delays));
        Assert.Equal(new[] { "dump", "stop" }, actions);
        Assert.Equal(3, host.Requests.Count);
        Assert.Equal(new[] { "dotnet-test", "C:\\next\\Mcg.AgentOrchestrator.App.dll" },
            host.Requests[2].CommandPrefix);
        var stall = Assert.Single(events.Events.Where(e => e.Operation == "tick-stall" && e.Status == "detected"));
        Assert.Contains("trigger=output-silence", stall.Detail, StringComparison.Ordinal);
        Assert.Contains("outputSilenceBudgetMinutes=10", stall.Detail, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(stall.PayloadJson!);
        Assert.Equal("output-silence", payload.RootElement.GetProperty("trigger").GetString());
        Assert.Equal(clock.StartedAt, payload.RootElement.GetProperty("lastLineAt").GetDateTimeOffset());
        Assert.Equal(10, payload.RootElement.GetProperty("outputSilenceBudgetMinutes").GetDouble());
    }

    [Xunit.Fact]
    public async Task NeverWritingPlainChildIsRecoveredAtSilenceBudget()
    {
        var clock = new AdvancingClock();
        var events = new RecordingEvents();
        var delays = new List<TimeSpan>();
        var stops = 0;
        var captures = 0;
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index > 0) return DeliberateExit(request, "stop-file");
            request.OnProcessStarted?.Invoke(4242);
            var exit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() =>
            {
                stops++;
                exit.TrySetResult(new(-1, 4242));
            });
            return exit.Task;
        });
        var supervisor = NewSupervisor(host, events, clock,
            (duration, _) =>
            {
                delays.Add(duration);
                clock.Advance(duration);
                return Task.CompletedTask;
            },
            (_, _, _) =>
            {
                captures++;
                return Task.FromResult(ConductorDumpCaptureResult.NotCaptured("test"));
            });

        Assert.Equal(0, await Run(supervisor));
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(delays));
        Assert.Equal(1, stops);
        Assert.Equal(1, captures);
        var stall = Assert.Single(events.Events.Where(e => e.Operation == "tick-stall" && e.Status == "detected"));
        using var payload = JsonDocument.Parse(stall.PayloadJson!);
        Assert.Equal(clock.StartedAt, payload.RootElement.GetProperty("lastLineAt").GetDateTimeOffset());
    }

    [Xunit.Fact]
    public async Task DrainingSuccessorWritingRelaunchDrainEveryFiveSecondsIsNeverStopped()
    {
        var clock = new AdvancingClock();
        var events = new RecordingEvents();
        var delays = new List<TimeSpan>();
        var stops = 0;
        var captures = 0;
        ConductorSupervisorProcessRequest? active = null;
        TaskCompletionSource<ConductorSupervisorProcessResult>? childExit = null;
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index == 0) return DeliberateExit(request, "max-duration");
            Assert.Equal(1, index);
            active = request;
            request.OnProcessStarted?.Invoke(4242);
            request.OnStandardOutputLine!("LOOP_START tick=0");
            request.OnStandardOutputLine("TICK_END tick=1 activation=true");
            childExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => stops++);
            return childExit.Task;
        });
        var elapsed = TimeSpan.Zero;
        var supervisor = NewSupervisor(host, events, clock,
            (duration, _) =>
            {
                delays.Add(duration);
                for (var i = 0; i < 120 && elapsed < TimeSpan.FromMinutes(30); i++)
                {
                    clock.Advance(TimeSpan.FromSeconds(5));
                    elapsed += TimeSpan.FromSeconds(5);
                    active!.OnStandardOutputLine!("LOOP_RELAUNCH_DRAIN tick=2 active=1 admitting=false");
                }
                if (elapsed >= TimeSpan.FromMinutes(30))
                {
                    ConductorContinuityExitArtifact.Write(active!.ExitArtifactPath,
                        new("stop-file", 2, 0, false));
                    childExit!.TrySetResult(new(0, 4242));
                }
                return Task.CompletedTask;
            },
            (_, _, _) =>
            {
                captures++;
                return Task.FromResult(ConductorDumpCaptureResult.NotCaptured("unused"));
            },
            activationHealthyTicks: 1);

        Assert.Equal(0, await Run(supervisor));
        Assert.Equal(TimeSpan.FromMinutes(30), elapsed);
        Assert.Equal(0, stops);
        Assert.Equal(0, captures);
        Assert.DoesNotContain(events.Events, e => e.Operation == "tick-stall");
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromMinutes(10)));
    }

    [Xunit.Fact]
    public async Task IdleChildTickingEveryPollIntervalAtThreeHundredSecondsIsNeverStopped()
    {
        var clock = new AdvancingClock();
        var events = new RecordingEvents();
        var stops = 0;
        var captures = 0;
        var tick = 1;
        ConductorSupervisorProcessRequest? active = null;
        TaskCompletionSource<ConductorSupervisorProcessResult>? childExit = null;
        var host = new ScriptedHost((request, index, token) =>
        {
            Assert.Equal(0, index);
            active = request;
            request.OnProcessStarted?.Invoke(4242);
            request.OnStandardOutputLine!("LOOP_START tick=0");
            request.OnStandardOutputLine("TICK_END tick=1 activation=true");
            childExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => stops++);
            return childExit.Task;
        });
        var supervisor = NewSupervisor(host, events, clock,
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromMinutes(5));
                active!.OnStandardOutputLine!($"TICK_END tick={++tick} activation=true");
                if (tick == 13)
                {
                    ConductorContinuityExitArtifact.Write(active.ExitArtifactPath,
                        new("stop-file", tick, 0, false));
                    childExit!.TrySetResult(new(0, 4242));
                }
                return Task.CompletedTask;
            },
            (_, _, _) =>
            {
                captures++;
                return Task.FromResult(ConductorDumpCaptureResult.NotCaptured("unused"));
            },
            activationHealthyTicks: 1, outputSilenceBudget:
                ConductorContinuitySupervisor.ResolveOutputSilenceBudget(null, 300));

        Assert.Equal(0, await supervisor.RunAsync(["conduct", "--loop", "--poll-seconds", "300"],
            "C:\\repo", ArtifactDirectory(), "default", "default")
            .WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, stops);
        Assert.Equal(0, captures);
        Assert.DoesNotContain(events.Events, e => e.Operation == "tick-stall");
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 15, 10)]
    [Xunit.InlineData("15", 15, 15)]
    [Xunit.InlineData(" 12 ", 15, 12)]
    [Xunit.InlineData("3", 15, 3)]
    [Xunit.InlineData("", 15, 10)]
    [Xunit.InlineData("  ", 15, 10)]
    [Xunit.InlineData("abc", 15, 10)]
    [Xunit.InlineData("2.5", 15, 10)]
    [Xunit.InlineData("0", 15, 10)]
    [Xunit.InlineData("-5", 15, 10)]
    [Xunit.InlineData(null, 120, 10)]
    [Xunit.InlineData(null, 300, 16)]
    public void BudgetUsesPositiveIntegerMinutesAndPollFloor(string? value, int pollSeconds,
        int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            ConductorContinuitySupervisor.ResolveOutputSilenceBudget(value, pollSeconds));

    private static ConductorContinuitySupervisor NewSupervisor(
        ScriptedHost host, RecordingEvents events, AdvancingClock clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<int?, string, CancellationToken, Task<ConductorDumpCaptureResult>> capture,
        int activationHealthyTicks = 3, TimeSpan? outputSilenceBudget = null) =>
        new(host, events, timeProvider: clock, delay: (_, _) => Task.CompletedTask,
            stageSuccessor: _ => Prepared(), dotnetPath: "dotnet-test",
            activationHealthyTicks: activationHealthyTicks,
            activationDelay: (_, _) => throw new InvalidOperationException("Already healthy"),
            tickStallBudget: ConductorContinuitySupervisor.ResolveTickStallBudget(null),
            tickStallDelay: delay, dumpCapture: new ScriptedDump(capture),
            outputSilenceBudget: outputSilenceBudget ?? TimeSpan.FromMinutes(10));

    private static Task<int> Run(ConductorContinuitySupervisor supervisor) =>
        supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", ArtifactDirectory(),
            "default", "default").WaitAsync(TestContext.Current.CancellationToken);

    private static Task<ConductorSupervisorProcessResult> DeliberateExit(
        ConductorSupervisorProcessRequest request, string reason)
    {
        ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
            reason == "max-duration" ? new(reason, 1, 1, true) : new(reason, 1, 0, false));
        return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
    }

    private static ConductorPreparedSuccessor Prepared() =>
        new("C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll", "head-next", "head-next",
            "LOOP_START selfCheck=true");

    private static string ArtifactDirectory() =>
        Path.Combine(Path.GetTempPath(), $"mcg-output-silence-{Guid.NewGuid():N}");

    private sealed class AdvancingClock : TimeProvider
    {
        internal DateTimeOffset StartedAt { get; } = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now;
        internal AdvancingClock() => _now = StartedAt;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
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
