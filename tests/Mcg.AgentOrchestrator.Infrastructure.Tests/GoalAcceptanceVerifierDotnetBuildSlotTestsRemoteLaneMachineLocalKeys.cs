using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneMachineLocalKeys : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task AllKeysListed_AcceptsRemotelyWithoutExclusiveResourceRefusal()
    {
        using var scenario = CreateScenario(["xunit:EnvMutation", "xunit:JobAccounting"]);
        scenario.Configure(["xunit:EnvMutation", "xunit:JobAccounting"]);
        var fake = GreenExecutor(scenario);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Single(fake.Requests, request => request.Lane == Lane(0));
        Assert.DoesNotContain(0, scenario.LocalStarts);
        Assert.StartsWith("remote-executor=fixture-executor", LaneCheck(result, 0).ResultSummary!);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health(), row => row.Lane == Lane(0)).Outcome);
        Assert.DoesNotContain(scenario.Health(), row => row.Outcome == RemoteLaneOutcomeCode.NotEligibleExclusiveResource);
    }

    [Xunit.Theory]
    [Xunit.InlineData("xunit:ProcessSpawning")]
    [Xunit.InlineData("xunit:jobaccounting")]
    public async Task OneKeyUnlisted_RunsLocallyAndRecordsOneRefusal(string secondListedKey)
    {
        using var scenario = CreateScenario(["xunit:EnvMutation", "xunit:JobAccounting"]);
        scenario.Configure(["xunit:EnvMutation", secondListedKey]);
        var fake = GreenExecutor(scenario);

        var result = await scenario.RunAsync();

        AssertLocalRefusal(scenario, fake, result);
    }

    [Xunit.Fact]
    public async Task KeyListAbsent_RunsLocallyAndRecordsOneRefusal()
    {
        using var scenario = CreateScenario(["xunit:EnvMutation"]);
        scenario.Configure(null);
        var fake = GreenExecutor(scenario);

        var result = await scenario.RunAsync();

        AssertLocalRefusal(scenario, fake, result);
    }

    [Xunit.Fact]
    public async Task ListedKeyWithBuildSystemChange_RunsLocallyAndRecordsOneRefusal()
    {
        using var scenario = CreateScenario(["xunit:EnvMutation"], requiresBuildSystemChange: true);
        scenario.Configure(["xunit:EnvMutation"]);
        var fake = GreenExecutor(scenario);

        var result = await scenario.RunAsync();

        AssertLocalRefusal(scenario, fake, result);
    }

    [Xunit.Fact]
    public async Task KeyedRemoteNotExecuted_UsesLocalRedVerdictAndRecordsExecutorFault()
    {
        using var scenario = CreateScenario(["xunit:EnvMutation"]);
        scenario.Configure(["xunit:EnvMutation"]);
        scenario.RedLane = 0;
        var fake = GreenExecutor(scenario, notExecutedLane: 0);

        var result = await scenario.RunAsync();

        Assert.Single(fake.Requests, request => request.Lane == Lane(0));
        Assert.False(result.Passed);
        var local = Assert.Single(result.Checks!, check => !check.Passed);
        Assert.Equal(Lane(0), local.Name);
        Assert.Equal(1, local.ExitCode);
        Assert.DoesNotContain("remote-executor=", local.ResultSummary ?? "");
        Assert.Contains(0, scenario.LocalStarts);
        var laneRows = scenario.Health().Where(row => row.Lane == Lane(0)).ToArray();
        var row = Assert.Single(laneRows, row => row.Outcome == RemoteLaneOutcomeCode.UnexpectedNotExecuted);
        Assert.Equal("executor", row.FaultOwner);
        Assert.Equal("not_executed=1", row.Reason);
        Assert.DoesNotContain(laneRows, row => row.Outcome is RemoteLaneOutcomeCode.Accepted or RemoteLaneOutcomeCode.TrxIncomplete);
        Assert.Contains("\"outcome\":\"unexpected-not-executed\"", File.ReadAllText(RemoteExecutorHealthLedger.ResolveStorePath(scenario.Root)));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task KeylessRemoteNotExecuted_AcceptsWithoutLocalRerun(bool requiresBuildSystemChange)
    {
        using var scenario = CreateScenario(requiresBuildSystemChange: requiresBuildSystemChange);
        scenario.Configure(["xunit:EnvMutation"]);
        scenario.RedLane = 0;
        var fake = GreenExecutor(scenario, notExecutedLane: 0);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Single(fake.Requests, request => request.Lane == Lane(0));
        Assert.DoesNotContain(0, scenario.LocalStarts);
        var remote = LaneCheck(result, 0);
        Assert.StartsWith("remote-executor=fixture-executor", remote.ResultSummary!);
        Assert.Equal(1, remote.ExecutedTestCount);
        Assert.Equal(2, remote.DiscoveredTestCount);
        var row = Assert.Single(scenario.Health(), row => row.Lane == Lane(0));
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Null(row.FaultOwner);
    }

    [Xunit.Fact]
    public async Task KeyedRemoteNotExecuted_DeclaredLiteralSkip_AcceptsRemotely()
    {
        using var scenario = CreateScenario(["xunit:EnvMutation"], lane0Source: SkippedSource("Skip = \"text\""));
        scenario.Configure(["xunit:EnvMutation"]);
        scenario.RedLane = 0; // Falling back locally must make this test red.
        var fake = GreenExecutor(scenario, notExecutedLane: 0);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Single(fake.Requests, request => request.Lane == Lane(0));
        Assert.DoesNotContain(0, scenario.LocalStarts);
        var remote = LaneCheck(result, 0);
        Assert.StartsWith("remote-executor=fixture-executor", remote.ResultSummary!);
        Assert.Equal(1, remote.ExecutedTestCount);
        Assert.Equal(2, remote.DiscoveredTestCount);
        var row = Assert.Single(scenario.Health(), row => row.Lane == Lane(0));
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Null(row.FaultOwner);
    }

    [Xunit.Theory]
    [Xunit.InlineData("SkipUnless")]
    [Xunit.InlineData("SkipWhen")]
    public async Task KeyedRemoteNotExecuted_SkipBesideConditionalSkip_FallsBackLocally(string member)
    {
        using var scenario = CreateScenario(["xunit:EnvMutation"],
            lane0Source: SkippedSource($"Skip = \"text\", {member} = \"IsSet\""));
        scenario.Configure(["xunit:EnvMutation"]);
        var fake = GreenExecutor(scenario, notExecutedLane: 0);

        var result = await scenario.RunAsync();

        AssertNotExecutedFallback(scenario, fake, result);
    }

    [Xunit.Fact]
    public async Task KeyedRemoteNotExecuted_UndeclaredSkip_FallsBackLocally()
    {
        using var scenario = CreateScenario(["xunit:EnvMutation"]);
        scenario.Configure(["xunit:EnvMutation"]);
        var events = new ConcurrentQueue<string>();
        TestOverrides.OnRemoteLaneEventForTests = events.Enqueue;
        var fake = GreenExecutor(scenario, notExecutedLane: 0);

        var result = await scenario.RunAsync();

        AssertNotExecutedFallback(scenario, fake, result);
        Assert.Contains(events, message => message.StartsWith("REMOTE_LANE_NOT_EXECUTED ", StringComparison.Ordinal) &&
                                           message.Contains("first_undeclared=Lane0Tests.Skipped", StringComparison.Ordinal));
    }

    private static string SkippedSource(string arguments) => $$"""
        public class Lane0Tests
        {
            public static bool IsSet => true;
            [Xunit.Fact] public void Executes() { }
            [Xunit.Fact({{arguments}})] public void Skipped() { }
        }
        """;

    private static void AssertNotExecutedFallback(Scenario scenario, FakeRemoteLaneExecutor fake,
        AcceptanceVerificationResult result)
    {
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Single(fake.Requests, request => request.Lane == Lane(0));
        Assert.Contains(0, scenario.LocalStarts);
        Assert.DoesNotContain("remote-executor=", LaneCheck(result, 0).ResultSummary ?? "");
        var laneRows = scenario.Health().Where(row => row.Lane == Lane(0)).ToArray();
        var row = Assert.Single(laneRows, row => row.Outcome == RemoteLaneOutcomeCode.UnexpectedNotExecuted);
        Assert.Equal(RemoteLaneOutcomeCode.UnexpectedNotExecuted, row.Outcome);
        Assert.StartsWith("not_executed=1", row.Reason!);
        Assert.Equal("executor", row.FaultOwner);
        Assert.DoesNotContain(laneRows, row => row.Outcome == RemoteLaneOutcomeCode.Accepted);
    }

    private static void AssertLocalRefusal(Scenario scenario, FakeRemoteLaneExecutor fake, AcceptanceVerificationResult result)
    {
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        var local = LaneCheck(result, 0); // The lane must survive selection for the refusal to count.
        Assert.DoesNotContain("remote-executor=", local.ResultSummary ?? "");
        Assert.DoesNotContain(fake.Requests, request => request.Lane == Lane(0));
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health(), row => row.Lane == Lane(0));
        Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, row.Outcome);
        Assert.Null(row.FaultOwner);
        Assert.Contains("\"outcome\":\"not-eligible-exclusive-resource\"", File.ReadAllText(RemoteExecutorHealthLedger.ResolveStorePath(scenario.Root)));
    }

    private FakeRemoteLaneExecutor GreenExecutor(Scenario scenario, int? notExecutedLane = null)
    {
        var fake = new FakeRemoteLaneExecutor();
        fake.Submit = (request, _) =>
        {
            var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
            handle.Publish(scenario.RemoteResult(request, request.Lane == Lane(notExecutedLane ?? -1)));
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        return fake;
    }

    private Scenario CreateScenario(string[]? keys = null, bool requiresBuildSystemChange = false, string? lane0Source = null)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, CreateShardWorkspace(keys ?? [], requiresBuildSystemChange, lane0Source));
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        RemoteLaneOfferSeeding.PrepareFixture(scenario.Root, TestOverrides);
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";
    private static AcceptanceCheckResult LaneCheck(AcceptanceVerificationResult result, int index) =>
        Assert.Single(result.Checks!, check => check.Name == Lane(index));

    private static string CreateShardWorkspace(string[] keys, bool requiresBuildSystemChange, string? lane0Source)
    {
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = Filter(index),
            exclusiveResourceKeys = index == 0 ? keys : Array.Empty<string>(),
            requiresBuildSystemChange = index == 0 && requiresBuildSystemChange
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
            File.WriteAllText(Path.Combine(sourceDirectory, $"Lane{index}Tests.cs"), index == 0 && lane0Source is not null
                ? lane0Source : $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        return root;
    }

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneMachineLocalKeys owner, string root) : IDisposable
    {
        internal string Root { get; } = root;
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal int? RedLane { get; set; }

        internal void Configure(string[]? machineLocalKeys)
        {
            var configuration = new Dictionary<string, object>
            {
                ["executors"] = new[] { new { id = "fixture-executor", leaseSeconds = 60 } },
                ["lanes"] = Enumerable.Range(0, 3).Select(Lane).ToArray()
            };
            if (machineLocalKeys is not null) configuration["machineLocalResourceKeys"] = machineLocalKeys;
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(configuration));
        }

        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(owner.TestOverrides, Runner);
            var attemptId = Guid.NewGuid().ToString("N");
            var attemptFolder = Path.Combine(Root, ".orchestrator", "attempts", attemptId);
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attemptId, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    Path.Combine(attemptFolder, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root), CancellationToken.None);
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (RedLane == index) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(RedLane == index ? 1 : 0, RedLane == index ? "Failed: 1" : "Passed: 1"));
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

        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request, bool notExecuted)
        {
            var index = Enumerable.Range(0, 3).Single(index => request.Lane == Lane(index));
            var directory = Path.Combine(Root, "remote-receipts");
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            string[] args = ["--results-directory", directory, "--report-trx-filename", filename];
            if (notExecuted)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, filename), $$"""
                    <TestRun>
                      <TestDefinitions>
                        <UnitTest id="executed" name="Lane{{index}}Tests.Executes"><TestMethod className="Lane{{index}}Tests" name="Executes" /></UnitTest>
                        <UnitTest id="skipped" name="Lane{{index}}Tests.Skipped"><TestMethod className="Lane{{index}}Tests" name="Skipped" /></UnitTest>
                      </TestDefinitions>
                      <Results>
                        <UnitTestResult testId="executed" testName="Lane{{index}}Tests.Executes" outcome="Passed" />
                        <UnitTestResult testId="skipped" testName="Lane{{index}}Tests.Skipped" outcome="NotExecuted" />
                      </Results>
                      <ResultSummary outcome="Completed"><Counters total="2" executed="1" passed="1" failed="0" notExecuted="1" /></ResultSummary>
                    </TestRun>
                    """);
            }
            else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, filename)]);
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
