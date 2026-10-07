using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneTiming : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task AcceptedDiagnostics_ReportRemoteTimingWithoutLocalSample()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        DiagnosticHandle? handle = null;
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                handle = new DiagnosticHandle(scenario.Clock.GetUtcNow(), scenario.RemoteResult(request), "{\"seconds\":187.5}");
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(Lane(0), Assert.Single(fake.Requests).Lane);
        Assert.NotNull(handle);
        Assert.Equal(1, handle.SnapshotCount);
        Assert.DoesNotContain(0, scenario.LocalStarts);
        var remoteProgress = Assert.Single(scenario.GateProgress.Where(item => item.Phase == "remote-shard-complete"));
        Assert.Equal(Lane(0), remoteProgress.CurrentTarget);
        Assert.Equal(TimeSpan.FromSeconds(187.5), remoteProgress.Elapsed);
        var localProgress = scenario.GateProgress.Where(item => item.Phase == "shard-complete").ToArray();
        Assert.DoesNotContain(localProgress, item => item.CurrentTarget == Lane(0));
        Assert.Single(localProgress.Where(item => item.CurrentTarget == Lane(1)));
        Assert.Single(localProgress.Where(item => item.CurrentTarget == Lane(2)));
        var breakdown = Assert.Single(scenario.GateProgress.Where(item => item.Phase == "gate-phase-breakdown"));
        Assert.Contains("remote_lanes=1", breakdown.CurrentTarget, StringComparison.Ordinal);
        Assert.Contains("longest_remote_lane_ms=187500", breakdown.CurrentTarget, StringComparison.Ordinal);
        var rows = File.ReadAllLines(AcceptanceLaneDurationStore.ResolveStorePath(scenario.Root)).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("laneName").GetString();
        }).ToArray();
        Assert.Contains(Lane(1), rows);
        Assert.Contains(Lane(2), rows);
        Assert.DoesNotContain(Lane(0), rows);
    }

    [Xunit.Fact]
    public async Task AcceptedWithoutDiagnostics_ReportsInjectedClockElapsed()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                scenario.Clock.Advance(TimeSpan.FromSeconds(42));
                var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
                handle.Publish(scenario.RemoteResult(request));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await scenario.RunAsync();

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(Lane(0), Assert.Single(fake.Requests).Lane);
        var progress = Assert.Single(scenario.GateProgress.Where(item => item.Phase == "remote-shard-complete"));
        Assert.Equal(Lane(0), progress.CurrentTarget);
        Assert.Equal(TimeSpan.FromSeconds(42), progress.Elapsed);
        Assert.DoesNotContain(0, scenario.LocalStarts);
        Assert.StartsWith("remote-executor=", Assert.Single(result.Checks!.Where(check => check.Name == Lane(0))).ResultSummary!);
    }

    private Scenario CreateScenario()
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, CreateShardWorkspace());
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        RemoteLaneOfferSeeding.PrepareFixture(scenario.Root, TestOverrides);
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";

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
            File.WriteAllText(Path.Combine(sourceDirectory, $"Lane{index}Tests.cs"), $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        return root;
    }

    private sealed class DiagnosticHandle(DateTimeOffset heartbeat, RemoteLaneResult result, string statusJson)
        : IRemoteLaneHandle, IRemoteLaneAttemptDiagnosticsSource
    {
        internal int SnapshotCount { get; private set; }
        public DateTimeOffset? NewestHeartbeat => heartbeat;
        public RemoteLaneResult? TryGetResult() => result;
        public void Abandon() { }
        public RemoteLaneHandleDiagnostics Snapshot()
        {
            SnapshotCount++;
            using var document = JsonDocument.Parse(statusJson);
            return new(new RemoteLanePollSummary(1, 0, "completed", null, null), [], document.RootElement.Clone());
        }
        public Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteLaneRunnerLogCapture(null));
    }

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneTiming owner, string root) : IDisposable
    {
        internal string Root { get; } = root;
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        private string RemoteDirectory => Path.Combine(Root, "remote-receipts");
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<AcceptanceGateProgress> GateProgress { get; } = new();
        internal void Configure() => File.WriteAllText(ConfigurationPath,
            JsonSerializer.Serialize(new { executors = new[] { new { id = "fixture-executor", leaseSeconds = 60 } }, lanes = new[] { Lane(0) } }));
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
                AcceptanceGateEngineSettings.Load(Root), CancellationToken.None,
                new AcceptanceRunExecutionOptions(ProgressSink: GateProgress.Enqueue));
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
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
        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            string[] args = ["--results-directory", RemoteDirectory, "--report-trx-filename", filename];
            WriteMtpTrx(args, 1, ["Lane0Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(RemoteDirectory, filename)]);
        }
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
