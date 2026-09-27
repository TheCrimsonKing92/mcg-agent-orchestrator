using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorRenewalProgressTests
{
    [Xunit.Fact]
    public async Task SevenLandingRenewals_RelaunchWithoutEscalation()
    {
        var (exitCode, requests, events, conductEvents) = await RunRenewals(landedGoals: 1);

        Assert.Equal(0, exitCode);
        Assert.Equal(8, requests);
        Assert.Equal(7, events.Count(evt => evt.Status == "planned"));
        Assert.DoesNotContain(events, evt => evt.Status == "escalated");
        Assert.DoesNotContain(conductEvents, evt => evt.Contains("SUPERVISOR_ESCALATED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task SevenUnproductiveRenewals_EscalateAtDefaultCap()
    {
        var (exitCode, requests, events, conductEvents) = await RunRenewals(landedGoals: 0);

        Assert.Equal(1, exitCode);
        Assert.Equal(7, requests);
        Assert.Equal("escalated", events[^1].Status);
        Assert.Contains("renewal-cap count=7 max=6", events[^1].Detail, StringComparison.Ordinal);
        Assert.Equal(["SUPERVISOR_ESCALATED reason=renewal-cap count=7 max=6"], conductEvents);
    }

    [Xunit.Fact]
    public async Task CrashesAfterLanding_StillEscalateAtRestartCap()
    {
        var host = new ScriptedHost((request, index) =>
        {
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("crashed", index, 0, RestartRequested: false, LandedGoals: 1));
            return new ConductorSupervisorProcessResult(2, 500 + index);
        });
        var events = new RecordingEvents();
        var conductEvents = new List<string>();
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var supervisor = new ConductorContinuitySupervisor(host, events, time,
            (delay, _) => { time.AdvanceForTests(delay); return Task.CompletedTask; },
            maxUnexpectedRestarts: 2,
            restartWindow: TimeSpan.FromMinutes(5),
            appendConductEvent: (_, _, detail) => conductEvents.Add(detail));

        var exitCode = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            TempOutputDirectory(), "default", "default");

        Assert.Equal(1, exitCode);
        Assert.Equal(3, host.Requests);
        Assert.Contains("restart-cap count=3 max=2", events.Events[^1].Detail, StringComparison.Ordinal);
        Assert.Equal(["SUPERVISOR_ESCALATED reason=restart-cap count=3 max=2"], conductEvents);
    }

    private static async Task<(int ExitCode, int Requests, List<RunEventAppend> Events, List<string> ConductEvents)>
        RunRenewals(int landedGoals)
    {
        var host = new ScriptedHost((request, index) =>
        {
            var restart = index < 7;
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact(
                    restart ? "self-relaunch-handoff" : "stop-file", index, 0,
                    RestartRequested: restart, LandedGoals: landedGoals));
            return new ConductorSupervisorProcessResult(0, 300 + index);
        });
        var events = new RecordingEvents();
        var conductEvents = new List<string>();
        var supervisor = new ConductorContinuitySupervisor(host, events,
            appendConductEvent: (_, _, detail) => conductEvents.Add(detail));

        var exitCode = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            TempOutputDirectory(), "default", "default");
        return (exitCode, host.Requests, events.Events, conductEvents);
    }

    private static string TempOutputDirectory() =>
        Path.Combine(Path.GetTempPath(), $"mcg-continuity-progress-{Guid.NewGuid():N}");

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, ConductorSupervisorProcessResult> step)
        : IConductorSupervisorProcessHost
    {
        public int Requests { get; private set; }

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(step(request, Requests++));
        }
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(evt);
            return Task.FromResult(new RunEventRecord(Events.Count,
                evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
