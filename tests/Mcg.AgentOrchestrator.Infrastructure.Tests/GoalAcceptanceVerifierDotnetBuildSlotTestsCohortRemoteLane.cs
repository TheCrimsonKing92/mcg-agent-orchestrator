using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsCohortRemoteLane : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string CohortGateIdentity = "11111111111111111111111111111111:22222222222222222222222222222222";

    [Fact(DisplayName = "Cohort offload binds allowlisted lanes to the combined candidate without verdict reuse")]
    public async Task BoundGreenOffersAllowlistedLanesWithoutPartitionJournal()
    {
        using var scenario = CreateScenario();
        scenario.Configure(0, 1);
        var fake = GreenExecutor(scenario);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        var cohortAttemptId = scenario.LastAttemptId;
        var requests = fake.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(new[] { Lane(0), Lane(1) }, requests.Select(request => request.Lane).Order().ToArray());
        foreach (var index in new[] { 0, 1 })
        {
            var request = Assert.Single(requests.Where(request => request.Lane == Lane(index)));
            Assert.Equal("combined-commit", request.VerifyingCommitSha);
            Assert.Equal("combined-tree", request.CandidateTreeSha);
            Assert.Equal("main-sha", request.MainSha);
            Assert.Equal(cohortAttemptId, request.AttemptId);
            Assert.Equal(CohortGateIdentity, request.GoalId);
            Assert.Equal(GoalAcceptanceVerifier.ShortHash(Filter(index)), request.FilterHash);
            var lane = Assert.Single(Lanes(result).Where(check => check.Name == Lane(index)));
            Assert.StartsWith("remote-executor=" + request.ExecutorId, lane.ResultSummary!);
            Assert.Equal(cohortAttemptId, lane.TestResultAttemptId);
        }
        Assert.Equal(new[] { 2 }, scenario.LocalStarts.ToArray());
        var rows = scenario.Health();
        Assert.Equal(2, rows.Count);
        foreach (var index in new[] { 0, 1 })
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(rows.Where(row => row.Lane == Lane(index))).Outcome);
        var journalDirectory = Path.Combine(scenario.Root, ".orchestrator", "goal-operations");
        Assert.Empty(Directory.Exists(journalDirectory)
            ? Directory.GetFiles(journalDirectory, "*", SearchOption.AllDirectories) : Array.Empty<string>());

        // Only after the no-journal assertion, run the same workspace with a goal-keyed cache.
        var baseline = await scenario.RunAsync(goalKeyed: true);
        Assert.True(baseline.Passed, JsonSerializer.Serialize(baseline.Checks));
        var baselineRequests = fake.Requests.ToArray().Skip(requests.Length).ToArray();
        Assert.Equal(2, baselineRequests.Length);
        foreach (var request in requests)
        {
            var goalRequest = Assert.Single(baselineRequests.Where(baselineRequest => baselineRequest.Lane == request.Lane));
            Assert.Equal(scenario.Goal.Value, goalRequest.GoalId);
            Assert.Equal(request.ManifestIdentity, goalRequest.ManifestIdentity);
        }
    }

    [Theory(DisplayName = "Cohort binding mismatches use the local red lane as the gate verdict")]
    [InlineData("tree", nameof(RemoteLaneOutcomeCode.BindingMismatchTree))]
    [InlineData("manifest", nameof(RemoteLaneOutcomeCode.BindingMismatchManifest))]
    [InlineData("commit", nameof(RemoteLaneOutcomeCode.BindingMismatchCommit))]
    public async Task BindingMismatchUsesLocalRedVerdict(string field, string expectedOutcome)
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        scenario.RedLane = 0;
        var fake = GreenExecutor(scenario, result => field switch
        {
            "tree" => result with { ObservedTreeSha = "different-tree" },
            "manifest" => result with { ManifestIdentity = "different-manifest" },
            "commit" => result with { VerifyingCommitSha = "different-commit" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        });

        var result = await scenario.RunAsync();

        Assert.Equal(Lane(0), Assert.Single(fake.Requests).Lane);
        var row = Assert.Single(scenario.Health().Where(row => row.Outcome == Enum.Parse<RemoteLaneOutcomeCode>(expectedOutcome)));
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(expectedOutcome), row.Outcome);
        Assert.Equal("executor", row.FaultOwner);
        Assert.NotEqual(row.Expected, row.Observed);
        Assert.Contains(0, scenario.LocalStarts);
        Assert.False(result.Passed);
        var local = Assert.Single(Lanes(result).Where(check => !check.Passed));
        Assert.Equal(Lane(0), local.Name);
        Assert.DoesNotContain("remote-executor=", local.ResultSummary ?? string.Empty);
    }

    [Theory(DisplayName = "Missing cohort opt-in or candidate tree keeps every lane local")]
    [InlineData(false, "combined-tree")]
    [InlineData(true, "")]
    public async Task MissingOptInOrTreeRunsAllLanesLocally(bool cohortRemoteLanes, string tree)
    {
        using var scenario = CreateScenario(tree);
        scenario.Configure(0, 1);
        var fake = GreenExecutor(scenario);

        var result = await scenario.RunAsync(cohortRemoteLanes: cohortRemoteLanes);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Empty(fake.Requests);
        Assert.Equal(new[] { 0, 1, 2 }, scenario.LocalStarts.Order().ToArray());
        Assert.Empty(scenario.Health());

        // Prove the same configuration can offload once the missing precondition is supplied.
        SetPartitionVerdictKeyHooks("combined-tree", "main-sha", "combined-commit");
        var enabled = await scenario.RunAsync(cohortRemoteLanes: true);
        Assert.True(enabled.Passed, JsonSerializer.Serialize(enabled.Checks));
        Assert.Equal(new[] { Lane(0), Lane(1) }, fake.Requests.Select(request => request.Lane).Order().ToArray());
        Assert.All(scenario.Health(), row => Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome));
    }

    private Scenario CreateScenario(string tree = "combined-tree")
    {
        SetPartitionVerdictKeyHooks(tree, "main-sha", "combined-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, CreateShardWorkspace());
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        return scenario;
    }

    private FakeRemoteLaneExecutor GreenExecutor(Scenario scenario, Func<RemoteLaneResult, RemoteLaneResult>? change = null)
    {
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
                var result = scenario.RemoteResult(request);
                handle.Publish(change?.Invoke(result) ?? result);
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        return fake;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";
    private static AcceptanceCheckResult[] Lanes(AcceptanceVerificationResult result) =>
        result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal)).ToArray();

    private static string CreateShardWorkspace()
    {
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = Filter(index), exclusiveResourceKeys = Array.Empty<string>()
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

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsCohortRemoteLane owner, string root) : IDisposable
    {
        internal string Root { get; } = root;
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        private string RemoteDirectory => Path.Combine(Root, "remote-receipts");
        internal string? LastAttemptId { get; private set; }
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal int? RedLane { get; set; }

        internal void Configure(params int[] lanes) => File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor-one", leaseSeconds = 60 }, new { id = "fixture-executor-two", leaseSeconds = 60 } },
            lanes = lanes.Select(Lane).ToArray()
        }));

        internal async Task<AcceptanceVerificationResult> RunAsync(bool cohortRemoteLanes = true, bool goalKeyed = false)
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(owner.TestOverrides, Runner);
            var attemptId = Guid.NewGuid().ToString("N");
            LastAttemptId = attemptId;
            var resultsPrefix = Path.Combine(Root, ".orchestrator", "attempts", attemptId, "result");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attemptId, goalKeyed ? Goal.Value : CohortGateIdentity, Root,
                    "combined-tree", "main-sha", "combined-commit", resultsPrefix, slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root),
                options: new AcceptanceRunExecutionOptions(GateRunIdentity: CohortGateIdentity, CohortRemoteLanes: cohortRemoteLanes));
            return await verifier.RunOwnedAsync(Root, goalKeyed ? Goal : null, null, slot, lease, executionOwner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (RedLane == index) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(RedLane == index ? 1 : 0,
                    RedLane == index ? "Failed: 1" : "Passed: 1"));
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

        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var index = Enumerable.Range(0, 3).Single(index => request.Lane == Lane(index));
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            string[] args = ["--results-directory", RemoteDirectory, "--report-trx-filename", filename];
            WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(RemoteDirectory, filename)]);
        }

        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));

        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
