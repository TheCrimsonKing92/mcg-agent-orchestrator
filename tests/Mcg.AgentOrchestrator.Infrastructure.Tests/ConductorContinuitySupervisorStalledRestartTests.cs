using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted processes, a fixed clock, and a unique artifact directory.
public sealed class ConductorContinuitySupervisorStalledRestartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stall_ValidRestartArtifact_StagesPlannedSuccessor(bool outputSilence)
    {
        var result = await RunScenario("restart", outputSilence: outputSilence);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.StageCalls);
        Assert.Equal(2, result.Launches);
        Assert.Single(result.Events.Where(e => e.Operation == "restart" && e.Status == "planned"));
        Assert.DoesNotContain(result.Events, e => e.Operation == "restart" && e.Status == "unexpected");
        var honored = Assert.Single(result.ConductEvents.Where(line =>
            line.StartsWith("SUPERVISOR_STALL_RESTART_HONORED ", StringComparison.Ordinal)));
        Assert.Contains("attempt=1 pid=4242 stall=tick-stall", honored);
        if (outputSilence) Assert.Contains("trigger=output-silence", honored);
        AssertStall(result, outputSilence ? "output-silence" : "tick-end");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("no-restart")]
    [InlineData("unreadable")]
    public async Task Stall_WithoutValidRestartRequest_RecordsUnexpected(string artifact)
    {
        var result = await RunScenario(artifact);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.StageCalls);
        Assert.Equal(2, result.Launches);
        Assert.Single(result.Events.Where(e => e.Operation == "restart" && e.Status == "unexpected"));
        Assert.DoesNotContain(result.Events, e => e.Operation == "restart" && e.Status == "planned");
        AssertNoStallRestartHonored(result);
        AssertStall(result, "tick-end");
    }

    [Fact]
    public async Task Stall_StopFileArtifact_PreservesDeliberateStop()
    {
        var result = await RunScenario("stop-file");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.StageCalls);
        Assert.Equal(1, result.Launches);
        Assert.Single(result.Events.Where(e => e.Operation == "stopped" && e.Status == "completed"));
        Assert.DoesNotContain(result.Events, e => e.Operation == "restart");
        Assert.Single(result.ConductEvents.Where(line => line.StartsWith("SUPERVISOR_STOP_HONORED ", StringComparison.Ordinal)));
        AssertNoStallRestartHonored(result);
        AssertStall(result, "tick-end");
    }

    [Fact]
    public async Task Stall_UnconfirmedTermination_EscalatesBeforeStaging()
    {
        var result = await RunScenario("restart", terminationConfirmed: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, result.StageCalls);
        Assert.Equal(1, result.Launches);
        Assert.Single(result.Events.Where(e => e.Operation == "tick-stall" && e.Status == "escalated"));
        Assert.DoesNotContain(result.Events, e => e.Operation == "restart" && e.Status == "planned");
        AssertNoStallRestartHonored(result);
        AssertStall(result, "tick-end");
    }

    private static void AssertNoStallRestartHonored(ScenarioResult result) =>
        Assert.DoesNotContain(result.ConductEvents, line => line.Contains("SUPERVISOR_STALL_RESTART_HONORED", StringComparison.Ordinal));

    private static void AssertStall(ScenarioResult result, string trigger)
    {
        Assert.Equal(1, result.Stops);
        Assert.Equal(1, result.Dumps);
        var detected = Assert.Single(result.Events.Where(e => e.Operation == "tick-stall" && e.Status == "detected"));
        using var json = JsonDocument.Parse(detected.PayloadJson!);
        Assert.Equal(trigger, json.RootElement.GetProperty("trigger").GetString());
    }

    private static async Task<ScenarioResult> RunScenario(string artifact,
        bool outputSilence = false, bool terminationConfirmed = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-stalled-restart-{Guid.NewGuid():N}");
        var events = new RecordingEvents();
        var conductEvents = new List<string>();
        var stageCalls = 0;
        var stops = 0;
        var dumps = 0;
        var host = new ScriptedHost((request, index, token) =>
        {
            if (index > 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 1, 0, false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 4243));
            }

            request.OnProcessStarted?.Invoke(4242);
            switch (artifact)
            {
                case "restart":
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 1, 1, true));
                    break;
                case "no-restart":
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 1, 0, false));
                    break;
                case "stop-file":
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 1, 0, false));
                    break;
                case "unreadable":
                    Directory.CreateDirectory(Path.GetDirectoryName(request.ExitArtifactPath)!);
                    File.WriteAllText(request.ExitArtifactPath, "{invalid-json");
                    break;
                case "missing": break;
                default: throw new InvalidOperationException($"Unknown artifact fixture: {artifact}");
            }

            var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() =>
            {
                stops++;
                stopped.TrySetResult(new(-1, 4242, TerminationConfirmed: terminationConfirmed));
            });
            return stopped.Task;
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            timeProvider: new FixedClock(), delay: (_, _) => Task.CompletedTask,
            inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
            stageSuccessor: _ =>
            {
                stageCalls++;
                return new("C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll", "head-next", "head-next", "LOOP_START selfCheck=true");
            },
            appendConductEvent: (_, _, detail) => conductEvents.Add(detail),
            dotnetPath: "dotnet-test",
            tickStallBudget: TimeSpan.FromMinutes(outputSilence ? 20 : 2),
            outputSilenceBudget: TimeSpan.FromMinutes(10),
            tickStallDelay: (_, _) => Task.CompletedTask,
            dumpCapture: new ScriptedDump((pid, _, _) =>
            {
                Assert.Equal(4242, pid);
                dumps++;
                return Task.FromResult(new ConductorDumpCaptureResult(true, "fixture.dmp", "captured"));
            }));
        try
        {
            var exit = await TestHangGuard.WaitAsync(supervisor.RunAsync(
                ["conduct", "--loop"], "C:\\repo", root, "default", "default",
                cancellationToken: TestContext.Current.CancellationToken), "stalled restart scenario");
            return new(exit, stageCalls, host.Launches, stops, dumps, events.Events, conductEvents);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record ScenarioResult(int ExitCode, int StageCalls, int Launches,
        int Stops, int Dumps, List<RunEventAppend> Events, List<string> ConductEvents);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class ScriptedDump(
        Func<int?, string, CancellationToken, Task<ConductorDumpCaptureResult>> capture) : IConductorDiagnosticDumpCapture
    {
        public Task<ConductorDumpCaptureResult> CaptureAsync(int? pid, string outputDirectory,
            CancellationToken cancellationToken) => capture(pid, outputDirectory, cancellationToken);
    }

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        internal int Launches { get; private set; }

        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request,
            CancellationToken cancellationToken) => step(request, Launches++, cancellationToken);
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        internal List<RunEventAppend> Events { get; } = [];

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
