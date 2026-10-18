using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = RemoteExecutorSlotArbiterTests.Fixture;
using Scenario = RemoteTypedSlotClaimsScenarioTests.JobAccountingScenario;
using static RemoteTypedSlotClaimsScenarioTests;

// Parallel safe: manual clocks, private host roots, event-driven fake queue state.
public sealed class RemoteShadowPreemptionTests
{
    [Fact]
    public void PreemptedRows_DoNotCountAsExecutorAttempts()
    {
        using var f = new Fixture();
        var row = new Mcg.AgentOrchestrator.Core.RemoteExecutorOutcomeRow(f.Clock.GetUtcNow(), "one", "shadow", "lane", "preempted-by-gate", null);
        var report = Mcg.AgentOrchestrator.Core.RemoteExecutorReport.Build(["one"], [row], []);
        var executor = Assert.Single(report.Executors);
        Assert.Equal(0, executor.Attempts);
        Assert.Empty(executor.OutcomeCounts);
        Assert.Empty(executor.RecentAttempts);
    }

    [Fact]
    public async Task GateWait_OneRequestForTwoShadowSlots_ClaimsReleasedExecutor()
    {
        using var f = new Fixture();
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow, slots: 2);
        Assert.NotNull(shadow.TryClaimIdleExecutor()); Assert.NotNull(shadow.TryClaimIdleExecutor());
        using var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate, slots: 2);
        var pending = gate.ClaimForGateAsync(Fixture.Lane(), default);
        Assert.Single(Directory.GetFiles(f.Folder, "*.preempt"));
        Assert.False(pending.IsCompleted);
        shadow.Release("one");
        var claimed = await Signal(pending, "gate claims released shadow slot");
        Assert.Equal("one", claimed?.Id);
        var row = Assert.Single(f.Health());
        Assert.Equal(RemoteLaneOutcomeCode.ShadowPreemptRequested, row.Outcome);
        Assert.Equal("", row.ExecutorId); Assert.Null(row.FaultOwner);
        Assert.Contains("shadow_attempt=shadow executor=one released=true waited_ms=", row.Reason);
        Assert.Empty(Directory.GetFiles(f.Folder, "*.preempt*"));
        Assert.Empty(Directory.GetFiles(f.Folder, "*.demand"));
    }

    [Fact]
    public async Task GateTimeout_RecordsMissAndDemand_AndDoesNotRetryHungPair()
    {
        using var f = new Fixture();
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow);
        Assert.NotNull(shadow.TryClaimIdleExecutor());
        var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate);
        using (gate)
        {
            var pending = gate.ClaimForGateAsync(Fixture.Lane(), default);
            Assert.Single(Directory.GetFiles(f.Folder, "*.preempt"));
            f.Clock.Advance(TimeSpan.FromSeconds(46));
            Assert.Null(await Signal(pending, "45-second injected-clock timeout"));
            var rows = f.Health();
            Assert.Equal(new[] { RemoteLaneOutcomeCode.ShadowPreemptRequested, RemoteLaneOutcomeCode.OfferSkippedNoExecutor }, rows.Select(row => row.Outcome));
            Assert.Contains("released=false waited_ms=46000", rows[0].Reason);
            Assert.Equal("Gate", Assert.Single(f.Demands()));
            Assert.Empty(Directory.GetFiles(f.Folder, "*.preempt*"));
            Assert.Null(await Signal(gate.ClaimForGateAsync(Fixture.Lane("B"), default), "hung pair skipped on second lane"));
            Assert.Single(f.Health(), row => row.Outcome == RemoteLaneOutcomeCode.ShadowPreemptRequested);
            shadow.Release("one");
            Assert.NotNull(await gate.ClaimForGateAsync(Fixture.Lane("C"), default));
            Assert.Empty(f.Demands());
        }
        Assert.Empty(Directory.GetFiles(f.Folder, "*.demand"));
    }

    [Fact]
    public async Task GateCancellation_RecordsElapsedAndRemovesOwnedRequest()
    {
        using var f = new Fixture();
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow);
        Assert.NotNull(shadow.TryClaimIdleExecutor());
        using var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate);
        using var cancellation = new CancellationTokenSource();
        var pending = gate.ClaimForGateAsync(Fixture.Lane(), cancellation.Token);
        Assert.Single(Directory.GetFiles(f.Folder, "*.preempt"));
        f.Clock.Advance(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Contains("released=false waited_ms=3000", Assert.Single(f.Health()).Reason);
        Assert.Empty(Directory.GetFiles(f.Folder, "*.preempt*"));
        Assert.Empty(Directory.GetFiles(f.Folder, "*.demand"));
    }

    [Fact]
    public async Task OnlyGate_CanRequestPreemption()
    {
        using var f = new Fixture();
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shadow.ClaimForGateAsync(Fixture.Lane(), default));
        Assert.False(Directory.Exists(f.Folder));
    }

    [Fact]
    public async Task OutstandingRequest_IsReused_AndTakenByOnlyOneShadowRun()
    {
        using var f = new Fixture();
        using var shadow = f.Coordinator("shadow", RemoteExecutorClaimClass.Shadow, slots: 2);
        Assert.NotNull(shadow.TryClaimIdleExecutor()); Assert.NotNull(shadow.TryClaimIdleExecutor());
        using var arbiter = f.Arbiter("other-gate", RemoteExecutorClaimClass.Gate);
        Assert.NotNull(arbiter.RequestPreemption(["one"], "other lane"));
        using var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate, slots: 2);
        var pending = gate.ClaimForGateAsync(Fixture.Lane(), default);
        Assert.Single(Directory.GetFiles(f.Folder, "*.preempt"));
        Assert.True(shadow.TryTakePreemption("one"));
        Assert.False(shadow.TryTakePreemption("one"));
        using var newcomer = f.Coordinator("new-shadow", RemoteExecutorClaimClass.Shadow, slots: 2);
        using (var transition = new FileStream(Path.Combine(f.Folder, "arbiter.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(newcomer.TryClaimIdleExecutor(out var reason)); Assert.Equal("yield-to-gate", reason);
        }
        shadow.CompletePreemption("one");
        shadow.Release("one");
        Assert.NotNull(await Signal(pending, "gate benefits from existing request"));
        Assert.Contains("shadow_attempt=shadow", Assert.Single(f.Health()).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShadowPreemption_WaitsForQueueOrInjectedDeadline(bool timeout)
    {
        await using var s = new Scenario();
        s.Executor.HoldQueue = true;
        s.Executor.QueueAnswer = timeout;
        var shadow = s.StartShadow(count: 1);
        await Signal(s.Executor.FirstShadowSubmitted.Task, "shadow submission before request");
        using var gate = new RemoteExecutorSlotArbiter(s.Root, "gate", RemoteExecutorClaimClass.Gate, s.Clock);
        Assert.NotNull(gate.RequestPreemption(["one"], "gate lane"));
        await Signal(s.Executor.QueueRead.Task, "shadow cancellation followed by queue probe");
        var handle = Assert.Single(s.Executor.ShadowHandles);
        Assert.Equal(1, handle.Cancellations);
        Assert.True(RemoteExecutorOccupancy.IsOccupied(s.Root, "one"));
        Assert.Single(Directory.GetFiles(s.Folder, "*.preempt-taken"));
        if (timeout) s.Clock.Advance(TimeSpan.FromSeconds(31));
        s.Executor.QueueGone.TrySetResult();
        await Signal(handle.Abandoned.Task, "shadow releases after queue disappearance or deadline");
        await shadow.CompleteAsync(s.LocalShadowResults());
        Assert.Equal(1, handle.Cancellations);
        var row = Assert.Single(s.Health());
        Assert.Equal(RemoteLaneOutcomeCode.PreemptedByGate, row.Outcome);
        Assert.Null(row.FaultOwner); Assert.Null(row.Agree);
        Assert.False(RemoteExecutorOccupancy.IsOccupied(s.Root, "one"));
        s.AssertNoPriorityFiles();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SshQueueSource_SharesProbeReadAndBusyDecision(bool listed)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request;
        var configuration = new RemoteLaneExecutorConfiguration(
            [new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor", Slots: 1, OccupancyProbe: true)],
            [request.Lane], null);
        var probes = 0;
        Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            if (!args.Any(arg => arg.EndsWith("/runner-state.json", StringComparison.Ordinal)))
                return fake.Transport(args, directory, bound, token);
            Interlocked.Increment(ref probes);
            File.WriteAllText(Path.Combine(directory, "runner-state.json"), JsonSerializer.Serialize(new
            {
                utc = fake.Clock.GetUtcNow(), slots = 1,
                running = listed ? new[] { request.AttemptId + "-" + SshRemoteLaneExecutor.LaneKey(request.Lane) + ".json" } : Array.Empty<string>(),
                queued = Array.Empty<string>()
            }));
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        }
        var executor = new SshRemoteLaneExecutor(configuration, fake.Root, Path.Combine(fake.Root, "attempt", "result"),
            fake.Clock, Transport, fake.Git);
        Assert.Equal(listed, await executor.IsJobListedAsync(request, default));
        var submission = await executor.SubmitAsync(request, default);
        if (listed)
        {
            Assert.Null(submission.Handle); Assert.Equal("executor-busy", submission.FailureReason);
            Assert.Empty(fake.Calls);
        }
        else fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        Assert.Equal(2, probes);
        Assert.Equal("occupancy", submission.Steps![0].Name);
    }
}
