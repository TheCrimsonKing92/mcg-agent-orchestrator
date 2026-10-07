using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLane : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task BoundGreenRunsThroughSshWithoutLocalLaneHost()
    {
        using var scenario = CreateScenario();
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        var lane = Assert.Single(result.Checks!.Where(check => check.Name == Lane(0)));
        Assert.StartsWith("remote-executor=", lane.ResultSummary);
        Assert.DoesNotContain(0, scenario.LocalStarts);
        Assert.Equal(new[] { 1, 2 }, scenario.LocalStarts.Order().ToArray());
        Assert.Equal(1, scenario.Pushes);
        Assert.Equal(1, scenario.Triggers);
        Assert.Equal(1, scenario.Fetches);
    }

    [Xunit.Fact]
    public async Task TriggerFailureRunsLocallyAndRecordsExactReason()
    {
        using var scenario = CreateScenario();
        scenario.TriggerFailure = true;
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.Unreachable));
        Assert.Equal("trigger-failed", row.Reason);
        Assert.Equal(0, scenario.Fetches);
    }

    [Xunit.Fact]
    public async Task ExecutorTreeMismatchFallsBackAndLocalRedAloneDecides()
    {
        using var scenario = CreateScenario();
        scenario.Tree = "other-tree";
        scenario.RedLane = 0;
        var result = await scenario.RunAsync();
        Assert.False(result.Passed);
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.BindingMismatchTree));
        Assert.Equal("other-tree", row.Observed!.Tree);
        var lane = Assert.Single(result.Checks!.Where(check => check.Name == Lane(0)));
        Assert.False(lane.Passed);
        Assert.DoesNotContain("remote-executor=", lane.ResultSummary ?? "");
    }

    private Scenario CreateScenario()
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 3,
                "infrastructureTestLanes": {{JsonSerializer.Serialize(Enumerable.Range(0, 3).Select(index => new { name = $"Lane {index}", filter = Filter(index) }))}},
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
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var index in Enumerable.Range(0, 3)) File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"),
            $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        var scenario = new Scenario(this, root);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(root, "executors.json");
        RemoteLaneOfferSeeding.PrepareFixture(root, TestOverrides);
        File.WriteAllText(TestOverrides.RemoteLaneExecutorConfigurationPathForTests, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor", transport = "ssh", runnerAlias = "runner", adminAlias = "admin", remoteRepository = "C:/repo/bare.git" } },
            lanes = new[] { Lane(0) }
        }));
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.RemoteLaneTransportRunnerForTests = scenario.Transport;
        TestOverrides.RemoteLaneGitRunnerForTests = scenario.Git;
        return scenario;
    }
    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLane owner, string root) : IDisposable
    {
        internal readonly ConcurrentQueue<int> LocalStarts = new();
        internal readonly GoalId Goal = new(Guid.NewGuid().ToString("N"));
        internal bool TriggerFailure;
        internal string Tree = "candidate-tree";
        internal int? RedLane;
        internal int Pushes, Triggers, Fetches;
        private JsonElement _job;
        internal GitCli.GitResult Git(string directory, int bound, string[] args)
        { Interlocked.Increment(ref Pushes); return new(0, "", ""); }
        internal Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            if (args[0] == SshRemoteLaneExecutor.SshPath)
            { Interlocked.Increment(ref Triggers); return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(TriggerFailure ? 1 : 0, "")); }
            if (args[3].EndsWith(".json", StringComparison.Ordinal) && !args[3].Contains(':'))
            {
                using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, args[3])));
                _job = job.RootElement.Clone();
            }
            else if (args.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)))
            {
                var destination = Path.Combine(directory, args[^1]);
                Directory.CreateDirectory(destination);
                var fields = new[] { "attemptId", "executorId", "filterHash", "mainSha", "manifestIdentity", "lane" }
                    .ToDictionary(name => name, name => (object?)_job.GetProperty(name).GetString());
                fields["state"] = "completed"; fields["commitSha"] = _job.GetProperty("sha").GetString();
                fields["treeSha"] = Tree; fields["exitCode"] = 0; fields["trx"] = new[] { "remote-lane.trx" };
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(fields));
                File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), "executor-time");
            }
            else
            {
                Interlocked.Increment(ref Fetches);
                WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", args[^1]], 1, ["Lane0Tests.Executes"]);
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
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (RedLane == index) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(RedLane == index ? 1 : 0, RedLane == index ? "Failed: 1" : "Passed: 1"));
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
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(root));
        public void Dispose()
        { DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal); DeleteDirectoryWithRetry(root); }
    }
}
