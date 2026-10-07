using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorClaim : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task HeldFirstExecutorIsSkippedForTheSecond()
    {
        using var scenario = CreateScenario();
        using var claim = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one");
        Assert.NotNull(claim);
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) => Task.FromResult(new RemoteLaneSubmission(ReadyHandle(scenario, request)))
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await Event(scenario.RunAsync(), "gate completed using second executor");
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal("two", Assert.Single(fake.Requests).ExecutorId);
        Assert.Equal(0, scenario.LocalStarts);
        var health = Assert.Single(scenario.Health());
        Assert.Equal("two", health.ExecutorId);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, health.Outcome);
    }

    [Xunit.Fact]
    public async Task AllHeldExecutorsRunLocallyWithoutLedgerRows()
    {
        using var scenario = CreateScenario();
        using var one = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one");
        using var two = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "two");
        Assert.NotNull(one);
        Assert.NotNull(two);
        var fake = new FakeRemoteLaneExecutor();
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await Event(scenario.RunAsync(), "local gate completed with all executors held");
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Empty(fake.Requests);
        Assert.Equal(1, scenario.LocalStarts);
        Assert.DoesNotContain(scenario.Health(), row => row.Lane == "infrastructure tests: Lane 0");
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task AcceptanceAndSubmissionRefusalCloseClaims(bool accepted)
    {
        using var scenario = CreateScenario();
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) => Task.FromResult(accepted
                ? new RemoteLaneSubmission(ReadyHandle(scenario, request))
                : new RemoteLaneSubmission(null, "fixture-unreachable"))
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await Event(scenario.RunAsync(), "gate completed and released claims");
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal("one", Assert.Single(fake.Requests).ExecutorId);
        Assert.Equal(accepted ? 0 : 1, scenario.LocalStarts);
        Assert.Equal(accepted ? RemoteLaneOutcomeCode.Accepted : RemoteLaneOutcomeCode.Unreachable,
            Assert.Single(scenario.Health()).Outcome);
        using var one = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one");
        using var two = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "two");
        Assert.NotNull(one);
        Assert.NotNull(two);
    }

    [Xunit.Fact]
    public async Task ExpiredLeaseRequestsCancellationBeforeLocalStart()
    {
        using var scenario = CreateScenario();
        var handle = new CancelHandle(scenario.Clock.GetUtcNow());
        TestOverrides.RemoteLaneExecutorForTests = PendingExecutor(handle);
        var cancelCountAtLocalStart = -1;
        scenario.BeforeLocal = () => cancelCountAtLocalStart = handle.CancelRequests;
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(handle.Polled.Task, "pending handle polled before lease expiry");
            scenario.Clock.Advance(TimeSpan.FromSeconds(61));
            var result = await Event(gate, "gate completed after lease expiry");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Equal(1, scenario.LocalStarts);
            Assert.Equal(1, cancelCountAtLocalStart);
            Assert.Equal(1, handle.CancelRequests);
            Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, Assert.Single(scenario.Health()).Outcome);
            using var claim = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one");
            Assert.NotNull(claim);
        }
        finally { await cancellation.CancelAsync(); await Drain(gate); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ObservedResultsDoNotRequestQueuedCancellation(bool treeMismatch)
    {
        using var scenario = CreateScenario();
        var handle = new CancelHandle(scenario.Clock.GetUtcNow());
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var result = scenario.RemoteResult(request);
                handle.Publish(treeMismatch ? result with { ObservedTreeSha = "different-tree" } : result);
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var verdict = await Event(scenario.RunAsync(), "gate completed with observed result");
        Assert.True(verdict.Passed, JsonSerializer.Serialize(verdict.Checks));
        Assert.Single(fake.Requests);
        Assert.Equal(0, handle.CancelRequests);
        Assert.Equal(treeMismatch ? 1 : 0, scenario.LocalStarts);
        var expectedOutcomes = treeMismatch
            ? new[] { RemoteLaneOutcomeCode.BindingMismatchTree, RemoteLaneOutcomeCode.LateAfterFallback }
            : new[] { RemoteLaneOutcomeCode.Accepted };
        Assert.Equal(expectedOutcomes, scenario.Health().Select(row => row.Outcome));
    }

    [Xunit.Fact]
    public async Task GateCancellationRequestsCancelBeforeAbandon()
    {
        using var scenario = CreateScenario();
        var handle = new CancelHandle(scenario.Clock.GetUtcNow());
        TestOverrides.RemoteLaneExecutorForTests = PendingExecutor(handle);
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(handle.Polled.Task, "pending handle polled before gate cancellation");
            await cancellation.CancelAsync();
            await Event(handle.Abandoned.Task, "pending handle abandoned");
            Assert.Equal(1, handle.CancelRequestsAtAbandon);
            Assert.Equal(1, handle.CancelRequests);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Event(gate, "cancelled gate drained"));
            Assert.Equal(0, scenario.LocalStarts);
        }
        finally { await cancellation.CancelAsync(); await Drain(gate); }
    }

    private Scenario CreateScenario()
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        return scenario;
    }

    private static FakeRemoteLaneExecutor.Handle ReadyHandle(Scenario scenario, RemoteLaneRequest request)
    {
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        handle.Publish(scenario.RemoteResult(request));
        return handle;
    }
    private static FakeRemoteLaneExecutor PendingExecutor(CancelHandle handle) => new()
    {
        Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle))
    };
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name) { await Event((Task)task, name); return await task; }
    private static async Task Drain(Task task)
    {
        try { await Event(task, "gate cleanup"); }
        catch (OperationCanceledException) { }
    }

    private sealed class CancelHandle(DateTimeOffset heartbeat) : IRemoteLaneHandle, IRemoteLaneQueuedJobCancellation
    {
        private RemoteLaneResult? _result;
        private int _cancelRequests;
        internal int CancelRequests => Volatile.Read(ref _cancelRequests);
        internal int CancelRequestsAtAbandon { get; private set; }
        internal TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? NewestHeartbeat => heartbeat;
        internal void Publish(RemoteLaneResult result) => Volatile.Write(ref _result, result);
        public RemoteLaneResult? TryGetResult() { Polled.TrySetResult(); return Volatile.Read(ref _result); }
        public void RequestQueuedJobCancellation() => Interlocked.Increment(ref _cancelRequests);
        public void Abandon() { CancelRequestsAtAbandon = CancelRequests; Abandoned.TrySetResult(); }
    }

    private sealed class Scenario : IDisposable
    {
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorClaim _owner;
        internal string Root { get; }
        internal string HostRoot => AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root);
        private GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal int LocalStarts { get; private set; }
        internal Action? BeforeLocal { get; set; }
        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorClaim owner)
        {
            _owner = owner;
            Root = CreateManifestWorkspace("""
                {
                  "version":1,
                  "engine":{
                    "maxConcurrentShards":3,
                    "infrastructureTestLanes":[{"name":"Lane 0","filter":"FullyQualifiedName~Lane0Tests"}],
                    "mtpInvocations":[{
                      "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                      "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                      "firewallExecutablePathTemplate":"bin/{projectName}/{configuration}/{projectName}.exe",
                      "arguments":["{executable}","--results-directory","{resultsDirectory}","--report-trx-filename","{trxFileName}"]
                    }]
                  },
                  "checks":[{
                    "name":"infrastructure tests","type":"dotnet-test","runner":"mtp",
                    "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "timeoutMinutes":2
                  }],
                  "forbiddenChangedPathGlobs":[]
                }
                """);
            var source = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Lane0Tests.cs"), "public class Lane0Tests { [Xunit.Fact] public void Executes() { } }");
            File.WriteAllText(ConfigurationPath, """{"executors":[{"id":"one"},{"id":"two"}],"lanes":["infrastructure tests: Lane 0"]}""");
        }
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health()
        {
            var path = RemoteExecutorHealthLedger.ResolveStorePath(Root);
            return File.Exists(path) ? RemoteExecutorHealthLedger.ReadAll(path) : [];
        }
        internal async Task<AcceptanceVerificationResult> RunAsync(CancellationToken token = default)
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(_owner.TestOverrides, Runner);
            var attempt = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(Root, ".orchestrator", "attempts", attempt, "result");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attempt, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    prefix, slot, Environment.ProcessId, null), AcceptanceGateEngineSettings.Load(Root), token);
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
        }
        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                LocalStarts++;
                BeforeLocal?.Invoke();
                WriteMtpTrx(args, 1, ["Lane0Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, project + ".dll"), "deterministic fixture");
                File.WriteAllText(Path.Combine(output, project + ".exe"), "deterministic fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }
        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var directory = Path.Combine(Root, "remote-receipts");
            const string filename = "remote.trx";
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, ["Lane0Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, filename)]);
        }
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
