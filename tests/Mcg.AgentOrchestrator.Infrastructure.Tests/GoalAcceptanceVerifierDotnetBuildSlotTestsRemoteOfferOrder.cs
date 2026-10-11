using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses the stable-slot and verifier override isolation of the existing executor claim tests.
[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteOfferOrder : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task ChangedFilter_NameHistory_OffersRemainderBeforeShortLanes()
    {
        using var scenario = CreateScenario();
        var remainder = scenario.Lanes.Single(lane => lane.Name == "Remainder");
        scenario.SeedOldFilterDurations(remainder, [590, 600, 605, 610, 620]);
        var fake = HoldRemoteResultsUntilLocalStart(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await RunToCompletion(scenario);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(remainder.FullName, fake.Requests.First().Lane);
        Assert.Equal(new[] { remainder.FullName, "infrastructure tests: Short A" },
            fake.Requests.Select(request => request.Lane));
        AssertPolicyOrder(scenario, remainder.FullName, "infrastructure tests: Short A",
            "infrastructure tests: Short B", "infrastructure tests: Short C");
        // Sorting and the policy both use the 605 second name-level median.
        Assert.Contains("decision=offer reason=fits L=605 Ln=5", scenario.PolicyLines.First());
    }

    [Fact]
    public async Task EmptyJournal_AssignsInManifestEstimateOrder()
    {
        using var scenario = CreateScenario();
        Assert.False(File.Exists(AcceptanceLaneDurationStore.ResolveStorePath(scenario.Root)));
        var fake = HoldRemoteResultsUntilLocalStart(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await RunToCompletion(scenario);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(2, fake.Requests.Count);
        var expected = scenario.Lanes.OrderByDescending(lane => lane.Estimate).Select(lane => lane.FullName).ToArray();
        Assert.Equal(expected.Take(fake.Requests.Count), fake.Requests.Select(request => request.Lane));
        AssertPolicyOrder(scenario, expected);
    }

    [Fact]
    public async Task KeepLocalLane_ShortNameHistory_IsNeverAssigned()
    {
        using var scenario = CreateScenario(includeKeepLocalLane: true);
        var tiny = scenario.Lanes.Single(lane => lane.Name == "Tiny");
        scenario.SeedOldFilterDurations(tiny, [20, 25, 30, 35, 40]);
        var fake = HoldRemoteResultsUntilLocalStart(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await RunToCompletion(scenario);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(2, fake.Requests.Count);
        AssertPolicyOrder(scenario, "infrastructure tests: Short A", "infrastructure tests: Short B",
            "infrastructure tests: Short C", "infrastructure tests: Remainder", tiny.FullName);
        Assert.Contains("decision=keep-local reason=short L=30 Ln=5", scenario.PolicyLines.Last());
        Assert.DoesNotContain(fake.Requests, request => request.Lane == tiny.FullName);
        Assert.Contains(tiny.FullName, scenario.LocalStarts);
    }

    [Fact]
    public async Task ShortManifest_LongNameHistory_IsOfferedToExecutor()
    {
        using var scenario = CreateScenario(includeKeepLocalLane: true);
        var tiny = scenario.Lanes.Single(lane => lane.Name == "Tiny");
        scenario.SeedOldFilterDurations(tiny, [590, 600, 605, 610, 620]);
        var fake = HoldRemoteResultsUntilLocalStart(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await RunToCompletion(scenario);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(tiny.FullName, fake.Requests.First().Lane);
        Assert.StartsWith($"REMOTE_LANE_POLICY lane=\"{tiny.FullName}\" ", scenario.PolicyLines.First());
        Assert.Contains("decision=offer reason=fits L=605 Ln=5", scenario.PolicyLines.First());
        Assert.DoesNotContain("keep-local reason=short", scenario.PolicyLines.First());
    }

    [Fact]
    public async Task TwoNameLevelSamples_UsesManifestEstimate()
    {
        using var scenario = CreateScenario();
        var remainder = scenario.Lanes.Single(lane => lane.Name == "Remainder");
        scenario.SeedOldFilterDurations(remainder, [600]);
        scenario.SeedCurrentFilterDurations(remainder, [605]);
        var fake = HoldRemoteResultsUntilLocalStart(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await RunToCompletion(scenario);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.StartsWith($"REMOTE_LANE_POLICY lane=\"{remainder.FullName}\" ", scenario.PolicyLines.Last());
        Assert.Contains("decision=offer reason=fits L=62 Ln=1", scenario.PolicyLines.Last());
    }

    [Fact]
    public async Task EmptyJournal_PolicyLinesReportManifestEstimates()
    {
        using var scenario = CreateScenario();
        Assert.False(File.Exists(AcceptanceLaneDurationStore.ResolveStorePath(scenario.Root)));
        var fake = HoldRemoteResultsUntilLocalStart(scenario);
        TestOverrides.RemoteLaneExecutorForTests = fake;

        var result = await RunToCompletion(scenario);

        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(2, fake.Requests.Count);
        var expected = scenario.Lanes.OrderByDescending(lane => lane.Estimate).ToArray();
        Assert.Equal(expected.Take(2).Select(lane => lane.FullName), fake.Requests.Select(request => request.Lane));
        AssertPolicyOrder(scenario, expected.Select(lane => lane.FullName).ToArray());
        var lines = scenario.PolicyLines.ToArray();
        for (var index = 0; index < expected.Length; index++)
            Assert.Contains($"decision=offer reason=fits L={expected[index].Estimate} Ln=0 ", lines[index]);
    }

    private Scenario CreateScenario(bool includeKeepLocalLane = false)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, includeKeepLocalLane);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        TestOverrides.RemoteLaneOfferHistoryPathForTests = scenario.OfferHistoryPath;
        foreach (var lane in scenario.Lanes)
            RemoteLaneOfferSeeding.SeedRemote(scenario.OfferHistoryPath, lane.FullName, lane.Filter, 100);
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.OnRemoteLaneProgressLineForTests = line =>
        {
            if (line.StartsWith("REMOTE_LANE_POLICY ", StringComparison.Ordinal))
                scenario.PolicyLines.Enqueue(line);
        };
        return scenario;
    }

    private static void AssertPolicyOrder(Scenario scenario, params string[] expected)
    {
        var lines = scenario.PolicyLines.ToArray();
        Assert.Equal(expected.Length, lines.Length);
        for (var index = 0; index < expected.Length; index++)
            Assert.StartsWith($"REMOTE_LANE_POLICY lane=\"{expected[index]}\" ", lines[index]);
    }

    private static FakeRemoteLaneExecutor HoldRemoteResultsUntilLocalStart(Scenario scenario)
    {
        var submissions = new List<(RemoteLaneRequest Request, FakeRemoteLaneExecutor.Handle Handle)>();
        var gate = new object();
        var published = false;
        // The first local start happens after the offer pass. Keep both remote claims occupied
        // through that pass so completed fake results cannot admit a third lane prematurely.
        scenario.BeforeLocal = () =>
        {
            lock (gate)
            {
                if (published) return;
                Assert.Equal(2, submissions.Count);
                foreach (var submission in submissions)
                    submission.Handle.Publish(scenario.RemoteResult(submission.Request));
                published = true;
            }
        };
        return new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
                lock (gate) submissions.Add((request, handle));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
    }

    private static async Task<AcceptanceVerificationResult> RunToCompletion(Scenario scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var run = scenario.RunAsync(cancellation.Token);
        try { return await Event(run, "gate completed after ordered lane offers"); }
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

    private sealed record Lane(string Name, string TestClass, double Estimate)
    {
        internal string FullName => "infrastructure tests: " + Name;
        internal string Filter => "FullyQualifiedName~" + TestClass;
        internal GoalAcceptanceVerifier.AcceptanceManifestCheck Check(string filter) => new()
        {
            Name = FullName,
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", filter],
            EstimatedSerialSeconds = Estimate
        };
    }

    private sealed class Scenario : IDisposable
    {
        private const string Project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteOfferOrder _owner;
        private GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal string Root { get; }
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal string OfferHistoryPath => Path.Combine(Root, "offer-history.jsonl");
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal IReadOnlyList<Lane> Lanes { get; }
        internal ConcurrentQueue<string> PolicyLines { get; } = new();
        internal ConcurrentQueue<string> LocalStarts { get; } = new();
        internal Action? BeforeLocal { get; set; }

        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteOfferOrder owner, bool includeKeepLocalLane)
        {
            _owner = owner;
            var lanes = new List<Lane>
            {
                new("Remainder", "RemainderTests", 62),
                new("Short C", "ShortCTests", 70),
                new("Short A", "ShortATests", 120),
                new("Short B", "ShortBTests", 95)
            };
            if (includeKeepLocalLane) lanes.Add(new("Tiny", "TinyTests", 30));
            Lanes = lanes;
            Root = CreateManifestWorkspace(JsonSerializer.Serialize(new
            {
                version = 1,
                engine = new
                {
                    maxConcurrentShards = 3,
                    infrastructureTestLanes = lanes.Select(lane => new
                    {
                        name = lane.Name, filter = lane.Filter, estimatedSerialSeconds = lane.Estimate
                    }),
                    mtpInvocations = new[]
                    {
                        new
                        {
                            project = $"tests/{Project}/{Project}.csproj",
                            executablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                            firewallExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.exe",
                            arguments = new[] { "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}" }
                        }
                    }
                },
                checks = new[]
                {
                    new { name = "infrastructure tests", type = "dotnet-test", runner = "mtp", project = $"tests/{Project}/{Project}.csproj", timeoutMinutes = 2 }
                },
                forbiddenChangedPathGlobs = Array.Empty<string>()
            }));
            var source = Path.Combine(Root, "tests", Project);
            Directory.CreateDirectory(source);
            foreach (var lane in lanes)
                File.WriteAllText(Path.Combine(source, lane.TestClass + ".cs"),
                    $"public class {lane.TestClass} {{ [Xunit.Fact] public void Executes() {{ }} }}");
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new
            {
                executors = new[] { new { id = "one", slots = 1 }, new { id = "two", slots = 1 } },
                lanes = lanes.Select(lane => lane.FullName)
            }));
        }

        internal void SeedOldFilterDurations(Lane lane, IReadOnlyList<double> samples) =>
            SeedDurations(lane.Check("FullyQualifiedName~Old" + lane.TestClass), samples);

        internal void SeedCurrentFilterDurations(Lane lane, IReadOnlyList<double> samples) =>
            SeedDurations(lane.Check(lane.Filter), samples);

        private void SeedDurations(GoalAcceptanceVerifier.AcceptanceManifestCheck check, IReadOnlyList<double> samples)
        {
            using var scope = AcceptanceLaneDurationStore.PushRecordingScope(Root);
            foreach (var seconds in samples)
                AcceptanceLaneDurationStore.Record(check, new AcceptanceCheckResult(check.Name, true, 0, null), TimeSpan.FromSeconds(seconds));
            AcceptanceLaneDurationStore.Flush();
        }

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
            if (IsMtpExecutableCall(args, Project))
            {
                var lane = Lanes.Single(lane => args.Any(argument =>
                    argument.Contains(lane.TestClass, StringComparison.Ordinal)));
                LocalStarts.Enqueue(lane.FullName);
                BeforeLocal?.Invoke();
                WriteMtpTrx(args, 1, [lane.TestClass + ".Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                var output = Path.Combine(GetArtifactsPath(args), "bin", Project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, Project + ".dll"), "deterministic fixture");
                File.WriteAllText(Path.Combine(output, Project + ".exe"), "deterministic fixture");
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        }

        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var lane = Lanes.Single(lane => lane.FullName == request.Lane);
            var directory = Path.Combine(Root, "remote-receipts");
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", filename], 1, [lane.TestClass + ".Executes"]);
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
