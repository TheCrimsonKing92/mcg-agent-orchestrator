using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// JobAccounting supplies isolated stable build slots; the command runner never launches a process.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsCohortRerunCache : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Lead = "11111111111111111111111111111111";
    private const string Other = "22222222222222222222222222222222";
    private const string CohortGateIdentity = Lead + ":" + Other;

    [Fact]
    public async Task CohortFailingTrxLaneGetsNarrowedRerun()
    {
        using (var scenario = CreateScenario())
        {
            var result = await scenario.RunAsync(withMembers: true);

            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Equal(2, scenario.LocalStarts.Count(index => index == 0));
            var lane = Assert.Single(result.Checks!, check => check.Name == Lane(0));
            var rerun = Assert.IsType<AcceptanceLaneRerunEvidence>(lane.LaneRerun);
            Assert.Equal(AcceptanceLaneRerunEvidence.Flake, rerun.Outcome);
            Assert.Equal("failing-trx", rerun.FirstPredicate);
            Assert.Equal("Lane0Tests.Executes", Assert.Single(rerun.FirstFailingTestIdentities));
            Assert.Equal(new[] { "Lane0Tests.Executes" }, Assert.Single(scenario.NarrowedMethods));
            Assert.True(File.Exists(Path.Combine(scenario.JournalDirectory, Lead + ".jsonl")));
            Assert.False(File.Exists(Path.Combine(scenario.JournalDirectory, Other + ".jsonl")));
            Assert.Equal(Lead, Assert.Single(new AcceptanceLaneFlakeLedger(scenario.Root).Read(Lane(0))).GoalId);
        }

        // A fresh root proves that the cache and rerun require declared membership.
        using (var scenario = CreateScenario())
        {
            var result = await scenario.RunAsync(withMembers: false);

            Assert.False(result.Passed);
            Assert.Equal(1, scenario.LocalStarts.Count(index => index == 0));
            Assert.False(Assert.Single(result.Checks!, check => check.Name == Lane(0)).Passed);
            Assert.Empty(scenario.NarrowedMethods);
            Assert.Empty(Directory.Exists(scenario.JournalDirectory)
                ? Directory.GetFiles(scenario.JournalDirectory, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }
    }

    private Scenario CreateScenario()
    {
        SetPartitionVerdictKeyHooks("combined-tree", "main-sha", "combined-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, CreateShardWorkspace());
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = Path.Combine(scenario.Root, "executors.json");
        Assert.False(File.Exists(TestOverrides.RemoteLaneExecutorConfigurationPathForTests));
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
            File.WriteAllText(Path.Combine(sourceDirectory, $"Lane{index}Tests.cs"),
                $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        return root;
    }

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsCohortRerunCache owner, string root) : IDisposable
    {
        private int _laneZeroStarts;
        internal string Root { get; } = root;
        internal string JournalDirectory => Path.Combine(Root, ".orchestrator", "goal-operations");
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<string[]> NarrowedMethods { get; } = new();

        internal async Task<AcceptanceVerificationResult> RunAsync(bool withMembers)
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(owner.TestOverrides, Runner);
            var attemptId = Guid.NewGuid().ToString("N");
            var resultsPrefix = Path.Combine(Root, ".orchestrator", "attempts", attemptId, "result");
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attemptId, CohortGateIdentity, Root,
                    "combined-tree", "main-sha", "combined-commit", resultsPrefix, slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root),
                options: new AcceptanceRunExecutionOptions(GateRunIdentity: CohortGateIdentity, CohortRemoteLanes: true,
                    AttemptMemberGoalIds: withMembers ? [new GoalId(Other), new GoalId(Lead)] : null));
            return await verifier.RunOwnedAsync(Root, null, null, slot, lease, executionOwner);
        }

        private Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                var fails = index == 0 && Interlocked.Increment(ref _laneZeroStarts) == 1;
                if (fails)
                {
                    var resultsIndex = Array.IndexOf(args, "--results-directory");
                    var filenameIndex = Array.IndexOf(args, "--report-trx-filename");
                    Assert.True(resultsIndex >= 0 && filenameIndex >= 0);
                    Directory.CreateDirectory(args[resultsIndex + 1]);
                    File.WriteAllText(Path.Combine(args[resultsIndex + 1], args[filenameIndex + 1]), """
                        <TestRun>
                          <TestDefinitions><UnitTest id="1" name="Lane0Tests.Executes"><TestMethod className="Lane0Tests" name="Executes" /></UnitTest></TestDefinitions>
                          <Results><UnitTestResult testId="1" testName="Lane0Tests.Executes" outcome="Failed" /></Results>
                          <ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="0" failed="1" notExecuted="0" /></ResultSummary>
                        </TestRun>
                        """);
                }
                else
                {
                    var methods = args.Select((arg, position) => (arg, position))
                        .Where(pair => pair.arg == "--filter-method")
                        .Select(pair => args[pair.position + 1].Trim('*')).ToArray();
                    if (methods.Length > 0)
                    {
                        NarrowedMethods.Enqueue(methods);
                        WriteMtpTrx(args, methods.Length, methods);
                    }
                    else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(fails ? 1 : 0,
                    fails ? "Failed: 1" : "Passed: 1"));
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

        public void Dispose() => DeleteDirectoryWithRetry(Root);
    }
}
