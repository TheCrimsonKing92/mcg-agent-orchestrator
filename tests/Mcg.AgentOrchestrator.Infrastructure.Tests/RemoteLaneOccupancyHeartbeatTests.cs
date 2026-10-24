using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using FakeTimeProvider = ManualRemoteLaneClock;

// Parallel-safe: each fixture owns its storage, executor claims, clock and poll gates.
public sealed class RemoteLaneOccupancyHeartbeatTests
{
    [Xunit.Fact]
    public void FirstBeat_WritesAttemptIdentity_AndLaneChangesBypassThrottle()
    {
        using var fixture = new RemoteRun();
        var now = fixture.Clock.GetUtcNow();
        var writer = fixture.Coordinator.Occupancy!;
        writer.End("never-started", now);
        Assert.Empty(fixture.Files());
        writer.Beat("Zulu", now.AddSeconds(-2), now);
        var path = Assert.Single(fixture.Files());
        Assert.StartsWith("activity-0.heartbeat-", Path.GetFileName(path));
        Assert.Equal(fixture.HeartbeatPath, path);
        var snapshot = fixture.Snapshot();
        Assert.Equal("running", snapshot.State);
        Assert.Equal("remote-lane-wait", snapshot.Phase);
        Assert.Equal("Zulu", snapshot.CurrentTarget);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.ChildPid);
        Assert.Equal(RemoteRun.Goal, snapshot.GoalId);
        Assert.Equal(GateHeartbeatRunClass.Acceptance, snapshot.RunClass);
        Assert.Equal(RemoteRun.RunId, snapshot.RunId);
        Assert.Equal(now, snapshot.StartedAt);
        Assert.Equal(now.AddSeconds(-2), snapshot.LastProgressAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        writer.Beat("Alpha", now, fixture.Clock.GetUtcNow());
        Assert.Equal("Alpha,Zulu", fixture.Snapshot().CurrentTarget);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Snapshot().LastObservedAt);
        Assert.Equal(now, fixture.Snapshot().LastProgressAt);
        writer.Beat("Zulu", now.AddSeconds(-10), fixture.Clock.GetUtcNow());
        writer.End("Alpha", fixture.Clock.GetUtcNow());
        Assert.Equal("Zulu", fixture.Snapshot().CurrentTarget);
        Assert.Equal("running", fixture.Snapshot().State);
        writer.End("Zulu", fixture.Clock.GetUtcNow());
        Assert.Equal("completed", fixture.Snapshot().State);
        writer.Beat("Later", now.AddSeconds(5), fixture.Clock.GetUtcNow());
        Assert.Equal("running", fixture.Snapshot().State);
        Assert.Equal(now, fixture.Snapshot().StartedAt);
    }

    [Xunit.Fact]
    public void Beat_PushedOwner_WritesOwnerAndAttemptIdentity()
    {
        using var fixture = new RemoteRun();
        var ownerDirectory = Path.Combine(Path.GetTempPath(), $"mcg-remote-owner-{Guid.NewGuid():N}");
        using var owner = GateHeartbeatOwner.Push(ownerDirectory);
        var now = fixture.Clock.GetUtcNow();

        fixture.Coordinator.Occupancy!.Beat("Zulu", now.AddSeconds(-2), now);

        var path = Assert.Single(fixture.Files());
        Assert.StartsWith("activity-0.heartbeat-", Path.GetFileName(path));
        Assert.Equal(fixture.HeartbeatPath, path);
        var snapshot = fixture.Snapshot();
        Assert.Equal(ownerDirectory, snapshot.OwnerStateDirectory);
        Assert.Equal("running", snapshot.State);
        Assert.Equal("remote-lane-wait", snapshot.Phase);
        Assert.Equal("Zulu", snapshot.CurrentTarget);
        Assert.Equal(Environment.ProcessId, snapshot.ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.ChildPid);
        Assert.Equal(RemoteRun.Goal, snapshot.GoalId);
        Assert.Equal(GateHeartbeatRunClass.Acceptance, snapshot.RunClass);
        Assert.Equal(RemoteRun.RunId, snapshot.RunId);
        Assert.Equal(now, snapshot.StartedAt);
        Assert.Equal(now, snapshot.LastObservedAt);
        Assert.Equal(now.AddSeconds(-2), snapshot.LastProgressAt);
    }

    [Xunit.Fact]
    public void RepeatedBeats_WriteOnlyAtFifteenSecondIntervals()
    {
        using var fixture = new RemoteRun();
        var start = fixture.Clock.GetUtcNow();
        for (var seconds = 0; seconds <= 30; seconds += 5)
        {
            if (seconds > 0) fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            fixture.Coordinator.Occupancy!.Beat("lane", fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow());
            Assert.Equal(start.AddSeconds(seconds / 15 * 15), fixture.Snapshot().LastObservedAt);
            Assert.Equal(start.AddSeconds(seconds / 15 * 15), fixture.Snapshot().LastProgressAt);
            Assert.Single(fixture.Files());
        }
        fixture.Coordinator.Occupancy!.End("lane", fixture.Clock.GetUtcNow());
        Assert.Equal("completed", fixture.Snapshot().State);
    }

    [Xunit.Theory]
    [Xunit.InlineData(-1)]
    [Xunit.InlineData(2)]
    public void InvalidSlot_WritesNothing(int slot)
    {
        using var fixture = new RemoteRun();
        var writer = new RemoteLaneOccupancyHeartbeat(RemoteRun.Goal, slot, fixture.Storage,
            GateHeartbeatRunClass.Acceptance, RemoteRun.RunId);
        writer.Beat("lane", fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow());
        writer.End("lane", fixture.Clock.GetUtcNow());
        Assert.Empty(fixture.Files());
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task FreshRemoteLease_KeepsHostLiveForSixMinutes_ThenCompletes()
    {
        await using var fixture = new RemoteRun();
        await fixture.StartAsync();
        for (var step = 0; step <= 12; step++)
        {
            if (step > 0) await fixture.AdvanceAsync(TimeSpan.FromSeconds(30));
            var status = Assert.Single(fixture.Running());
            Assert.Equal(RemoteRun.RunId, status.Snapshot!.RunId);
            Assert.Equal(fixture.Clock.GetUtcNow(), status.Snapshot.LastObservedAt);
        }
        await fixture.AcceptAsync();
        Assert.Equal("completed", fixture.Snapshot().State);
        Assert.Empty(fixture.Running());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(fixture.Rows()).Outcome);
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("lease")]
    [Xunit.InlineData("cancel")]
    [Xunit.InlineData("exception")]
    public async Task LaneExit_EndsHostOccupancy(string exit)
    {
        await using var fixture = new RemoteRun();
        await fixture.StartAsync();
        Assert.Single(fixture.Running());
        if (exit == "cancel") await fixture.CancelAsync();
        else if (exit == "lease")
        {
            await fixture.AdvanceAsync(TimeSpan.FromSeconds(61), renew: false, expectExit: true);
            Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, Assert.Single(fixture.Rows()).Outcome);
        }
        else
        {
            fixture.Handle.OnHeartbeatRead = () => throw new IOException("fixture-poll-failed");
            await fixture.AdvanceAsync(TimeSpan.Zero, expectExit: true);
            Assert.Equal(RemoteLaneOutcomeCode.Unreachable, Assert.Single(fixture.Rows()).Outcome);
        }
        Assert.Equal("completed", fixture.Snapshot().State);
        Assert.Empty(fixture.Running());
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task DisabledRemoteAndRefusal_PreserveFilesOutcomesAndHistory()
    {
        await using var baseline = new RemoteRun(bindOccupancy: false, remoteEnabled: false);
        await using var bound = new RemoteRun(remoteEnabled: false);
        foreach (var fixture in new[] { baseline, bound })
        {
            var before = fixture.Files();
            Assert.False(fixture.Coordinator.IsEligible(fixture.Check));
            // The same local-only script writes only its ordinary local heartbeat.
            fixture.WriteLocalHeartbeat();
            Assert.Empty(before);
            Assert.Single(fixture.Files());
            Assert.DoesNotContain(fixture.Files(), path => path == fixture.HeartbeatPath);
            Assert.Empty(fixture.Executor.Requests);
        }
        Assert.Equal(baseline.Files().Select(Path.GetFileName), bound.Files().Select(Path.GetFileName));
        Assert.Equal(baseline.Events, bound.Events);
        Assert.Equal(baseline.Outcomes, bound.Outcomes);
        Assert.Equal(baseline.Rows(), bound.Rows());

        // Refusal and expiry before the first poll also retain their original records.
        foreach (var reason in new[] { "executor-busy", "expired-before-poll" })
        {
            await using var original = new RemoteRun(bindOccupancy: false);
            await using var instrumented = new RemoteRun();
            foreach (var fixture in new[] { original, instrumented })
            {
                fixture.Executor.Submit = (_, _) =>
                {
                    if (reason == "executor-busy") return Task.FromResult(new RemoteLaneSubmission(null, reason));
                    fixture.Clock.Advance(TimeSpan.FromSeconds(61));
                    return Task.FromResult(new RemoteLaneSubmission(fixture.Handle));
                };
                var outcome = await fixture.RunToExitAsync();
                Assert.Null(outcome.Accepted);
                Assert.Equal(reason == "executor-busy" ? null : fixture.Handle, outcome.Handle);
                Assert.Empty(fixture.Files());
                Assert.Single(fixture.Executor.Requests);
                Assert.Single(fixture.Rows());
            }
            Assert.Equal(original.Events, instrumented.Events);
            Assert.Equal(original.Outcomes, instrumented.Outcomes);
            Assert.Equal(JsonSerializer.Serialize(original.Rows()), JsonSerializer.Serialize(instrumented.Rows()));
        }
    }

    [Xunit.Fact]
    public void ParameterlessProbe_DelegatesWithTheSameOverrideAndFreshnessFilter()
    {
        var clock = new FakeTimeProvider();
        GateLoadContextProbe.LiveGateOccupant[] occupants =
        [
            new(123, "fresh", 0, TimeSpan.FromSeconds(30), RunClass: GateHeartbeatRunClass.Acceptance),
            new(124, "stale", 1, TimeSpan.FromMinutes(3), RunClass: GateHeartbeatRunClass.Acceptance)
        ];
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => occupants);
        var expected = GateLoadContextProbe.CaptureLiveGateOccupants(
            DotnetBuildEnvironmentManager.CaptureStorageRoot(), clock.GetUtcNow());
        Assert.Equal(occupants.Take(1), expected);
        Assert.Equal(expected, GateLoadContextProbe.CaptureLiveGateOccupants());
    }

    internal sealed class RemoteRun : IDisposable, IAsyncDisposable
    {
        internal const string Goal = "remote-tail-goal";
        internal const string RunId = "train:remote-tail-goal+peer";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-remote-occupancy-" + Guid.NewGuid().ToString("N"));
        internal FakeTimeProvider Clock { get; } = new();
        internal DotnetBuildStorageRoot Storage { get; }
        internal RemoteLaneCoordinator Coordinator { get; }
        internal FakeRemoteLaneExecutor Executor { get; } = new();
        internal FakeRemoteLaneExecutor.Handle Handle { get; }
        internal List<string> Events { get; } = [];
        internal List<(string Lane, RemoteLaneOutcomeCode Code)> Outcomes { get; } = [];
        internal Check Check { get; } = new()
        {
            Name = "infrastructure tests: RemoteTail", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~RemoteTail"]
        };
        private readonly RemoteLaneExecutorEntry _entry = new("executor", 60);
        private readonly SemaphoreSlim _polled = new(0);
        private readonly SemaphoreSlim _continue = new(0);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private Task<RemoteLaneOutcome>? _run;
        internal string HeartbeatPath => GateHeartbeatArtifacts.GetRunScopedStableSlotPath(0, RunId + "|remote-lane-wait", Storage);
        internal RemoteRun(bool bindOccupancy = true, bool remoteEnabled = true)
        {
            Directory.CreateDirectory(Root);
            Storage = new(Path.Combine(Root, "storage"));
            Handle = new(Clock.GetUtcNow());
            // Poll signal follows Beat; holding TryGetResult makes the next decision explicit.
            Handle.OnPolled = () => { _polled.Release(); _continue.Wait(_stop.Token); };
            Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(Handle));
            Coordinator = new(new([_entry], remoteEnabled ? [Check.Name] : [], null),
                new RemoteLaneCandidateIdentity("attempt", Goal, "commit", "tree", "main", "manifest"),
                Root, Path.Combine(Root, "receipts", "attempt"), Executor, Clock, TimeSpan.FromMilliseconds(1),
                (lane, code) => Outcomes.Add((lane, code)), line => Events.Add(line));
            if (bindOccupancy) Coordinator.Occupancy = new(Goal, 0, Storage, GateHeartbeatRunClass.Acceptance, RunId);
        }
        internal async Task StartAsync()
        {
            Assert.True(Coordinator.IsEligible(Check));
            Assert.Equal(_entry, Coordinator.TryClaimIdleExecutor());
            _run = Task.Run(() => Coordinator.RunRemoteAsync(Check, _entry, TimeSpan.FromHours(1), _stop.Token));
            await AwaitPollAsync();
        }
        private async Task AwaitPollAsync()
        {
            var polled = _polled.WaitAsync(_stop.Token);
            await Task.WhenAny(polled, _run!).WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(_run!.IsCompleted, "Remote lane exited before the scripted poll signal.");
            await polled;
        }
        internal async Task AdvanceAsync(TimeSpan elapsed, bool renew = true, bool expectExit = false)
        {
            Clock.Advance(elapsed);
            if (renew) Handle.Heartbeat(Clock.GetUtcNow());
            _continue.Release();
            if (expectExit)
            {
                // Removing the lease check must fail on the next poll, rather than hang.
                var polledAgain = _polled.WaitAsync(_stop.Token);
                var completed = await Task.WhenAny(_run!, polledAgain).WaitAsync(TestContext.Current.CancellationToken);
                Assert.True(completed == _run, "Remote lane polled again instead of taking the scripted exit.");
                await _run!;
            }
            else await AwaitPollAsync();
        }
        internal async Task AcceptAsync()
        {
            var request = Assert.Single(Executor.Requests);
            var trx = Path.Combine(Root, "passed.trx");
            File.WriteAllText(trx, """
                <TestRun><Results><UnitTestResult testId="one" testName="RemoteTail.Passes" outcome="Passed" /></Results>
                <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
                """);
            Handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [trx]));
            _continue.Release();
            Assert.NotNull((await _run!.WaitAsync(TestContext.Current.CancellationToken)).Accepted);
        }
        internal async Task CancelAsync()
        {
            _stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _run!);
        }
        internal Task<RemoteLaneOutcome> RunToExitAsync() => Coordinator.RunRemoteAsync(
            Check, _entry, TimeSpan.FromHours(1), _stop.Token);
        internal GateHeartbeatSnapshot Snapshot() => JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
            File.ReadAllText(HeartbeatPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        internal GateHeartbeatStatus[] Running() => GateHeartbeatArtifacts.ReadStableSlots(Storage, Clock.GetUtcNow())
            .Where(status => status.Snapshot is { State: "running", RunId: RunId }).ToArray();
        internal string[] Files()
        {
            var folder = Path.GetDirectoryName(HeartbeatPath)!;
            return Directory.Exists(folder) ? Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToArray() : [];
        }
        internal IReadOnlyList<RemoteExecutorHealthRecord> Rows() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));
        internal void WriteLocalHeartbeat() => GateHeartbeatArtifacts.TryWrite(
            GateHeartbeatArtifacts.GetStableSlotPath(0, Storage), new(Goal, "local", "local-only", 0,
                Environment.ProcessId, Environment.ProcessId, "completed", Clock.GetUtcNow(), Clock.GetUtcNow(),
                Clock.GetUtcNow(), 0, 0, 0));
        public void Dispose()
        {
            Coordinator.Dispose();
            _stop.Dispose();
            _polled.Dispose();
            _continue.Dispose();
            Directory.Delete(Root, recursive: true);
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_run is not null)
                try { await _run; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            Dispose();
        }
    }
}
