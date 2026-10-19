using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: private roots and overrides; each execution acquires its own stable-slot lease.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteSlotInstrumentation
{
    [Fact]
    public async Task ShadowOccupancyRecordsTwoLocalFallbacksAndRemoteResources()
    {
        using var scenario = new Scenario(3, shadow: true);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        var accepted = Assert.Single(scenario.Health(), row => row.Outcome == RemoteLaneOutcomeCode.Accepted);
        GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneResourceUsage.AssertUsage(accepted);
        var skipped = scenario.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.OfferSkippedNoExecutor).ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.Equal(2, skipped.Select(row => row.Lane).Distinct().Count());
        foreach (var row in skipped)
        {
            Assert.Equal(new RemoteExecutorOccupancyEntry("fixture-executor", 1, 0, 1, 0, 0, "full"), Assert.Single(row.Occupancy!));
            Assert.Equal(1, scenario.LocalStarts.Count(index => scenario.Lane(index) == row.Lane));
        }
        Assert.DoesNotContain(scenario.LocalStarts, index => scenario.Lane(index) == accepted.Lane);
        Assert.Equal(3, scenario.Events.Count(line => line.StartsWith("REMOTE_LANE_POLICY ") && line.Contains("decision=offer")));
        Assert.Equal(2, scenario.Events.Count(line => line.StartsWith("REMOTE_LANE ") && line.Contains("outcome=offer-skipped-no-executor")));
    }

    [Fact]
    public async Task TwoFreeSlotsAcceptBothWithoutNoSlotRows()
    {
        using var scenario = new Scenario(2);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        var rows = scenario.Health();
        Assert.Equal(2, scenario.Progress.Count(line => line.StartsWith("REMOTE_LANE_POLICY ") && line.Contains("decision=offer")));
        Assert.True(rows.Count(row => row.Outcome == RemoteLaneOutcomeCode.Accepted) == 2, JsonSerializer.Serialize(rows));
        Assert.DoesNotContain(rows, row => row.Outcome == RemoteLaneOutcomeCode.OfferSkippedNoExecutor);
        Assert.Empty(scenario.LocalStarts);
        var receipts = result.Checks!.Where(check => check.Name == scenario.Lane(0) || check.Name == scenario.Lane(1))
            .Select(check => Assert.Single(check.TestResultPaths!)).ToArray();
        Assert.Equal(2, receipts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(receipts, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public async Task KeepLocalNeverProducesNoSlotRows()
    {
        using var scenario = new Scenario(2, keepLocal: true);
        Assert.True((await scenario.RunAsync()).Passed);
        Assert.Equal(new[] { 0, 1 }, scenario.LocalStarts.Order());
        Assert.Equal(2, scenario.Progress.Count(line => line.Contains("decision=keep-local")));
        Assert.Empty(scenario.Health());
    }

    [Fact]
    public async Task LegacyStatusKeepsExactAcceptedPropertySet()
    {
        using var scenario = new Scenario(1, resources: false);
        Assert.True((await scenario.RunAsync()).Passed);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(scenario.Ledger)));
        Assert.Equal(new[] { "observed_at", "executor_id", "gate_attempt_id", "lane", "outcome", "fault_owner",
            "expected", "observed", "reason", "version", "attempt", "phases", "test_host_budget" }.Order(),
            json.RootElement.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task FaultedLaneFallsBackOnceWithoutReofferOrNoSlotRow()
    {
        using var scenario = new Scenario(1) { TriggerFailure = true };
        Assert.True((await scenario.RunAsync()).Passed);
        Assert.Equal(0, Assert.Single(scenario.LocalStarts));
        Assert.Equal(RemoteLaneOutcomeCode.Unreachable, Assert.Single(scenario.Health()).Outcome);
        Assert.Single(scenario.Progress, line => line.StartsWith("REMOTE_LANE_POLICY "));
    }

    [Xunit.Collection(TestCollections.JobAccounting)]
    internal sealed class Scenario : GoalAcceptanceVerifierDotnetBuildSlotTests, IDisposable
    {
        private readonly int _lanes;
        private readonly bool _shadow;
        private readonly bool _resources;
        private readonly string _nameSuffix;
        private readonly ConcurrentDictionary<string, JsonElement> _jobs = new();
        private readonly TaskCompletionSource _offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _skipped;
        internal string Root { get; }
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(Root);
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<string> Progress { get; } = new();
        internal ConcurrentQueue<string> Events { get; } = new();
        internal ConcurrentQueue<string> OutcomeEvents { get; } = new();
        internal bool ThrowSink { get; set; }
        internal bool TriggerFailure { get; set; }
        internal string Lane(int index) => "infrastructure tests: Lane " + index + _nameSuffix;
        private static string Filter(int index) => "FullyQualifiedName~Lane" + index + "Tests";

        internal Scenario(int lanes, bool shadow = false, bool resources = true, bool keepLocal = false, string nameSuffix = "")
        {
            _lanes = lanes; _shadow = shadow; _resources = resources; _nameSuffix = nameSuffix;
            SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
            TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
            Root = CreateManifestWorkspace(JsonSerializer.Serialize(new
            {
                version = 1, engine = new
                {
                    maxConcurrentShards = 3,
                    infrastructureTestLanes = Enumerable.Range(0, lanes).Select(index => new { name = "Lane " + index + nameSuffix, filter = Filter(index) }),
                    mtpInvocations = new[] { new
                    {
                        project = RemoteFirstFocusedEvidenceFixture.Project,
                        executablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                        firewallExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.exe",
                        arguments = new[] { "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}" }
                    } }
                },
                checks = new[] { new { name = "infrastructure tests", type = "dotnet-test", runner = "mtp",
                    project = RemoteFirstFocusedEvidenceFixture.Project, timeoutMinutes = 2 } }, forbiddenChangedPathGlobs = Array.Empty<string>()
            }));
            var source = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(source);
            foreach (var index in Enumerable.Range(0, lanes)) File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"),
                $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
            RemoteLaneOfferSeeding.PrepareFixture(Root, TestOverrides);
            if (keepLocal)
            {
                var local = AcceptanceLaneDurationStore.ResolveStorePath(Root);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                File.WriteAllLines(local, Enumerable.Range(0, lanes).SelectMany(index => Enumerable.Range(0, 3).Select(_ =>
                    JsonSerializer.Serialize(new { version = 1, laneName = Lane(index), filterHash = GoalAcceptanceVerifier.ShortHash(Filter(index)),
                        durationSeconds = 10, recordedAt = Clock.GetUtcNow() }))));
            }
            TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(Root, "executors.json");
            File.WriteAllText(TestOverrides.RemoteLaneExecutorConfigurationPathForTests, JsonSerializer.Serialize(new
            {
                executors = new[] { new { id = "fixture-executor", transport = "ssh", runnerAlias = "runner", adminAlias = "admin",
                    remoteRepository = "C:/repo/bare.git", slots = 2 } }, lanes = Enumerable.Range(0, lanes).Select(Lane)
            }));
            TestOverrides.RemoteLaneTimeProviderForTests = Clock;
            TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
            TestOverrides.RemoteLaneTransportRunnerForTests = Transport;
            TestOverrides.RemoteLaneGitRunnerForTests = (_, _, _) => new(0, "", "");
            TestOverrides.OnRemoteLaneProgressLineForTests = Progress.Enqueue;
            TestOverrides.OnRemoteLaneEventForTests = OutcomeEvents.Enqueue;
        }

        private async Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            if (args[0] == SshRemoteLaneExecutor.SshPath) return new(TriggerFailure ? 1 : 0, "");
            if (args[3] == "job.json")
            {
                using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, args[3])));
                _jobs[directory] = job.RootElement.Clone();
            }
            else if (args.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)))
            {
                // This event fences completion behind both no-slot observations, never a timer.
                if (_shadow)
                    try { await _offered.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
                    catch (TimeoutException) { throw new TimeoutException("Missing event: both no-slot offers before remote completion"); }
                var job = _jobs.Single(pair => directory.StartsWith(pair.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || directory == pair.Key).Value;
                var destination = Path.Combine(directory, args[^1]);
                Directory.CreateDirectory(destination);
                var fields = new[] { "attemptId", "executorId", "filterHash", "mainSha", "manifestIdentity", "lane" }
                    .ToDictionary(name => name, name => (object?)job.GetProperty(name).GetString());
                fields["state"] = "completed"; fields["commitSha"] = job.GetProperty("sha").GetString();
                // The host retains receipts by basename, so concurrent lanes need distinct names.
                var index = Enumerable.Range(0, _lanes).Single(index => Lane(index) == job.GetProperty("lane").GetString());
                fields["treeSha"] = "candidate-tree"; fields["exitCode"] = 0; fields["trx"] = new[] { $"remote-lane-{index}.trx" };
                if (_resources)
                {
                    fields["testHostCpuSeconds"] = 12.5; fields["testHostPeakWorkingSetBytes"] = 4096;
                    fields["availableMemoryMbMin"] = 1024; fields["resourceSamples"] = 3;
                }
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(fields));
                File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), "heartbeat");
            }
            else if (args.Any(arg => arg.EndsWith(".trx", StringComparison.Ordinal)))
            {
                var job = _jobs.Single(pair => directory.StartsWith(pair.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || directory == pair.Key).Value;
                var index = Enumerable.Range(0, _lanes).Single(index => Lane(index) == job.GetProperty("lane").GetString());
                WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", args[^1]], 1, [$"Lane{index}Tests.Executes"]);
            }
            return new(0, "");
        }

        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            var attempt = Guid.NewGuid().ToString("N");
            using var shadow = _shadow ? new RemoteLaneCoordinator(
                RemoteLaneExecutorConfiguration.Load(TestOverrides.RemoteLaneExecutorConfigurationPathForTests!),
                new RemoteLaneCandidateIdentity("shadow-attempt", Goal.Value, "verifying-commit", "candidate-tree", "main-sha", ""),
                Root, null, new FakeRemoteLaneExecutor(), Clock, TimeSpan.FromMilliseconds(1), null,
                claimClass: RemoteExecutorClaimClass.Shadow) : null;
            if (shadow is not null) Assert.NotNull(shadow.TryClaimIdleExecutor());
            await using var owner = new AcceptanceAttemptExecutionOwner(new AcceptanceAttemptIdentity(
                attempt, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                Path.Combine(Root, ".orchestrator", "attempts", attempt, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root), options: new(RemoteLaneEventSink: line =>
                {
                    Events.Enqueue(line);
                    if (line.Contains("outcome=offer-skipped-no-executor") && Interlocked.Increment(ref _skipped) == 2) _offered.TrySetResult();
                    if (ThrowSink) throw new InvalidOperationException("fixture sink failed");
                }));
            return await new GoalAcceptanceVerifier(TestOverrides, Runner).RunOwnedAsync(Root, Goal, null, slot, lease, owner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, _lanes).Single(index => args.Any(arg => arg.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
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
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => RemoteExecutorHealthLedger.ReadAll(Ledger);
        public void Dispose() { DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal); DeleteDirectoryWithRetry(Root); }
    }
}
