using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsAttributionLaneScope : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttemptScope_DeterminesExactlyWhichLanesExecute(bool scoped)
    {
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = $"FullyQualifiedName~Lane{index}Tests", exclusiveResourceKeys = Array.Empty<string>()
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
        try
        {
            var sourceDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(sourceDirectory);
            foreach (var index in Enumerable.Range(0, 3))
                File.WriteAllText(Path.Combine(sourceDirectory, $"Lane{index}Tests.cs"),
                    $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
            SetPartitionVerdictKeyHooks("scope-tree", "main-sha", "scope-commit");
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
            TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
            var executed = new ConcurrentQueue<int>();
            Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    var index = Enumerable.Range(0, 3).Single(index => args.Any(arg => arg.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                    executed.Enqueue(index);
                    WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                }
                if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
                {
                    const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                    var output = Path.Combine(GetArtifactsPath(args), "bin", projectName, "debug");
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, projectName + ".dll"), "fixture");
                    File.WriteAllText(Path.Combine(output, projectName + ".exe"), "fixture");
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }

            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            var runId = Guid.NewGuid().ToString("N");
            var resultsPrefix = Path.Combine(root, ".orchestrator", "attempts", runId, "result");
            var scope = scoped ? AcceptanceAttributionLaneScope.FromCombinedFailedChecks(["infrastructure tests: Lane 1"]) : null;
            await using var owner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(runId, "scope-cohort", root, "scope-tree", "main-sha", "scope-commit",
                    resultsPrefix, slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(root), options: new AcceptanceRunExecutionOptions(AttributionLaneScope: scope));
            var result = await new GoalAcceptanceVerifier(TestOverrides, Runner).RunOwnedAsync(root, null, null, slot, lease, owner);
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            var expected = scoped ? new[] { 1 } : new[] { 0, 1, 2 };
            Assert.Equal(expected, executed.Order());
            Assert.Equal(expected.Select(index => $"infrastructure tests: Lane {index}"), result.Checks!.Select(check => check.Name).Order(StringComparer.Ordinal));
        }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
