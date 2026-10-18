using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorSuccessorLockRetryTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task FailedSuccessorWaitsThenRetriesSameBuildAndAdopts()
    {
        using var fixture = new Fixture();
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lease = new MemoryStream();
        var successor = fixture.Build(lease);
        var host = new ScriptedHost((request, index, _) =>
        {
            Assert.InRange(index, 0, 2);
            if (index == 0) return Restart(request, index);
            if (index == 1) return Exit(1, index);
            Assert.True(lease.CanRead);
            ReadyAndHealthy(request);
            return Stop(request, index);
        });
        var supervisor = fixture.Supervisor(host, _ => successor, retry: true,
            retryDelay: async token =>
            {
                delayEntered.TrySetResult();
                await releaseDelay.Task.WaitAsync(token);
            });
        var run = fixture.Run(supervisor);
        await delayEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, host.Requests.Count);
        Assert.True(lease.CanRead); // The staged build remains leased throughout the retry delay.
        Assert.Single(fixture.ConductEvents, detail => detail.Contains("LOOP_HANDOFF_RETRY_SCHEDULED"));
        releaseDelay.SetResult();
        Assert.Equal(0, await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, host.Requests.Count);
        Assert.Equal(successor.AppDllPath, host.Requests[1].CommandPrefix![1]);
        Assert.Equal(host.Requests[1].CommandPrefix, host.Requests[2].CommandPrefix);
        Assert.Single(fixture.Delays, delay => delay == TimeSpan.FromSeconds(60));
        var scheduled = Assert.Single(fixture.ConductEvents, detail => detail.Contains("LOOP_HANDOFF_RETRY_SCHEDULED"));
        Assert.Contains("head=locked-head delaySeconds=60 reason=successor-exited-before-readiness exit=1", scheduled);
        Assert.Single(fixture.Events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted");
        Assert.DoesNotContain(fixture.Events.Events, evt => evt.Status == "reverted");
        Assert.False(lease.CanRead);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task SecondFailureRestoresIncumbentAndSuppressesFurtherSameHeadSuccessor()
    {
        using var fixture = new Fixture();
        using var lease = new MemoryStream();
        using var suppressedLease = new MemoryStream();
        var successor = fixture.Build(lease);
        var host = new ScriptedHost((request, index, _) =>
        {
            Assert.InRange(index, 0, 4);
            if (index == 0) return Restart(request, index);
            if (index is 1 or 2)
            {
                Assert.True(lease.CanRead);
                return Exit(1, index);
            }
            Assert.Null(request.CommandPrefix);
            Assert.False(lease.CanRead);
            ReadyAndHealthy(request);
            return index == 3 ? Restart(request, index) : Stop(request, index);
        });
        var stages = 0;
        var supervisor = fixture.Supervisor(host, _ =>
        {
            if (++stages == 1) return successor;
            // Same-head suppression happens after restoration; the normal incumbent restart still runs.
            Assert.Equal(4, host.Requests.Count);
            Assert.Null(host.Requests[3].CommandPrefix);
            return fixture.Build(suppressedLease);
        }, retry: true);
        Assert.Equal(0, await fixture.Run(supervisor));
        Assert.Equal(2, stages);
        Assert.Equal(5, host.Requests.Count);
        Assert.All(host.Requests.Skip(3), request => Assert.Null(request.CommandPrefix));
        Assert.Single(fixture.Delays, delay => delay == TimeSpan.FromSeconds(60));
        Assert.Single(fixture.ConductEvents, detail => detail.Contains("LOOP_HANDOFF_RETRY_SCHEDULED"));
        var reverted = Assert.Single(fixture.Events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
        Assert.Contains("reason=SuccessorExited", reverted.Detail);
        Assert.Single(fixture.Events.Events, evt => evt.Operation == "activation" && evt.Status == "suppressed");
        Assert.False(suppressedLease.CanRead);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData("deliberate-stop")]
    [InlineData("launch-exception")]
    [InlineData("readiness-timeout")]
    [InlineData("disabled")]
    public async Task OtherStartupOutcomesKeepExistingBehaviorWithoutRetry(string outcome)
    {
        using var fixture = new Fixture();
        using var lease = new MemoryStream();
        var host = new ScriptedHost((request, index, token) =>
        {
            Assert.InRange(index, 0, 2);
            if (index == 0) return Restart(request, index);
            if (index == 2)
            {
                Assert.Null(request.CommandPrefix);
                Assert.False(lease.CanRead);
                return Stop(request, index);
            }
            return outcome switch
            {
                "deliberate-stop" => Stop(request, index),
                "launch-exception" => throw new IOException("scripted launch failure"),
                "readiness-timeout" => ExitWhenCancelled(index, token),
                _ => Exit(1, index)
            };
        });
        var supervisor = fixture.Supervisor(host, _ => fixture.Build(lease), retry: outcome != "disabled",
            readinessTimesOut: outcome == "readiness-timeout");
        Assert.Equal(0, await fixture.Run(supervisor));
        Assert.DoesNotContain(fixture.Delays, delay => delay == TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(fixture.ConductEvents, detail => detail.Contains("LOOP_HANDOFF_RETRY_SCHEDULED"));
        if (outcome == "deliberate-stop")
        {
            Assert.Equal(2, host.Requests.Count);
            Assert.DoesNotContain(fixture.Events.Events, evt => evt.Status == "reverted");
        }
        else
        {
            Assert.Equal(3, host.Requests.Count);
            var reverted = Assert.Single(fixture.Events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
            Assert.Contains(outcome == "readiness-timeout" ? "reason=ReadinessNeverReported" : "reason=SuccessorExited", reverted.Detail);
        }
    }

    private static async Task<ConductorSupervisorProcessResult> ExitWhenCancelled(int index, CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return new(1, 100 + index, TerminationConfirmed: true);
    }

    private static void ReadyAndHealthy(ConductorSupervisorProcessRequest request)
    {
        request.OnStandardOutputLine!("LOOP_READY lock=acquired");
        request.OnStandardOutputLine("LOOP_START tick=0");
        for (var tick = 1; tick <= 3; tick++) request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
    }

    private static Task<ConductorSupervisorProcessResult> Restart(ConductorSupervisorProcessRequest request, int index)
    {
        ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 3, 1, true));
        return Exit(0, index);
    }

    private static Task<ConductorSupervisorProcessResult> Stop(ConductorSupervisorProcessRequest request, int index)
    {
        ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 3, 1, false));
        return Exit(0, index);
    }

    private static Task<ConductorSupervisorProcessResult> Exit(int code, int index) =>
        Task.FromResult(new ConductorSupervisorProcessResult(code, 100 + index));

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("successor-lock-retry-").FullName;
        public RecordingRunEventStore Events { get; } = new();
        public List<string> ConductEvents { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public ConductorPreparedSuccessor Build(IDisposable lease) =>
            new(_root, Path.Combine(_root, "App.dll"), "locked-head", "locked-head", "LOOP_START selfCheck=true", lease);

        public ConductorContinuitySupervisor Supervisor(ScriptedHost host,
            Func<CancellationToken, ConductorPreparedSuccessor> stage, bool retry,
            Func<CancellationToken, Task>? retryDelay = null, bool readinessTimesOut = false) =>
            new(host, Events, stageSuccessor: stage,
                inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
                appendConductEvent: (_, _, detail) => ConductEvents.Add(detail),
                readinessTimeout: TimeSpan.FromMinutes(5), activationHealthyTicks: 3,
                activationDelay: (_, _) => throw new InvalidOperationException("Healthy scripted output requires no activation deadline"),
                successorExitRetryDelay: retry ? TimeSpan.FromSeconds(60) : null,
                delay: (duration, token) =>
                {
                    Delays.Add(duration);
                    if (duration == TimeSpan.FromSeconds(60)) return retryDelay?.Invoke(token) ?? Task.CompletedTask;
                    if (readinessTimesOut && duration == TimeSpan.FromMinutes(5)) return Task.CompletedTask;
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                });

        public Task<int> Run(ConductorContinuitySupervisor supervisor) => supervisor.RunAsync(
            ["conduct", "--loop"], _root, Path.Combine(_root, "continuity"), "default", "default",
            cancellationToken: TestContext.Current.CancellationToken);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
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
