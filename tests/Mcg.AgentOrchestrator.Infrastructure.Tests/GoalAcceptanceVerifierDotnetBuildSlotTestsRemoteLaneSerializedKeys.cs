using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// The collection owns the isolated dotnet root; each scenario also owns its host state.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneSerializedKeys : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Key = "xunit:JobAccounting";

    [Xunit.Fact]
    public async Task SerializedKey_OffersOneKeyedLaneAndKeylessPeerWhileOtherRunsLocally()
    {
        using var scenario = CreateScenario();
        scenario.Configure(null, [Key]);
        var fake = scenario.GreenExecutor(waitForKeyedLocal: true);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(2, fake.Requests.Count);
        var keyedRequest = Assert.Single(fake.Requests, request => request.Lane == Lane(0) || request.Lane == Lane(1));
        Assert.Single(fake.Requests, request => request.Lane == Lane(2));
        var localIndex = keyedRequest.Lane == Lane(0) ? 1 : 0;
        Assert.Equal(localIndex, Assert.Single(scenario.LocalStarts));
        Assert.Equal(Lane(localIndex), Assert.Single(scenario.ResourcesAcquired));
        foreach (var request in fake.Requests)
        {
            var accepted = Assert.Single(scenario.Health(), row => row.Lane == request.Lane && row.Outcome == RemoteLaneOutcomeCode.Accepted);
            Assert.Equal("fixture-executor", accepted.ExecutorId);
            var check = Assert.Single(result.Checks!, check => check.Name == request.Lane);
            Assert.StartsWith("remote-executor=fixture-executor", check.ResultSummary!);
        }
        var skipped = Assert.Single(scenario.Health(), row => row.Outcome == RemoteLaneOutcomeCode.OfferSkippedNoExecutor);
        Assert.Equal(Lane(localIndex), skipped.Lane);
        Assert.StartsWith("serialized-key-busy=xunit:JobAccounting no-idle-slot ", skipped.Reason!);
        Assert.DoesNotContain(scenario.Health(), row => row.Outcome == RemoteLaneOutcomeCode.NotEligibleExclusiveResource);
    }

    [Xunit.Fact]
    public async Task UnusedSerializedKey_WithMachineLocalKey_MatchesBaseline()
    {
        var baseline = await Observe([Key], null);
        var extended = await Observe([Key], ["xunit:Unused"]);
        Assert.Equal(baseline.Requests, extended.Requests);
        Assert.Equal(baseline.LocalStarts, extended.LocalStarts);
        Assert.Equal(baseline.Health, extended.Health);
        Assert.Contains(Lane(0), baseline.Requests);
        Assert.Contains(Lane(1), baseline.Requests);
        Assert.Empty(baseline.LocalStarts);
        Assert.Equal(3, baseline.Requests.Length);
        Assert.All(baseline.Health, row => Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome));
    }

    [Xunit.Fact]
    public async Task UnusedSerializedKey_WithUnlistedKey_MatchesLocalBaseline()
    {
        var baseline = await Observe(null, null);
        var extended = await Observe(null, ["xunit:Unused"]);
        Assert.Equal(baseline.Requests, extended.Requests);
        Assert.Equal(baseline.LocalStarts, extended.LocalStarts);
        Assert.Equal(baseline.Health, extended.Health);
        Assert.Equal(new[] { Lane(2) }, baseline.Requests);
        Assert.Equal(new[] { 0, 1 }, baseline.LocalStarts);
        foreach (var lane in new[] { Lane(0), Lane(1) })
            Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource,
                Assert.Single(baseline.Health, row => row.Lane == lane).Outcome);
    }

    private async Task<Observation> Observe(string[]? machineLocal, string[]? serialized)
    {
        using var scenario = CreateScenario();
        scenario.Configure(machineLocal, serialized);
        var fake = scenario.GreenExecutor(waitForKeyedLocal: false);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        return new(fake.Requests.Select(request => request.Lane).ToArray(), scenario.LocalStarts.Order().ToArray(),
            scenario.Health().OrderBy(row => row.Lane, StringComparer.Ordinal).ThenBy(row => row.Outcome)
                .Select(row => (row.Lane, row.Outcome)).ToArray());
    }

    private sealed record Observation(string[] Requests, int[] LocalStarts,
        (string Lane, RemoteLaneOutcomeCode Outcome)[] Health);

    private Scenario CreateScenario()
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, CreateShardWorkspace());
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        RemoteLaneOfferSeeding.PrepareFixture(scenario.Root, TestOverrides);
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";

    private static string CreateShardWorkspace()
    {
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = Filter(index),
            exclusiveResourceKeys = index < 2 ? new[] { Key } : Array.Empty<string>()
        });
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 3,
                "infrastructureTestLanes": {{JsonSerializer.Serialize(rows)}},
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "timeoutMinutes": 2
              }],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var sourceDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(sourceDirectory);
        foreach (var index in Enumerable.Range(0, 3))
            File.WriteAllText(Path.Combine(sourceDirectory, $"Lane{index}Tests.cs"),
                $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        return root;
    }

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneSerializedKeys owner, string root) : IDisposable
    {
        internal string Root { get; } = root;
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<string> ResourcesAcquired { get; } = new();

        internal void Configure(string[]? machineLocalKeys, string[]? serializedKeys)
        {
            var configuration = new Dictionary<string, object>
            {
                ["executors"] = new[] { new { id = "fixture-executor", leaseSeconds = 60, slots = 3 } },
                ["lanes"] = Enumerable.Range(0, 3).Select(Lane).ToArray()
            };
            if (machineLocalKeys is not null) configuration["machineLocalResourceKeys"] = machineLocalKeys;
            if (serializedKeys is not null) configuration["executorSerializedResourceKeys"] = serializedKeys;
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(configuration));
        }

        internal FakeRemoteLaneExecutor GreenExecutor(bool waitForKeyedLocal)
        {
            var gate = new object();
            var mayPublish = !waitForKeyedLocal;
            var pending = new List<(RemoteLaneRequest Request, FakeRemoteLaneExecutor.Handle Handle)>();
            owner.TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = lane =>
            {
                ResourcesAcquired.Enqueue(lane);
                if (lane != Lane(0) && lane != Lane(1)) return;
                lock (gate)
                {
                    mayPublish = true;
                    foreach (var (request, handle) in pending) handle.Publish(RemoteResult(request));
                    pending.Clear();
                }
            };
            var fake = new FakeRemoteLaneExecutor();
            fake.Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
                lock (gate)
                {
                    if (mayPublish) handle.Publish(RemoteResult(request));
                    else pending.Add((request, handle));
                }
                return Task.FromResult(new RemoteLaneSubmission(handle));
            };
            owner.TestOverrides.RemoteLaneExecutorForTests = fake;
            return fake;
        }

        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(owner.TestOverrides, Runner);
            var attemptId = Guid.NewGuid().ToString("N");
            var attemptFolder = Path.Combine(Root, ".orchestrator", "attempts", attemptId);
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            // Hang detector only: decisions use the frozen manual clock and the local-start signal.
            using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attemptId, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    Path.Combine(attemptFolder, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root), bound.Token);
            try { return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner); }
            catch (OperationCanceledException) when (bound.IsCancellationRequested)
            { throw new TimeoutException("Missing event: verifier settlement after keyed local start and remote publication."); }
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", projectName, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, projectName + ".dll"), "deterministic fixture");
                File.WriteAllText(Path.Combine(output, projectName + ".exe"), "deterministic fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }

        private RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var index = Enumerable.Range(0, 3).Single(index => request.Lane == Lane(index));
            var directory = Path.Combine(Root, "remote-receipts");
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, [$"Lane{index}Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, filename)]);
        }

        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));

        public void Dispose()
        {
            owner.TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
