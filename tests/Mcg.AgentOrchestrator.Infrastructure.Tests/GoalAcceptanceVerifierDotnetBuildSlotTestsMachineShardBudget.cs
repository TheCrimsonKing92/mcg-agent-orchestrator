using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Collections.Concurrent;
using System.Globalization;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsMachineShardBudget : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task TwoGatesShareSixPermitsAcrossEightLanes()
    {
        var workspace = CreateShardWorkspace(4);
        var secondWorkspace = CreateShardWorkspace(4);
        var permitRoot = Path.Combine(Path.GetTempPath(), "shard-budget-" + Guid.NewGuid().ToString("N"));
        var releases = new ConcurrentQueue<TaskCompletionSource>();
        var events = new ConcurrentQueue<(long Sequence, string Kind)>();
        var waiters = new ConcurrentDictionary<string, byte>();
        var sixStarted = Signal();
        var twoWaiting = Signal();
        var seventhStarted = Signal();
        var eighthStarted = Signal();
        var firstReleased = Signal();
        var secondReleased = Signal();
        var active = 0;
        var peak = 0;
        var starts = 0;
        var sequence = 0L;
        var releaseCount = 0;
        var draining = false;

        GoalAcceptanceVerifierTestOverrides Overrides(string gate)
        {
            return new GoalAcceptanceVerifierTestOverrides
            {
                ResolveShardCoreBudgetForTests = () => 4,
                ResolveGateShardBudgetForTests = () => 6,
                ShardPermitRootForTests = permitRoot,
                ShardPermitPollInterval = TimeSpan.FromMilliseconds(10),
                OnShardPermitWaitingForTests = lane =>
                {
                    if (waiters.TryAdd(gate + lane, 0) && waiters.Count == 2)
                        twoWaiting.TrySetResult();
                    events.Enqueue((Interlocked.Increment(ref sequence), "wait"));
                },
                OnShardPermitAcquiredForTests = _ =>
                    events.Enqueue((Interlocked.Increment(ref sequence), "acquire")),
                OnShardPermitReleasedForTests = _ =>
                {
                    events.Enqueue((Interlocked.Increment(ref sequence), "permit-release"));
                    var count = Interlocked.Increment(ref releaseCount);
                    if (count == 1) firstReleased.TrySetResult();
                    if (count == 2) secondReleased.TrySetResult();
                }
            };
        }

        async Task<GoalAcceptanceVerifier.CommandResult> RunLane(
            string[] args, string _, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!IsTestCommand(args)) return Passed();
            var now = Interlocked.Increment(ref active);
            var observed = Volatile.Read(ref peak);
            while (now > observed)
            {
                var previous = Interlocked.CompareExchange(ref peak, now, observed);
                if (previous == observed) break;
                observed = previous;
            }
            var started = Interlocked.Increment(ref starts);
            events.Enqueue((Interlocked.Increment(ref sequence), "start"));
            if (started == 6) sixStarted.TrySetResult();
            if (started == 7) seventhStarted.TrySetResult();
            if (started == 8) eighthStarted.TrySetResult();
            var release = Signal();
            releases.Enqueue(release);
            try
            {
                if (!Volatile.Read(ref draining))
                    await release.Task.WaitAsync(cancellationToken);
                return Passed();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var first = new GoalAcceptanceVerifier(Overrides("first"), RunLane);
        var second = new GoalAcceptanceVerifier(Overrides("second"), RunLane);
        Task<AcceptanceVerificationResult>? firstRun = null;
        Task<AcceptanceVerificationResult>? secondRun = null;
        try
        {
            firstRun = first.RunAsync(workspace);
            secondRun = second.RunAsync(secondWorkspace);
            await Task.WhenAll(sixStarted.Task, twoWaiting.Task).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(6, Volatile.Read(ref starts));
            Assert.Equal(6, Volatile.Read(ref active));
            Assert.Equal(6, Volatile.Read(ref peak));
            Assert.Equal(2, waiters.Count);
            Assert.True(releases.TryDequeue(out var firstRelease));
            events.Enqueue((Interlocked.Increment(ref sequence), "release"));
            firstRelease.TrySetResult();
            await Task.WhenAll(firstReleased.Task, seventhStarted.Task).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(7, Volatile.Read(ref starts));
            Assert.True(releases.TryDequeue(out var secondRelease));
            events.Enqueue((Interlocked.Increment(ref sequence), "release"));
            secondRelease.TrySetResult();
            await Task.WhenAll(secondReleased.Task, eighthStarted.Task).WaitAsync(TimeSpan.FromSeconds(20));
            var ordered = events.OrderBy(item => item.Sequence).ToArray();
            Assert.Equal(8, ordered.Count(item => item.Kind == "acquire"));
            Assert.Equal(8, ordered.Count(item => item.Kind == "start"));
            var startsInOrder = ordered.Where(item => item.Kind == "start").ToArray();
            var releasesInOrder = ordered.Where(item => item.Kind == "release").ToArray();
            Assert.Equal(2, releasesInOrder.Length);
            Assert.True(startsInOrder[5].Sequence < releasesInOrder[0].Sequence);
            Assert.True(releasesInOrder[0].Sequence < startsInOrder[6].Sequence);
            Assert.True(releasesInOrder[1].Sequence < startsInOrder[7].Sequence);
            Assert.True(Volatile.Read(ref peak) <= 6);
        }
        finally
        {
            Volatile.Write(ref draining, true);
            while (releases.TryDequeue(out var release)) release.TrySetResult();
            if (firstRun is not null && secondRun is not null)
                await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(20));
            DeleteDirectoryWithRetry(workspace);
            DeleteDirectoryWithRetry(secondWorkspace);
            if (Directory.Exists(permitRoot)) Directory.Delete(permitRoot, recursive: true);
        }
        Assert.True(firstRun!.Result.Passed, firstRun.Result.OutputTail);
        Assert.True(secondRun!.Result.Passed, secondRun.Result.OutputTail);
    }

    [Fact]
    public async Task WaitingLaneGetsFullTimeoutAndSeparateBreakdownField()
    {
        var workspace = CreateShardWorkspace(1);
        var permitRoot = Path.Combine(Path.GetTempPath(), "shard-budget-" + Guid.NewGuid().ToString("N"));
        var pool = GateShardPermitPool.ForRoot(permitRoot, 1);
        Assert.True(pool.TryAcquire(out var held));
        var waiting = Signal();
        var acquired = Signal();
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
        var launches = 0;
        var receivedTimeout = TimeSpan.Zero;
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            ResolveShardCoreBudgetForTests = () => 1,
            ResolveGateShardBudgetForTests = () => 1,
            ShardPermitRootForTests = permitRoot,
            ShardPermitPollInterval = TimeSpan.FromMilliseconds(10),
            OnShardPermitWaitingForTests = _ => waiting.TrySetResult(),
            OnShardPermitAcquiredForTests = _ => acquired.TrySetResult()
        };
        Task<AcceptanceVerificationResult>? run = null;
        try
        {
            var verifier = new GoalAcceptanceVerifier(overrides, (
                string[] args, string _worktree, TimeSpan timeout, CancellationToken _ct) =>
            {
                if (IsTestCommand(args))
                {
                    Interlocked.Increment(ref launches);
                    receivedTimeout = timeout;
                }
                return Task.FromResult(Passed());
            });
            run = verifier.RunOwnedAsync(workspace, null, null, null, null, CancellationToken.None,
                new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, Volatile.Read(ref launches));
            held!.Dispose();
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(result.Passed, result.OutputTail);
            Assert.Equal(1, launches);
            Assert.Equal(AcceptanceGateEngineSettings.Load(workspace).ResolveCheckTimeout(2), receivedTimeout);
            var emitted = Assert.Single(progress, item => item.Phase == "gate-phase-breakdown");
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(emitted.PhaseBreakdown);
            Assert.Equal(1, breakdown.ShardPermitBudget);
            Assert.True(breakdown.ShardPermitWaitDuration > TimeSpan.Zero);
            Assert.Contains("shard_permit_budget=1", emitted.CurrentTarget, StringComparison.Ordinal);
            var waitField = emitted.CurrentTarget.Split(';').Single(field => field.StartsWith("shard_permit_wait_ms=", StringComparison.Ordinal));
            Assert.True(double.Parse(waitField["shard_permit_wait_ms=".Length..], CultureInfo.InvariantCulture) > 0);
            Assert.Contains("slot_wait_ms=", emitted.CurrentTarget, StringComparison.Ordinal);
        }
        finally
        {
            held!.Dispose();
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(20));
            DeleteDirectoryWithRetry(workspace);
            if (Directory.Exists(permitRoot)) Directory.Delete(permitRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LargerMachineBudgetPreservesPerGateShardWidth()
    {
        var workspace = CreateShardWorkspace(4);
        var permitRoot = Path.Combine(Path.GetTempPath(), "shard-budget-" + Guid.NewGuid().ToString("N"));
        var twoStarted = Signal();
        var release = Signal();
        var starts = 0;
        var active = 0;
        var peak = 0;
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            ResolveShardCoreBudgetForTests = () => 2,
            ResolveGateShardBudgetForTests = () => 8,
            ShardPermitRootForTests = permitRoot
        };
        try
        {
            var verifier = new GoalAcceptanceVerifier(overrides, async (
                string[] args, string _, TimeSpan timeout, CancellationToken cancellationToken) =>
            {
                if (!IsTestCommand(args)) return Passed();
                var started = Interlocked.Increment(ref starts);
                var now = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref peak, now);
                if (started == 2) twoStarted.TrySetResult();
                try
                {
                    if (started <= 2) await release.Task.WaitAsync(cancellationToken);
                    return Passed();
                }
                finally { Interlocked.Decrement(ref active); }
            });
            var run = verifier.RunAsync(workspace);
            try
            {
                await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(2, Volatile.Read(ref starts));
                Assert.Equal(2, Volatile.Read(ref peak));
            }
            finally { release.TrySetResult(); }
            var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(result.Passed, result.OutputTail);
            Assert.Equal(4, starts);
            Assert.Equal(2, peak);
        }
        finally
        {
            DeleteDirectoryWithRetry(workspace);
            if (Directory.Exists(permitRoot)) Directory.Delete(permitRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    public async Task FailedTimedOutOrCancelledLaneReleasesPermit(string outcome)
    {
        var workspace = CreateShardWorkspace(1);
        var permitRoot = Path.Combine(Path.GetTempPath(), "shard-budget-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var acquired = 0;
        var released = 0;
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            ResolveGateShardBudgetForTests = () => 1,
            ShardPermitRootForTests = permitRoot,
            OnShardPermitAcquiredForTests = _ => Interlocked.Increment(ref acquired),
            OnShardPermitReleasedForTests = _ => Interlocked.Increment(ref released)
        };
        try
        {
            var verifier = new GoalAcceptanceVerifier(overrides, (
                string[] args, string _, TimeSpan timeout, CancellationToken token) =>
            {
                if (!IsTestCommand(args)) return Task.FromResult(Passed());
                if (outcome == "cancellation")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(outcome == "timeout"
                    ? new GoalAcceptanceVerifier.CommandResult(124, "timed out", TimedOut: true, Timeout: timeout)
                    : new GoalAcceptanceVerifier.CommandResult(7, "lane failed"));
            });
            try
            {
                await verifier.RunAsync(workspace, cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) when (outcome == "cancellation") { }
            Assert.True(acquired > 0);
            Assert.Equal(acquired, released);
            var pool = GateShardPermitPool.ForRoot(permitRoot, 1);
            Assert.True(pool.TryAcquire(out var permit));
            permit!.Dispose();
        }
        finally
        {
            DeleteDirectoryWithRetry(workspace);
            if (Directory.Exists(permitRoot)) Directory.Delete(permitRoot, recursive: true);
        }
    }

    private static bool IsTestCommand(string[] args) =>
        args.Length > 1 && args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("test", StringComparison.OrdinalIgnoreCase);

    private static GoalAcceptanceVerifier.CommandResult Passed() =>
        new(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.");

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            var observed = Volatile.Read(ref target);
            while (value > observed)
            {
                var prior = Interlocked.CompareExchange(ref target, value, observed);
                if (prior == observed) return;
                observed = prior;
            }
        }
    }

    private static string CreateShardWorkspace(int lanes)
    {
        var laneRows = Enumerable.Range(0, lanes).Select(index =>
            $"{{ \"name\": \"Lane {index}\", \"filter\": \"FullyQualifiedName~Lane{index}Tests\" }}");
        return CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": {{lanes}},
                "infrastructureTestLanes": [{{string.Join(",", laneRows)}}]
              },
              "checks": [{
                "name": "infrastructure tests",
                "type": "dotnet-test",
                "runner": "vstest",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "timeoutMinutes": 2
              }],
              "forbiddenChangedPathGlobs": []
            }
            """);
    }
}
