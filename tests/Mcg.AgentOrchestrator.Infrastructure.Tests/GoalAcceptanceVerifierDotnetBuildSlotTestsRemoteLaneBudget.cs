using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneBudget : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task HostBudget_RemoteGreen_RecordsBudgetAndAllPhases()
    {
        using var scenario = CreateScenario(40);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(1200, scenario.Job.GetProperty("testHostTimeoutSeconds").GetInt32());
        var lane = Assert.Single(result.Checks!.Where(check => check.Name == Lane(0)));
        Assert.StartsWith("remote-executor=", lane.ResultSummary);
        Assert.DoesNotContain(0, scenario.LocalStarts);
        var accepted = Assert.Single(scenario.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.Accepted));
        var budget = Assert.IsType<RemoteLaneTestHostBudgetDecision>(accepted.TestHostBudget);
        Assert.Equal("remote-history", budget.Source);
        Assert.Equal(3, budget.RemoteSamples);
        Assert.Equal(400, budget.RemoteMedianSeconds);
        Assert.Equal(2100, budget.HostBudgetSeconds);
        var phases = Assert.IsType<RemoteLanePhaseTimings>(accepted.Phases);
        Assert.Equal(new RemoteLanePhaseTimings(3, 7, 490, 4.2, 0, 118.5, false, 140, 330, 15), phases);
    }

    [Fact]
    public async Task SmallHostBudget_Submission_CarriesMinimumBudget()
    {
        using var scenario = CreateScenario(2);
        scenario.AdvanceToFetch = false;
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(60, scenario.Job.GetProperty("testHostTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task RunnerTimeout_LocalFallback_DecidesVerdictAndKeepsPhases()
    {
        using var scenario = CreateScenario(40);
        scenario.RunnerTimeout = true;
        scenario.RedLane = 0;
        var result = await scenario.RunAsync();
        Assert.False(result.Passed);
        Assert.Contains(0, scenario.LocalStarts);
        var lane = Assert.Single(result.Checks!.Where(check => check.Name == Lane(0)));
        Assert.False(lane.Passed);
        Assert.DoesNotContain("remote-executor=", lane.ResultSummary ?? "");
        var timeoutRow = Assert.Single(scenario.Health()
            .Where(row => row.Outcome == RemoteLaneOutcomeCode.TrxIncomplete));
        Assert.Equal("exit=29 trx=missing", timeoutRow.Reason);
        var phases = Assert.IsType<RemoteLanePhaseTimings>(timeoutRow.Phases);
        Assert.Equal(905, phases.RunnerSeconds);
        Assert.Equal(118.5, phases.BuildSeconds);
        Assert.Null(phases.TestSeconds);
    }

    private Scenario CreateScenario(int timeoutMinutes)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var root = CreateManifestWorkspace($$"""
            {"version":1,"engine":{
              "maxConcurrentShards":3,
              "infrastructureTestLanes":{{JsonSerializer.Serialize(Enumerable.Range(0, 3).Select(i => new { name = $"Lane {i}", filter = $"FullyQualifiedName~Lane{i}Tests", estimatedSerialSeconds = 600 }))}},
              "mtpInvocations":[{
                "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                "firewallExecutablePathTemplate":"bin/{projectName}/{configuration}/{projectName}.exe",
                "arguments":["{executable}","--results-directory","{resultsDirectory}","--report-trx-filename","{trxFileName}"]}]},
              "checks":[{"name":"infrastructure tests","type":"dotnet-test","runner":"mtp",
                "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "timeoutMinutes":{{timeoutMinutes}}}],"forbiddenChangedPathGlobs":[]}
            """);
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var i in Enumerable.Range(0, 3)) File.WriteAllText(Path.Combine(source, $"Lane{i}Tests.cs"),
            $"public class Lane{i}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        var scenario = new Scenario(this, root);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(root, "executors.json");
        // A 600-second deadline keeps the 495-second phase-accounting timeline on the remote path.
        TestOverrides.RemoteLaneOfferHistoryPathForTests = Path.Combine(root, "offer-history.jsonl");
        RemoteLaneOfferSeeding.SeedRemote(TestOverrides.RemoteLaneOfferHistoryPathForTests,
            Lane(0), "FullyQualifiedName~Lane0Tests", 400);
        File.WriteAllText(TestOverrides.RemoteLaneExecutorConfigurationPathForTests, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor", transport = "ssh", runnerAlias = "runner", adminAlias = "admin",
                remoteRepository = "C:/repo/bare.git", leaseSeconds = 3600 } }, lanes = new[] { Lane(0) }
        }));
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.RemoteLaneTransportRunnerForTests = scenario.Transport;
        TestOverrides.RemoteLaneGitRunnerForTests = scenario.Git;
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneBudget owner, string root) : IDisposable
    {
        internal readonly ConcurrentQueue<int> LocalStarts = new();
        internal readonly GoalId Goal = new(Guid.NewGuid().ToString("N"));
        internal readonly ManualRemoteLaneClock Clock = new();
        internal bool AdvanceToFetch = true;
        internal bool RunnerTimeout;
        internal int? RedLane;
        internal JsonElement Job;
        private DateTimeOffset _start;

        internal GitCli.GitResult Git(string directory, int bound, string[] args)
        { _start = Clock.GetUtcNow(); Clock.Advance(TimeSpan.FromSeconds(1)); return new(0, "", ""); }

        internal Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            if (args[0] == SshRemoteLaneExecutor.SshPath) Clock.Advance(TimeSpan.FromSeconds(1));
            else if (args[3] == "job.json")
            {
                using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "job.json")));
                Job = job.RootElement.Clone();
                Clock.Advance(TimeSpan.FromSeconds(1));
            }
            else if (args.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)))
            {
                var destination = Path.Combine(directory, args[^1]);
                Directory.CreateDirectory(destination);
                var fields = new[] { "attemptId", "executorId", "filterHash", "mainSha", "manifestIdentity", "lane" }
                    .ToDictionary(name => name, name => (object?)Job.GetProperty(name).GetString());
                fields["state"] = "completed"; fields["commitSha"] = Job.GetProperty("sha").GetString();
                fields["treeSha"] = "candidate-tree"; fields["exitCode"] = RunnerTimeout ? 29 : 0;
                fields["trx"] = RunnerTimeout ? Array.Empty<string>() : new[] { "remote-lane.trx" };
                fields["utc"] = _start.AddSeconds(500).ToString("o");
                fields["seconds"] = RunnerTimeout ? 905 : 490;
                fields["phases"] = new { worktreeSeconds = 4.2, buildWaitSeconds = 0, buildSeconds = 118.5, buildSkipped = false };
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(fields));
                File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), "executor-time");
            }
            else if (args[3].EndsWith("/runner.log", StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(directory, args[^1]), "timeout fixture");
            else if (args[3].EndsWith(".trx", StringComparison.Ordinal))
            {
                WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", args[^1]], 1, ["Lane0Tests.Executes"]);
                var path = Path.Combine(directory, args[^1]);
                var trx = XDocument.Load(path);
                trx.Root!.Add(new XElement("Times", new XAttribute("start", _start.AddSeconds(150).ToString("o")),
                    new XAttribute("finish", _start.AddSeconds(480).ToString("o"))));
                trx.Save(path);
                if (AdvanceToFetch) Clock.Advance(_start.AddSeconds(495) - Clock.GetUtcNow());
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        }

        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var attemptId = Guid.NewGuid().ToString("N");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(new AcceptanceAttemptIdentity(
                attemptId, Goal.Value, root, "candidate-tree", "main-sha", "verifying-commit",
                Path.Combine(root, ".orchestrator", "attempts", attemptId, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(root), default);
            return await new GoalAcceptanceVerifier(owner.TestOverrides, Runner).RunOwnedAsync(root, Goal, null, slot, lease, executionOwner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(i => args.Any(arg => arg.Contains($"Lane{i}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (RedLane == index) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(RedLane == index ? 1 : 0,
                    RedLane == index ? "Failed: 1" : "Passed: 1"));
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

        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(root));
        public void Dispose()
        { DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal); DeleteDirectoryWithRetry(root); }
    }
}
