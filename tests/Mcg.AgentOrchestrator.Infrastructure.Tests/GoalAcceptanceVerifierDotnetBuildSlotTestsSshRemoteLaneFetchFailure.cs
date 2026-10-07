using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLaneFetchFailure : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Lane = "infrastructure tests: Lane 0";
    private const string FetchError = "scp.exe: open local \"x\": No such file or directory";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task FetchFailureFallsBackAndLocalVerdictDecides(bool localRed)
    {
        using var scenario = CreateScenario(localRed);
        var result = await scenario.RunAsync();
        Assert.Equal(!localRed, result.Passed);
        Assert.Equal(new[] { Lane }, scenario.LocalStarts.ToArray());
        var lane = Assert.Single(result.Checks!.Where(check => check.Name == Lane));
        Assert.Equal(!localRed, lane.Passed);
        Assert.DoesNotContain("remote-executor=", lane.ResultSummary ?? "");
        Assert.Equal(1, scenario.Fetches);
        var row = Assert.Single(scenario.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.Unreachable));
        Assert.Contains("RemoteLaneTransportException: result-fetch-failed", row.Reason);
        Assert.Contains("exit=1", row.Reason);
        Assert.Contains(FetchError, row.Reason);
    }

    private Scenario CreateScenario(bool localRed)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 3,
                "infrastructureTestLanes": [{"name": "Lane 0", "filter": "FullyQualifiedName~Lane0Tests"}],
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
        File.WriteAllText(Path.Combine(source, "Lane0Tests.cs"),
            "public class Lane0Tests { [Xunit.Fact] public void Executes() { } }");
        var scenario = new Scenario(this, root, localRed);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(root, "executors.json");
        File.WriteAllText(TestOverrides.RemoteLaneExecutorConfigurationPathForTests, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor", transport = "ssh", runnerAlias = "runner", adminAlias = "admin", remoteRepository = "C:/repo/bare.git" } },
            lanes = new[] { Lane }
        }));
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.RemoteLaneTransportRunnerForTests = scenario.Transport;
        TestOverrides.RemoteLaneGitRunnerForTests = (_, _, _) => new(0, "", "");
        return scenario;
    }

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLaneFetchFailure owner,
        string root, bool localRed) : IDisposable
    {
        internal readonly ConcurrentQueue<string> LocalStarts = new();
        private readonly GoalId _goal = new(Guid.NewGuid().ToString("N"));
        internal int Fetches;
        private JsonElement _job;

        internal Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory,
            TimeSpan bound, CancellationToken token)
        {
            if (args[0] == SshRemoteLaneExecutor.SshPath)
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
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
                fields["treeSha"] = "candidate-tree"; fields["exitCode"] = 0; fields["trx"] = new[] { "remote-lane.trx" };
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(fields));
                File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), "executor-time");
            }
            else
            {
                Interlocked.Increment(ref Fetches);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "", Stderr: FetchError));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        }

        internal async Task<AcceptanceVerificationResult> RunAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var attemptId = Guid.NewGuid().ToString("N");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(new AcceptanceAttemptIdentity(
                attemptId, _goal.Value, root, "candidate-tree", "main-sha", "verifying-commit",
                Path.Combine(root, ".orchestrator", "attempts", attemptId, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(root), default);
            return await new GoalAcceptanceVerifier(owner.TestOverrides, Runner).RunOwnedAsync(root, _goal, null, slot, lease, executionOwner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                Assert.Contains(args, argument => argument.Contains("Lane0Tests", StringComparison.Ordinal));
                LocalStarts.Enqueue(Lane);
                if (localRed) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, ["Lane0Tests.Executes"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(localRed ? 1 : 0,
                    localRed ? "Failed: 1" : "Passed: 1"));
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
        { DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(_goal); DeleteDirectoryWithRetry(root); }
    }
}
