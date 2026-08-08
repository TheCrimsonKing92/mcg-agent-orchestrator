using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false, true)]
    [Xunit.InlineData(true, false, false)]
    [Xunit.InlineData(false, true, false)]
    public void Supervision_ChildOrAuthorityTransfer_IsNotNested(
        bool child,
        bool authorityTransfer,
        bool expected)
    {
        var args = new List<string> { "conduct", "--loop" };
        if (child)
        {
            args.Add(ConductorContinuitySupervisor.ChildFlag);
        }

        Assert.Equal(
            expected,
            ConductorContinuitySupervisor.ShouldSupervise(args, authorityTransfer));
    }

    [Xunit.Fact]
    public void Lifecycle_Stop_PersistsCauseWithSeamedClock()
    {
        var store = new RecordingRunEventStore();
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var recorder = new ConductorLifecycleRecorder(store, () => now, () => "generation-1");

        var session = recorder.Start("Conservative", null, now, null, TimeSpan.FromMinutes(5));
        now = now.AddMinutes(2);
        session.Stop("stop-file", 7);

        Assert.Equal(2, store.Events.Count);
        Assert.Equal(["start", "stop"], store.Events.Select(evt => evt.Operation));
        Assert.Equal("stop-file", store.Events[1].Status);
        Assert.Equal(now, store.Events[1].OccurredAt);
        Assert.All(store.Events, evt =>
            Assert.Equal("generation-1", JsonDocument.Parse(evt.PayloadJson!).RootElement.GetProperty("generationId").GetString()));
    }

    [Xunit.Fact]
    public async Task MissingArtifact_RestartsThenAcceptsIntentionalStop()
    {
        var store = new RecordingRunEventStore();
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var delays = new List<TimeSpan>();
        var host = new ScriptedSupervisorProcessHost(
            (_, _) => new ConductorSupervisorProcessResult(1, 101),
            (request, _) =>
            {
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 4, 0, RestartRequested: false));
                return new ConductorSupervisorProcessResult(0, 102);
            });
        var supervisor = new ConductorContinuitySupervisor(
            host,
            store,
            time,
            (delay, _) =>
            {
                delays.Add(delay);
                time.AdvanceForTests(delay);
                return Task.CompletedTask;
            });

        var exitCode = await supervisor.RunAsync(
            ["conduct", "--loop", "--watch"],
            "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-continuity-{Guid.NewGuid():N}"),
            "project-a",
            "tenant-a");

        Assert.Equal(0, exitCode);
        Assert.Equal(2, host.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
        Assert.Equal(["unexpected", "completed"], store.Events.Select(evt => evt.Status));
        Assert.All(host.Requests, request =>
        {
            Assert.Contains("--project=project-a", request.Arguments);
            Assert.Contains("--tenant=tenant-a", request.Arguments);
            Assert.Contains(ConductorContinuitySupervisor.ChildFlag, request.Arguments);
        });
    }

    [Xunit.Fact]
    public async Task LaunchFailure_RestartsThenAcceptsIntentionalStop()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-continuity-launch-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "logs");
        var store = new RecordingRunEventStore();
        var host = new ScriptedSupervisorProcessHost(
            (_, _) => throw new InvalidOperationException("spawn failed"),
            (request, _) =>
            {
                ConductorContinuityExitArtifact.Write(
                    request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 1, 0, RestartRequested: false));
                return new ConductorSupervisorProcessResult(0, 402);
            });
        var supervisor = new ConductorContinuitySupervisor(
            host,
            store,
            delay: (_, _) => Task.CompletedTask);

        try
        {
            var exitCode = await supervisor.RunAsync(
                ["conduct", "--loop"],
                "C:\\repo",
                Path.Combine(root, "artifacts"),
                "default",
                "default",
                logDirectory);

            Assert.Equal(0, exitCode);
            Assert.Equal(2, host.Requests.Count);
            Assert.Contains("launch=InvalidOperationException:spawn failed", store.Events[0].Detail, StringComparison.Ordinal);
            Assert.All(host.Requests, request =>
            {
                Assert.True(Path.IsPathFullyQualified(request.StdoutPath));
                Assert.True(Path.IsPathFullyQualified(request.StderrPath));
                Assert.True(File.Exists(request.StdoutPath));
                Assert.True(File.Exists(request.StderrPath));
            });
            var payload = JsonDocument.Parse(store.Events[0].PayloadJson!).RootElement;
            Assert.Equal(host.Requests[0].StdoutPath, payload.GetProperty("stdoutPath").GetString());
            Assert.Equal(host.Requests[0].StderrPath, payload.GetProperty("stderrPath").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ProcessHost_TeesChildOutputAndPublishesEnvironmentPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-continuity-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var stdoutPath = Path.Combine(root, "child.out.log");
        var stderrPath = Path.Combine(root, "child.err.log");
        var request = new ConductorSupervisorProcessRequest(
            [],
            root,
            Path.Combine(root, "exit.json"),
            stdoutPath,
            stderrPath);
        var host = new SystemConductorSupervisorProcessHost(_ => BuildOutputProbeStartInfo());

        try
        {
            var result = await host.RunAsync(request, CancellationToken.None);

            Assert.Equal(7, result.ExitCode);
            Assert.True(result.ProcessId > 0);
            Assert.Equal(stdoutPath, result.StdoutPath);
            Assert.Equal(stderrPath, result.StderrPath);
            var stdout = File.ReadAllText(stdoutPath);
            var stderr = File.ReadAllText(stderrPath);
            Assert.Contains("child-out", stdout, StringComparison.Ordinal);
            Assert.Contains(stdoutPath, stdout, StringComparison.Ordinal);
            Assert.Contains("child-err", stderr, StringComparison.Ordinal);
            Assert.Contains(stderrPath, stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task RepeatedUnexpectedExit_EscalatesAtRestartCap()
    {
        var store = new RecordingRunEventStore();
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        var host = new ScriptedSupervisorProcessHost(
            (_, index) => new ConductorSupervisorProcessResult(2, 200 + index));
        var supervisor = new ConductorContinuitySupervisor(
            host,
            store,
            time,
            (delay, _) =>
            {
                time.AdvanceForTests(delay);
                return Task.CompletedTask;
            },
            maxUnexpectedRestarts: 2,
            restartWindow: TimeSpan.FromMinutes(5));

        var exitCode = await supervisor.RunAsync(
            ["conduct", "--loop"],
            "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-continuity-{Guid.NewGuid():N}"),
            "default",
            "default");

        Assert.Equal(1, exitCode);
        Assert.Equal(3, host.Requests.Count);
        Assert.Equal("escalated", store.Events[^1].Status);
        Assert.Contains("restart-cap count=3 max=2", store.Events[^1].Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task PlannedRenewals_EscalateWithoutProgressAtCap()
    {
        var store = new RecordingRunEventStore();
        var host = new ScriptedSupervisorProcessHost((request, index) =>
        {
            ConductorContinuityExitArtifact.Write(
                request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("max-duration", index, 0, RestartRequested: true));
            return new ConductorSupervisorProcessResult(0, 300 + index);
        });
        var supervisor = new ConductorContinuitySupervisor(
            host,
            store,
            maxRenewalsWithoutProgress: 2);

        var exitCode = await supervisor.RunAsync(
            ["conduct", "--loop", "--max-duration", "60"],
            "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-continuity-{Guid.NewGuid():N}"),
            "default",
            "default");

        Assert.Equal(1, exitCode);
        Assert.Equal(3, host.Requests.Count);
        Assert.Equal("escalated", store.Events[^1].Status);
        Assert.Contains("renewal-cap count=3 max=2", store.Events[^1].Detail, StringComparison.Ordinal);
    }

    private sealed class ScriptedSupervisorProcessHost(
        params Func<ConductorSupervisorProcessRequest, int, ConductorSupervisorProcessResult>[] steps)
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
            var step = steps[Math.Min(index, steps.Length - 1)];
            return Task.FromResult(step(request, index));
        }
    }

    private static System.Diagnostics.ProcessStartInfo BuildOutputProbeStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(
                "echo child-out & echo %MCG_ORCHESTRATOR_STDOUT_LOG_PATH% & " +
                "echo child-err 1>&2 & echo %MCG_ORCHESTRATOR_STDERR_LOG_PATH% 1>&2 & exit /b 7");
            return startInfo;
        }

        var shell = new System.Diagnostics.ProcessStartInfo("/bin/sh");
        shell.ArgumentList.Add("-c");
        shell.ArgumentList.Add(
            "printf '%s\\n' child-out \"$MCG_ORCHESTRATOR_STDOUT_LOG_PATH\"; " +
            "printf '%s\\n' child-err \"$MCG_ORCHESTRATOR_STDERR_LOG_PATH\" >&2; exit 7");
        return shell;
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
