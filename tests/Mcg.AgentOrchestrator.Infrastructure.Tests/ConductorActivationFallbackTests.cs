using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorActivationFallbackTests
{
    [Xunit.Fact]
    public async Task FailedSuccessorRestoresPriorStagedBuildAndSuppressesSameHead()
    {
        var events = new RecordingRunEventStore();
        using var priorLease = new MemoryStream();
        using var failedLease = new MemoryStream();
        using var suppressedLease = new MemoryStream();
        var prior = Build("head-0", "prior", priorLease);
        var failed = Build("head-1", "failed", failedLease);
        var suppressed = Build("head-1", "failed-again", suppressedLease);
        var stages = new Queue<ConductorPreparedSuccessor>([prior, failed, suppressed]);
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index is 1 or 2 or 3)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                var ticks = index == 2 ? 1 : 3;
                for (var tick = 1; tick <= ticks; tick++)
                {
                    request.OnStandardOutputLine($"TICK_END tick={tick}");
                }
            }

            if (index == 2)
            {
                return Task.FromResult(new ConductorSupervisorProcessResult(-1, 102));
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact(index == 4 ? "stop-file" : "max-duration",
                    3, 1, RestartRequested: index != 4));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => stages.Dequeue(), activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"));

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-{Guid.NewGuid():N}"), "default", "default");

        Assert.Equal(0, result);
        Assert.Equal(5, host.Requests.Count);
        Assert.Equal(prior.AppDllPath, host.Requests[3].CommandPrefix![1]);
        Assert.Equal(prior.AppDllPath, host.Requests[4].CommandPrefix![1]);
        Assert.DoesNotContain(host.Requests.Skip(3), request =>
            request.CommandPrefix is { Count: > 1 } && request.CommandPrefix[1] == failed.AppDllPath);
        var reverted = Assert.Single(events.Events.Where(evt => evt.Operation == "activation" && evt.Status == "reverted"));
        using var payload = JsonDocument.Parse(reverted.PayloadJson!);
        Assert.Equal("head-1", payload.RootElement.GetProperty("failedBuild").GetProperty("commitSha").GetString());
        Assert.Equal("head-0", payload.RootElement.GetProperty("restoredBuild").GetProperty("commitSha").GetString());
        Assert.Contains("reason=SuccessorExited", reverted.Detail, StringComparison.Ordinal);
        Assert.Contains(events.Events, evt => evt.Operation == "activation" && evt.Status == "suppressed");
        Assert.Throws<ObjectDisposedException>(() => failedLease.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => suppressedLease.ReadByte());
    }

    [Xunit.Fact]
    public async Task RestoredBuildFailureEscalatesWithoutAlternating()
    {
        var events = new RecordingRunEventStore();
        var attention = new List<string>();
        var prepared = Build("bad-head", "bad", new MemoryStream());
        var host = new ScriptedHost((request, index, _) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 200));
            }

            if (index == 1)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
            }

            return Task.FromResult(new ConductorSupervisorProcessResult(-1, 200 + index));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => prepared, activationHealthyTicks: 3,
            activationDelay: (_, _) => throw new InvalidOperationException("No deadline should be needed"),
            raiseActivationAttention: attention.Add);

        var result = await supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-both-{Guid.NewGuid():N}"), "default", "default");

        Assert.Equal(1, result);
        Assert.Equal(3, host.Requests.Count);
        Assert.Contains(events.Events, evt => evt.Operation == "activation" && evt.Status == "failed-both");
        Assert.Contains("bad-head", Assert.Single(attention), StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, "ReadinessNeverReported")]
    [Xunit.InlineData(true, "TickStall")]
    public async Task DeadlineBeforeHealthyThresholdRestoresPriorBuild(
        bool startsLoop, string expectedReason)
    {
        var events = new RecordingRunEventStore();
        var deadlineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = new MemoryStream();
        var prepared = Build("head-timeout", "timed-out", lease);
        var host = new ScriptedHost((request, index, cancellationToken) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 300));
            }

            if (index == 1)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                if (startsLoop)
                {
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    request.OnStandardOutputLine("TICK_END tick=1");
                }

                var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => stopped.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 301, TerminationConfirmed: true)));
                return stopped.Task;
            }

            Assert.Null(request.CommandPrefix);
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 302));
        });
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => prepared,
            activationDelay: (_, _) =>
            {
                deadlineCreated.TrySetResult();
                return releaseDeadline.Task;
            });

        var run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-deadline-{Guid.NewGuid():N}"),
            "default", "default");
        await deadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseDeadline.TrySetResult();
        var result = await run.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(3, host.Requests.Count);
        var reverted = Assert.Single(events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Contains($"reason={expectedReason}", reverted.Detail, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => lease.ReadByte());
    }

    private static ConductorPreparedSuccessor Build(string head, string name, IDisposable lease) =>
        new($"C:\\{name}", $"C:\\{name}\\Mcg.AgentOrchestrator.App.dll", head, head,
            "LOOP_START selfCheck=true", lease);

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, cancellationToken);
        }
    }

    private sealed class RecordingRunEventStore : IRunEventStore
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
