using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses the same stable-slot and verifier override isolation as the existing claim tests.
[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorSlots : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task TwoSlots_BothLanesRunRemotelyAndReleaseTheirClaims()
    {
        using var scenario = CreateScenario("""[{"id":"one","slots":2}]""");
        var fake = PublishAfterTwoSubmissions(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await RunToCompletion(scenario);
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(new[] { "one", "one" }, fake.Requests.Select(request => request.ExecutorId));
        Assert.Equal(2, fake.Requests.Select(request => request.Lane).Distinct().Count());
        Assert.Empty(scenario.LocalStarts);
        Assert.Equal(2, scenario.Health().Count);
        Assert.All(scenario.Health(), row => Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome));
        using var zero = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one", 0);
        using var one = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one", 1);
        Assert.NotNull(zero);
        Assert.NotNull(one);
        Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.HostRoot, "one"));
    }

    [Fact]
    public async Task MissingSlots_OneRemoteLaneAndOneLocalLane()
    {
        using var scenario = CreateScenario("""[{"id":"one"}]""");
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle))
        };
        scenario.BeforeLocal = _ => handle.Publish(scenario.RemoteResult(Assert.Single(fake.Requests)));
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await RunToCompletion(scenario);
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        var request = Assert.Single(fake.Requests);
        Assert.Equal("one", request.ExecutorId);
        var localLane = Assert.Single(scenario.LocalStarts);
        Assert.NotEqual($"infrastructure tests: Lane {localLane}", request.Lane);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health()).Outcome);
        var folder = Path.Combine(scenario.HostRoot, ".orchestrator", "remote-executor-occupancy", "one");
        Assert.Equal("claim.lock", Assert.Single(Directory.GetFiles(folder).Select(Path.GetFileName)));
    }

    [Fact]
    public async Task TwoExecutors_ClaimsSpreadBeforeReusingAnExecutor()
    {
        using var scenario = CreateScenario("""[{"id":"one","slots":2},{"id":"two","slots":2}]""");
        var fake = PublishAfterTwoSubmissions(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await RunToCompletion(scenario);
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(new[] { "one", "two" }, fake.Requests.Select(request => request.ExecutorId));
        Assert.Empty(scenario.LocalStarts);
        Assert.Equal(2, scenario.Health().Count);
        Assert.All(scenario.Health(), row => Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome));
    }

    [Fact]
    public async Task SlotFault_RetiresExecutorWhilePeerKeepsItsClaim()
    {
        using var scenario = CreateScenario("""[{"id":"one","slots":2}]""");
        var peer = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        var fake = new FakeRemoteLaneExecutor();
        fake.Submit = (_, _) => Task.FromResult(fake.Requests.Count == 1
            ? new RemoteLaneSubmission(peer) : new RemoteLaneSubmission(null, "fixture-unreachable"));
        scenario.BeforeLocal = _ =>
        {
            using var zero = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one", 0);
            using var one = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "one", 1);
            Assert.Null(zero);
            Assert.NotNull(one);
            Assert.True(RemoteExecutorOccupancy.IsOccupied(scenario.HostRoot, "one"));
            peer.Publish(scenario.RemoteResult(fake.Requests.First()));
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var result = await RunToCompletion(scenario);
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(2, fake.Requests.Count);
        Assert.Single(scenario.LocalStarts);
        Assert.Single(scenario.Health(), row => row.Outcome == RemoteLaneOutcomeCode.Unreachable);
        Assert.Single(scenario.Health(), row => row.Outcome == RemoteLaneOutcomeCode.Accepted);
        Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.HostRoot, "one"));
    }

    private Scenario CreateScenario(string executors)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, executors);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        return scenario;
    }

    private static FakeRemoteLaneExecutor PublishAfterTwoSubmissions(Scenario scenario)
    {
        var submissions = new List<(RemoteLaneRequest Request, FakeRemoteLaneExecutor.Handle Handle)>();
        var gate = new object();
        return new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
                lock (gate)
                {
                    submissions.Add((request, handle));
                    if (submissions.Count == 2)
                        foreach (var submission in submissions)
                            submission.Handle.Publish(scenario.RemoteResult(submission.Request));
                }
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
    }

    private static async Task<AcceptanceVerificationResult> RunToCompletion(Scenario scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var run = scenario.RunAsync(cancellation.Token);
        try { return await Event(run, "two-lane gate completed"); }
        finally
        {
            await cancellation.CancelAsync();
            try { await Event(run, "gate cleanup completed"); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<T> Event<T>(Task<T> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }

    private sealed class Scenario : IDisposable
    {
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorSlots _owner;
        internal string Root { get; }
        internal string HostRoot => AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root);
        private GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal Action<int>? BeforeLocal { get; set; }

        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteExecutorSlots owner, string executors)
        {
            _owner = owner;
            Root = CreateManifestWorkspace("""
                {
                  "version":1,
                  "engine":{
                    "maxConcurrentShards":3,
                    "infrastructureTestLanes":[
                      {"name":"Lane 0","filter":"FullyQualifiedName~Lane0Tests"},
                      {"name":"Lane 1","filter":"FullyQualifiedName~Lane1Tests"}
                    ],
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
            for (var index = 0; index < 2; index++)
                File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"),
                    $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
            File.WriteAllText(ConfigurationPath, $$"""{"executors":{{executors}},"lanes":["infrastructure tests: Lane 0","infrastructure tests: Lane 1"]}""");
        }

        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));

        internal async Task<AcceptanceVerificationResult> RunAsync(CancellationToken token)
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
                var index = Enumerable.Range(0, 2).Single(index => args.Any(argument =>
                    argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                BeforeLocal?.Invoke(index);
                WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
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
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            var index = request.Lane == "infrastructure tests: Lane 0" ? 0 : 1;
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, [$"Lane{index}Tests.Executes"]);
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
