using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteEarlyOffer : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task SubmitsLane0WhileHostBuildIsBlocked()
    {
        using var scenario = CreateScenario(disabled: false);
        var buildEntered = Signal();
        var releaseBuild = Signal();
        scenario.BeforeBuild = async token =>
        {
            buildEntered.TrySetResult();
            await releaseBuild.Task.WaitAsync(token);
        };
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(buildEntered.Task, "host build entered its blocking hook");
            var request = await Event(scenario.Fake.Submitted.Task, "Lane 0 submitted while host build is blocked");
            Assert.Equal(Lane(0), request.Lane);
            Assert.False(releaseBuild.Task.IsCompleted);
            Assert.Empty(scenario.LocalStarts);
            Assert.Contains("decision=offer", Assert.Single(scenario.PolicyLines(), line => line.Contains($"lane=\"{Lane(0)}\"")));
            releaseBuild.TrySetResult();
            var result = await Event(gate, "gate finished after releasing host build");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Equal(3, Lanes(result).Length);
            Assert.Equal(Lane(0), Assert.Single(scenario.Fake.Requests).Lane);
            Assert.Equal([1, 2], scenario.LocalStarts.Order());
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health(), row => row.Lane == Lane(0)).Outcome);
        }
        finally
        {
            releaseBuild.TrySetResult();
            await cancellation.CancelAsync();
            await Drain(gate);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("green")]
    [Xunit.InlineData("exclusive")]
    [Xunit.InlineData("keep-local")]
    [Xunit.InlineData("busy")]
    [Xunit.InlineData("cache")]
    public async Task ObservableResultsEqualBaseline(string control)
    {
        var early = await RunControl(control, disabled: false);
        var baseline = await RunControl(control, disabled: true);
        Assert.Equal(baseline.Passed, early.Passed);
        Assert.Equal(baseline.Lanes, early.Lanes);
        Assert.Equal(baseline.LocalStarts, early.LocalStarts);
        Assert.Equal(baseline.Requests, early.Requests);
        Assert.Equal(baseline.Health, early.Health);
        Assert.Equal(baseline.Policy, early.Policy);
    }

    private async Task<Observation> RunControl(string control, bool disabled)
    {
        using var scenario = CreateScenario(disabled, control == "exclusive" ? 1 : null);
        if (control == "exclusive") scenario.Configure(0, 1);
        if (control == "keep-local") scenario.SeedLocal([5, 120, 120]);
        AcceptanceVerificationResult result;
        if (control == "busy")
        {
            scenario.Configure(0, 1, 2);
            using var held = RemoteExecutorOccupancy.TryClaimExclusive(scenario.HostRoot, "fixture-executor", 0);
            Assert.NotNull(held);
            result = await Event(scenario.RunAsync(), control + " gate completed");
        }
        else result = await Event(scenario.RunAsync(), control + " gate completed");
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(3, Lanes(result).Length);
        if (control == "cache")
        {
            Assert.Single(scenario.Fake.Requests);
            result = await Event(scenario.RunAsync(), "second gate reused partition verdict");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.StartsWith("partition-verdict-cache reused", Assert.Single(Lanes(result), check => check.Name == Lane(0)).ResultSummary!);
        }
        if (control == "exclusive")
        {
            Assert.DoesNotContain(scenario.Fake.Requests, request => request.Lane == Lane(1));
            Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, Assert.Single(scenario.Health(), row => row.Lane == Lane(1)).Outcome);
        }
        if (control == "keep-local")
        {
            Assert.Empty(scenario.Fake.Requests);
            Assert.Contains("decision=keep-local", Assert.Single(scenario.PolicyLines()));
        }
        else if (control == "busy")
        {
            Assert.Empty(scenario.Fake.Requests);
            Assert.Equal([0, 1, 2], scenario.LocalStarts.Order());
            Assert.Equal(3, scenario.Health().Count);
            foreach (var index in Enumerable.Range(0, 3))
                Assert.Equal(RemoteLaneOutcomeCode.OfferSkippedNoExecutor, Assert.Single(scenario.Health(), row => row.Lane == Lane(index)).Outcome);
        }
        else
        {
            Assert.Equal(Lane(0), Assert.Single(scenario.Fake.Requests).Lane);
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health(), row => row.Lane == Lane(0)).Outcome);
            if (control != "cache") Assert.Equal([1, 2], scenario.LocalStarts.Order());
        }
        Assert.NotEmpty(scenario.PolicyLines());
        Assert.All(scenario.PolicyLines(), line => Assert.Contains(" F=", line));
        return new(result.Passed, Lanes(result).Select(check => (check.Name, check.Passed)).ToArray(),
            scenario.LocalStarts.Order().ToArray(), scenario.Fake.Requests.Select(request => request.Lane).ToArray(),
            scenario.Health().Select(row => (row.Lane, row.Outcome)).ToArray(), scenario.PolicyLines());
    }

    [Xunit.Fact]
    public async Task HostBuildFailureEqualsBaselineAndReleasesRemoteSlot()
    {
        var early = await RunBuildFailure(disabled: false);
        var baseline = await RunBuildFailure(disabled: true);
        Assert.False(early.Passed);
        Assert.Equal(baseline.Passed, early.Passed);
        Assert.Equal(baseline.Classifications, early.Classifications);
        Assert.Equal(baseline.Lanes, early.Lanes);
    }

    private async Task<(bool Passed, (string, string?)[] Classifications, (string, bool)[] Lanes)> RunBuildFailure(bool disabled)
    {
        using var scenario = CreateScenario(disabled);
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        scenario.Fake.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle));
        scenario.BuildFails = true;
        if (!disabled) scenario.BeforeBuild = token => scenario.Fake.Submitted.Task.WaitAsync(token);
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(handle.Polled.Task, "pending remote handle polled");
            // The failed host build still enters the shard batch. Expire its pending remote
            // through the decision clock so the existing fallback path can finish too.
            scenario.Clock.Advance(TimeSpan.FromSeconds(61));
            var result = await Event(gate, "failed-build gate drained");
            Assert.False(result.Passed);
            Assert.Equal(3, Lanes(result).Length);
            Assert.All(Lanes(result), check =>
            {
                Assert.False(check.Passed);
                Assert.StartsWith("build phase failed", check.ResultSummary!);
            });
            Assert.True(handle.IsAbandoned);
            Assert.Equal(Lane(0), Assert.Single(scenario.Fake.Requests).Lane);
            var census = RemoteExecutorOccupancyCensus.Take(scenario.HostRoot, [("fixture-executor", "idle")]);
            Assert.Equal(0, Assert.Single(census).Gate);
            return (result.Passed, result.Checks!.Select(check => (check.Name, check.FailureClassification)).ToArray(),
                Lanes(result).Select(check => (check.Name, check.Passed)).ToArray());
        }
        finally { await cancellation.CancelAsync(); await Drain(gate); }
    }

    private sealed record Observation(bool Passed, (string, bool)[] Lanes, int[] LocalStarts,
        string[] Requests, (string, RemoteLaneOutcomeCode)[] Health, string[] Policy);

    private Scenario CreateScenario(bool disabled, int? keyedLane = null)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        TestOverrides.RemoteLaneEarlyOfferDisabledForTests = disabled;
        var scenario = new Scenario(this, keyedLane);
        scenario.Configure(0);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        RemoteLaneOfferSeeding.PrepareFixture(scenario.Root, TestOverrides);
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.OnRemoteLaneProgressLineForTests = scenario.Progress.Enqueue;
        TestOverrides.RemoteLaneExecutorForTests = scenario.Fake;
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";
    private static AcceptanceCheckResult[] Lanes(AcceptanceVerificationResult result) =>
        result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal)).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name)
    {
        await Event((Task)task, name);
        return await task;
    }
    private static async Task Drain(Task task)
    {
        try { await Event(task, "gate cleanup"); }
        catch (OperationCanceledException) { }
    }

    private static string CreateShardManifest(int? keyedLane)
    {
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = Filter(index),
            exclusiveResourceKeys = index == keyedLane ? new[] { "fixture-resource-" + index } : Array.Empty<string>()
        });
        return $$"""
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
            """;
    }

    private sealed class Scenario : IDisposable
    {
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteEarlyOffer _owner;
        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteEarlyOffer owner, int? keyedLane)
        {
            _owner = owner;
            Root = CreateManifestWorkspace(CreateShardManifest(keyedLane));
            var source = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(source);
            foreach (var index in Enumerable.Range(0, 3))
                File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"), $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
            Fake.Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
                handle.Publish(RemoteResult(request));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            };
        }
        internal string Root { get; }
        internal string HostRoot => AcceptancePartitionVerdictCache.ResolveHostStateRoot(Root);
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal FakeRemoteLaneExecutor Fake { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<string> Progress { get; } = new();
        internal Func<CancellationToken, Task>? BeforeBuild { get; set; }
        internal bool BuildFails { get; set; }
        internal void Configure(params int[] lanes) => File.WriteAllText(ConfigurationPath,
            JsonSerializer.Serialize(new { executors = new[] { new { id = "fixture-executor", leaseSeconds = 60, slots = 1 } }, lanes = lanes.Select(Lane).ToArray() }));
        internal void SeedLocal(double[] seconds)
        {
            var path = AcceptanceLaneDurationStore.ResolveStorePath(Root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, Enumerable.Range(0, 3).SelectMany(index => Enumerable.Range(0, 3).Select(_ =>
                JsonSerializer.Serialize(new { version = 1, laneName = Lane(index), filterHash = GoalAcceptanceVerifier.ShortHash(Filter(index)),
                    durationSeconds = seconds[index], recordedAt = Clock.GetUtcNow() }))));
        }
        internal async Task<AcceptanceVerificationResult> RunAsync(CancellationToken token = default)
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(_owner.TestOverrides, Runner);
            var attemptId = Guid.NewGuid().ToString("N");
            var attemptFolder = Path.Combine(Root, ".orchestrator", "attempts", attemptId);
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attemptId, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    Path.Combine(attemptFolder, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root), token);
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
        }
        private async Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return new(0, "Passed: 1");
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                if (BeforeBuild is not null) await BeforeBuild(token);
                if (BuildFails) return new(1, "error CS1002: deterministic host build failure");
                const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, project + ".dll"), "deterministic fixture");
                File.WriteAllText(Path.Combine(output, project + ".exe"), "deterministic fixture");
            }
            return new(0, "Build succeeded.");
        }
        private RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var directory = Path.Combine(Root, "remote-receipts");
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, ["Lane0Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, filename)]);
        }
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));
        internal string[] PolicyLines() => Progress.Where(line => line.StartsWith("REMOTE_LANE_POLICY", StringComparison.Ordinal)).ToArray();
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
