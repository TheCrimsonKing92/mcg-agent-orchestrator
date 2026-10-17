using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLaneNoTrx : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Lane = "infrastructure tests: Lane 0";

    [Xunit.Theory]
    [Xunit.InlineData(29, false, "[]", nameof(RemoteLaneOutcomeCode.TrxIncomplete), "exit=29 trx=missing")]
    [Xunit.InlineData(29, true, "[]", nameof(RemoteLaneOutcomeCode.TrxIncomplete), "exit=29 trx=missing")]
    [Xunit.InlineData(29, false, "[\"../x.trx\"]", nameof(RemoteLaneOutcomeCode.Unreachable),
        "RemoteLaneTransportException: invalid-trx-name: ..?x.trx")]
    [Xunit.InlineData(0, false, "[]", nameof(RemoteLaneOutcomeCode.Unreachable),
        "RemoteLaneTransportException: no-trx-produced exitCode=0")]
    public async Task StatusFailureFallsBackAndLocalVerdictDecides(int exitCode, bool localRed,
        string trxJson, string outcome, string reason)
    {
        using var scenario = CreateScenario(exitCode, trxJson, localRed);
        var result = await scenario.RunAsync();
        Assert.Equal(!localRed, result.Passed);
        Assert.Equal(new[] { Lane }, scenario.LocalStarts.ToArray());
        var lane = Assert.Single(result.Checks!.Where(check => check.Name == Lane));
        Assert.Equal(!localRed, lane.Passed);
        Assert.DoesNotContain("remote-executor=", lane.ResultSummary ?? "");
        var rows = scenario.Health();
        var row = Assert.Single(rows.Where(record => record.Outcome != RemoteLaneOutcomeCode.LateAfterFallback));
        Assert.Equal(outcome, row.Outcome.ToString());
        Assert.Equal(reason, row.Reason);
        if (exitCode != 0 && trxJson == "[]")
        {
            // A completed handle remains readable when the verifier polls after local fallback.
            var late = Assert.Single(rows.Where(record => record.Outcome == RemoteLaneOutcomeCode.LateAfterFallback));
            Assert.Equal(row.GateAttemptId, late.GateAttemptId);
            Assert.Equal(row.Lane, late.Lane);
            Assert.Equal(row.Expected, late.Expected);
            Assert.Equal(row.Observed, late.Observed);
        }
        else
            Assert.Single(rows);
        Assert.Contains(scenario.TransportCalls, args => args.Any(arg => arg.EndsWith("/status.json", StringComparison.Ordinal)));
        Assert.DoesNotContain(scenario.TransportCalls, args => args[0] == SshRemoteLaneExecutor.ScpPath &&
            args.Any(arg => arg.EndsWith(".trx", StringComparison.Ordinal)));
        if (trxJson == "[]")
            Assert.DoesNotContain(rows, record => record.Reason?.Contains("invalid-trx-name", StringComparison.Ordinal) == true);
        else
        {
            Assert.Equal("RemoteLaneTransportException: invalid-trx-name: " +
                RemoteLaneStatusValueText.Sanitize("../x.trx"), row.Reason);
            Assert.DoesNotContain(row.Reason, character => character is '/' or '\\' or '"' or '\r' or '\n');
        }
    }

    private Scenario CreateScenario(int exitCode, string trxJson, bool localRed)
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
        var scenario = new Scenario(this, root, exitCode, trxJson, localRed);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(root, "executors.json");
        RemoteLaneOfferSeeding.PrepareFixture(root, TestOverrides);
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

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsSshRemoteLaneNoTrx owner,
        string root, int exitCode, string trxJson, bool localRed) : IDisposable
    {
        internal readonly ConcurrentQueue<string> LocalStarts = new();
        internal readonly ConcurrentQueue<string[]> TransportCalls = new();
        private readonly GoalId _goal = new(Guid.NewGuid().ToString("N"));
        private JsonElement _job;

        internal Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory,
            TimeSpan bound, CancellationToken token)
        {
            TransportCalls.Enqueue(args.ToArray());
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
                using var trx = JsonDocument.Parse(trxJson);
                fields["state"] = "completed"; fields["commitSha"] = _job.GetProperty("sha").GetString();
                fields["treeSha"] = "candidate-tree"; fields["exitCode"] = exitCode; fields["trx"] = trx.RootElement.Clone();
                File.WriteAllText(Path.Combine(destination, "status.json"), JsonSerializer.Serialize(fields));
                File.WriteAllText(Path.Combine(destination, "heartbeat.txt"), "executor-time");
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
