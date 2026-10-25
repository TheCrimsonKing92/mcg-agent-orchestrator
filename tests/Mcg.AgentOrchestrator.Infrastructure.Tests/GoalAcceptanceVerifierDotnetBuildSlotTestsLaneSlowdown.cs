using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns stable slots and global verifier hooks in the same collection as LaneReceipt.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneSlowdown : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Theory]
    [InlineData(3, 120000, 0, "slow")]
    [InlineData(3, 80000, 0, "not-slow")]
    [InlineData(2, 120000, 0, "insufficient-history")]
    [InlineData(3, 120000, 0, "no-test-data")]
    [InlineData(3, 120000, 7, "slow")]
    public async Task RunOwned_LaneCompletion_ReportsSlowdownWithoutChangingVerdict(
        int priorRuns, int wallMs, int exitCode, string status)
    {
        const string lane = "infrastructure tests: Alpha";
        var identities = new[] { "Git.Commit", "Git.Status", "Git.Fetch" };
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        var identity = Guid.NewGuid().ToString("N");
        SetPartitionVerdictKeyHooks("tree-" + identity, "main-" + identity, "commit-" + identity);
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests", "exclusiveResourceKeys": ["fixture-clock"] },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~AlphaShardTests&Category!=HostIntegration", "exclusiveResourceKeys": ["fixture-clock"] }
                ],
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var time = new RecordingTimeProvider();
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
        var executed = new ConcurrentQueue<string>();
        var admitted = new ConcurrentQueue<string>();
        TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = admitted.Enqueue;
        try
        {
            for (var index = 0; index < priorRuns; index++)
                Assert.Null(GateLaneBaselineStore.TryAppend(root, lane,
                    new(DateTimeOffset.UnixEpoch.AddMinutes(index), 60000,
                        identities.ToDictionary(test => test, _ => 4000d)), true));
            Assert.Equal(priorRuns, GateLaneBaselineStore.ReadHistory(root, lane).Count);

            Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string _, CancellationToken cancellation)
            {
                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                    var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture");
                    File.WriteAllText(Path.Combine(output, project + (OperatingSystem.IsWindows() ? ".exe" : "")), "fixture");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }
                if (!args.Contains("--report-trx-filename", StringComparer.Ordinal))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                var filterIndex = Array.IndexOf(args, "--filter-class");
                var alpha = filterIndex >= 0 && args[filterIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                var excluded = Array.IndexOf(args, "--filter-not-class");
                Assert.True(alpha || excluded >= 0 && args[excluded + 1].Contains("AlphaShardTests", StringComparison.Ordinal));
                executed.Enqueue(alpha ? lane : "infrastructure tests: Remainder");
                WriteMtpTrx(args, executedTestCount: 3, identities);
                var path = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
                    args[Array.IndexOf(args, "--report-trx-filename") + 1]);
                var document = XDocument.Load(path);
                foreach (var row in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
                    row.SetAttributeValue("duration", alpha && status == "no-test-data" ? null
                        : TimeSpan.FromMilliseconds(alpha ? 12000 : 4000).ToString("c", CultureInfo.InvariantCulture));
                document.Save(path);
                time.Advance(TimeSpan.FromMilliseconds(alpha ? wallMs : 60000));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(alpha ? exitCode : 0, "deterministic lane verdict"));
            }

            using var memory = GateHostHealthProbe.PushPhysicalAvailableMbProbe(() => GateLoadSample.Available(8192));
            using var health = GateHostHealthProbe.PushProbe(() => new(GateLoadSample.Unavailable("fixture"), GateLoadSample.Unavailable("fixture")));
            using var gates = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
            using var cpu = GateLoadContextProbe.PushHostCpuProbe(() => new(10, 4, 1000));
            var verifier = new GoalAcceptanceVerifier(TestOverrides, Runner, time);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var result = await verifier.RunOwnedAsync(root, new GoalId("12345678123456781234567812345678"),
                changedFiles: null, stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease,
                CancellationToken.None, new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
            Assert.Equal(new[] { lane, "infrastructure tests: Remainder" }, admitted.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { lane, "infrastructure tests: Remainder" }, executed.Order(StringComparer.Ordinal));
            var check = Assert.Single(result.Checks!, item => item.Name == lane);
            Assert.Equal(exitCode, check.ExitCode);
            Assert.Equal(exitCode == 0, check.Passed);
            Assert.Equal(exitCode == 0, result.Passed);
            var receipt = Assert.Single(ReadRows(GateLaneReceiptLedger.ResolveStorePath(root)), row => row.GetProperty("lane").GetString() == lane);
            Assert.Equal(status, receipt.GetProperty("slowdown_status").GetString());
            Assert.Equal(26, receipt.EnumerateObject().Count());
            Assert.Equal(wallMs, receipt.GetProperty("wall_ms").GetDouble());
            var slowdownsPath = Path.Combine(HostLedgerStateDirectory.Resolve(root), GateLaneSlowdownDiff.FileName);
            var emitted = progress.Where(item => item.Phase == "lane-slowdown").ToArray();
            if (status == "slow")
            {
                var row = Assert.Single(ReadRows(slowdownsPath));
                Assert.Equal(lane, row.GetProperty("lane").GetString());
                Assert.Equal(1, row.GetProperty("version").GetInt32());
                Assert.Equal(2, row.GetProperty("ratio").GetDouble());
                Assert.Equal(2, receipt.GetProperty("slowdown_ratio").GetDouble());
                Assert.Equal(identities.Order(StringComparer.Ordinal), row.GetProperty("grown").EnumerateArray()
                    .Select(test => test.GetProperty("test").GetString()).Order(StringComparer.Ordinal));
                var detail = ShardCompleteLoadFieldsTests.WriteDetail(Assert.Single(emitted));
                Assert.Contains(lane, detail);
                Assert.Contains("ratio=2.00", detail);
                Assert.Contains("median_wall_ms=60000", detail);
                Assert.All(identities, test => Assert.Contains(test, detail));
                Assert.True(detail.Length <= 400, detail);
            }
            else
            {
                Assert.False(File.Exists(slowdownsPath));
                Assert.Empty(emitted);
                Assert.Equal(JsonValueKind.Null, receipt.GetProperty("slowdown_ratio").ValueKind);
            }
            Assert.Equal(priorRuns + (exitCode == 0 ? 1 : 0), GateLaneBaselineStore.ReadHistory(root, lane).Count);
            if (exitCode != 0) Assert.All(GateLaneBaselineStore.ReadHistory(root, lane), run => Assert.Equal(60000, run.WallMs));
        }
        finally
        {
            TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = null;
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    private static JsonElement[] ReadRows(string path) => File.ReadAllLines(path).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();
}
