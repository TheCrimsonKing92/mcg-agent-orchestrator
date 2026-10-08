using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLanePolicy : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task Policy_OffersOnlyFittingLaneAndLeavesOtherSlotsUnclaimed()
    {
        using var scenario = CreateScenario([45, 300, 200]);
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(1), Filter(1), 700);
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(2), Filter(2), 250);
        scenario.HoldRemoteUntilLocalStarts = true;
        scenario.CheckUnusedSlots = true;

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(Lane(2), Assert.Single(scenario.Fake.Requests).Lane);
        Assert.Equal(new[] { 0, 1 }, scenario.LocalStarts.Order().ToArray());
        Assert.Equal(2, scenario.SlotChecks);
        Assert.Equal(new[]
        {
            $"REMOTE_LANE_POLICY lane=\"{Lane(0)}\" decision=keep-local reason=short L=45 Ln=3 R=105 Rn=0 F=300 Fn=0",
            $"REMOTE_LANE_POLICY lane=\"{Lane(1)}\" decision=keep-local reason=remote-slower L=300 Ln=3 R=700 Rn=3 F=300 Fn=0",
            $"REMOTE_LANE_POLICY lane=\"{Lane(2)}\" decision=offer reason=fits L=200 Ln=3 R=250 Rn=3 F=300 Fn=0"
        }, scenario.Progress.Where(line => line.StartsWith("REMOTE_LANE_POLICY ")).Order(StringComparer.Ordinal).ToArray());
        var rows = RemoteExecutorHealthLedger.ReadAll(scenario.History);
        Assert.Equal(7, rows.Count);
        Assert.Equal(3, rows.Count(row => row.Lane == Lane(1))); // History only: no keep-local outcome.
        Assert.DoesNotContain(rows, row => row.Lane == Lane(0));
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(rows, row => row.Lane == Lane(2) && row.GateAttemptId != "seed-attempt").Outcome);
    }

    [Xunit.Fact]
    public async Task RecentFailures_RunLocallyWhileHealthyLaneIsOffered()
    {
        using var scenario = CreateScenario([120, 120, 120]);
        var binding = new RemoteLaneBinding("seed-executor", Lane(0), "commit", "tree", "main",
            GoalAcceptanceVerifier.ShortHash(Filter(0)), "manifest");
        for (var index = 0; index < 3; index++)
            RemoteExecutorHealthLedger.Append(scenario.History,
                new(scenario.Clock.GetUtcNow().AddHours(-1), binding.ExecutorId, "seed-attempt", Lane(0),
                    RemoteLaneOutcomeCode.RemoteRed, null, binding, null));
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(1), Filter(1), 100);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(Lane(1), Assert.Single(scenario.Fake.Requests).Lane);
        Assert.Equal(new[] { 0, 2 }, scenario.LocalStarts.Order().ToArray());
        Assert.Equal(new[]
        {
            $"REMOTE_LANE_POLICY lane=\"{Lane(0)}\" decision=keep-local reason=remote-failing L=120 Ln=3 R=255 Rn=0 F=120 Fn=3 Fage=1.0",
            $"REMOTE_LANE_POLICY lane=\"{Lane(1)}\" decision=offer reason=fits L=120 Ln=3 R=100 Rn=3 F=120 Fn=0",
            $"REMOTE_LANE_POLICY lane=\"{Lane(2)}\" decision=keep-local reason=remote-slower L=120 Ln=3 R=255 Rn=0 F=120 Fn=0"
        }, scenario.Progress.Where(line => line.StartsWith("REMOTE_LANE_POLICY ")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, RemoteExecutorHealthLedger.ReadAll(scenario.History).Count(row => row.Lane == Lane(0)));
    }

    [Xunit.Fact]
    public async Task Wildcard_AllInfrastructureLanesStillHonorExclusiveKeys()
    {
        using var scenario = CreateScenario([120, 120, 120], keyed: true, lanes: ["*"]);
        foreach (var index in Enumerable.Range(0, 3))
            RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(index), Filter(index), 100);

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(new[] { Lane(1), Lane(2) }, scenario.Fake.Requests.Select(request => request.Lane).Order().ToArray());
        Assert.Equal(0, Assert.Single(scenario.LocalStarts));
        var refusal = Assert.Single(RemoteExecutorHealthLedger.ReadAll(scenario.History), row => row.GateAttemptId != "seed-attempt" && row.Lane == Lane(0));
        Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, refusal.Outcome);
        Assert.Equal(2, scenario.Progress.Count(line => line.StartsWith("REMOTE_LANE_POLICY ")));
        Assert.DoesNotContain(scenario.Progress, line => line.StartsWith($"REMOTE_LANE_POLICY lane=\"{Lane(0)}\""));
    }

    [Xunit.Theory]
    [Xunit.InlineData("infrastructure tests: Cli")]
    [Xunit.InlineData("*")]
    public async Task Wildcard_MixedOrRepeatedIsConfigurationFault(string second)
    {
        using var scenario = CreateScenario([120, 120, 120], lanes: ["*", second]);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Empty(scenario.Fake.Requests);
        Assert.Equal(new[] { 0, 1, 2 }, scenario.LocalStarts.Order().ToArray());
        Assert.Equal("REMOTE_LANES_DISABLED reason=invalid", Assert.Single(scenario.Progress));
    }

    [Xunit.Fact]
    public void History_UsesLast500LinesAcceptedRowsAndExactFilterWithReportMedian()
    {
        using var scenario = CreateScenario([120, 120, 120]);
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(0), Filter(0), 999, samples: 3);
        File.AppendAllLines(scenario.History, Enumerable.Repeat("malformed", 494));
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(0), Filter(0), 100, samples: 1);
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(0), Filter(0), 200, samples: 1);
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(0), Filter(0), 300, samples: 1);
        RemoteLaneOfferSeeding.SeedRemote(scenario.History, Lane(0), "changed-filter", 800, samples: 3);
        File.AppendAllLines(scenario.History,
            [File.ReadLines(scenario.History).First().Replace("\"outcome\":\"accepted\"", "\"outcome\":\"remote-red\"", StringComparison.Ordinal)]);
        var rows = GoalAcceptanceVerifier.ReadRemoteLaneOfferHistory(scenario.History);
        var matching = rows[(Lane(0), GoalAcceptanceVerifier.ShortHash(Filter(0)))];
        Assert.Equal(3, matching.Samples);
        Assert.Equal(200, matching.MedianSeconds);
        Assert.Equal(800, rows[(Lane(0), GoalAcceptanceVerifier.ShortHash("changed-filter"))].MedianSeconds);
    }

    private Scenario CreateScenario(double[] local, bool keyed = false, string[]? lanes = null)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = Filter(index), estimatedSerialSeconds = 999,
            exclusiveResourceKeys = keyed ? index switch
            {
                0 => new[] { "xunit:ProcessSpawning" },
                2 => new[] { "xunit:EnvMutation" },
                _ => Array.Empty<string>()
            } : Array.Empty<string>()
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
              }], "forbiddenChangedPathGlobs": []
            }
            """);
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var index in Enumerable.Range(0, 3))
            File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"), $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        var scenario = new Scenario(this, root);
        var localPath = AcceptanceLaneDurationStore.ResolveStorePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllLines(localPath, Enumerable.Range(0, 3).SelectMany(index => Enumerable.Range(0, 3).Select(_ =>
            JsonSerializer.Serialize(new { version = 1, laneName = Lane(index), filterHash = GoalAcceptanceVerifier.ShortHash(Filter(index)), durationSeconds = local[index], recordedAt = scenario.Clock.GetUtcNow() }))));
        var config = Path.Combine(root, "executors.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor", leaseSeconds = 60, slots = 3 } },
            lanes = lanes ?? Enumerable.Range(0, 3).Select(Lane).ToArray(),
            machineLocalResourceKeys = new[] { "xunit:EnvMutation" }
        }));
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = config;
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.RemoteLaneExecutorForTests = scenario.Fake;
        TestOverrides.OnRemoteLaneProgressLineForTests = scenario.Progress.Enqueue;
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";

    private sealed class Scenario : IDisposable
    {
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLanePolicy _owner;
        private FakeRemoteLaneExecutor.Handle? _handle;
        private RemoteLaneResult? _remoteResult;
        private int _slotChecks;
        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLanePolicy owner, string root)
        {
            _owner = owner;
            Root = root;
            Fake.Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
                var index = Enumerable.Range(0, 3).Single(index => request.Lane == Lane(index));
                var directory = Path.Combine(Root, "remote-receipts");
                var filename = $"lane-{index}.trx";
                WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, [$"Lane{index}Tests.Executes"]);
                var result = new RemoteLaneResult(request.ExecutorId, request.Lane, request.FilterHash,
                    request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity,
                    0, [Path.Combine(directory, filename)]);
                if (HoldRemoteUntilLocalStarts) { _handle = handle; _remoteResult = result; }
                else handle.Publish(result);
                return Task.FromResult(new RemoteLaneSubmission(handle));
            };
        }
        internal string Root { get; }
        internal string History => RemoteExecutorHealthLedger.ResolveStorePath(Root);
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal FakeRemoteLaneExecutor Fake { get; } = new();
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<string> Progress { get; } = new();
        internal bool HoldRemoteUntilLocalStarts { get; set; }
        internal bool CheckUnusedSlots { get; set; }
        internal int SlotChecks => _slotChecks;

        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(_owner.TestOverrides, Runner);
            var attempt = Guid.NewGuid().ToString("N");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attempt, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    Path.Combine(Root, ".orchestrator", "attempts", attempt, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root), CancellationToken.None);
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                if (CheckUnusedSlots)
                {
                    using var occupied = RemoteExecutorOccupancy.TryClaimExclusive(Root, "fixture-executor", 0);
                    Assert.Null(occupied); // The offered lane still owns its slot.
                    using var slot1 = RemoteExecutorOccupancy.TryClaimExclusive(Root, "fixture-executor", 1);
                    using var slot2 = RemoteExecutorOccupancy.TryClaimExclusive(Root, "fixture-executor", 2);
                    Assert.NotNull(slot1);
                    Assert.NotNull(slot2);
                    Interlocked.Increment(ref _slotChecks);
                }
                LocalStarts.Enqueue(index);
                WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                if (HoldRemoteUntilLocalStarts && LocalStarts.Count == 2) _handle!.Publish(_remoteResult!);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(output, project + ".exe"), "fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
