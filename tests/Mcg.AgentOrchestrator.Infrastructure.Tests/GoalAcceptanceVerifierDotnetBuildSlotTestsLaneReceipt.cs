using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns stable slots through the existing JobAccounting collection; fixture roots are per run.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneReceipt : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task RunOwned_TwoLocalLanes_RecordsResourcesAndWorkerLoad()
    {
        var (result, rows, progress) = await RunScenarioAsync(accountingAvailable: true, laneExitCode: 0);
        Assert.True(result.Passed);
        Assert.Equal(2, rows.Length);
        var completions = progress.Where(item => item.Phase == "shard-complete").ToArray();
        Assert.Equal(2, completions.Length);
        foreach (var row in rows)
        {
            var lane = row.GetProperty("lane").GetString()!;
            var expected = lane.EndsWith("Alpha", StringComparison.Ordinal) ? 240000d : 60000d;
            Assert.Equal(expected, row.GetProperty("cpu_ms").GetDouble());
            Assert.Equal(expected, row.GetProperty("wall_ms").GetDouble());
            Assert.Equal(1288490188d, row.GetProperty("peak_mem_bytes").GetDouble());
            Assert.Equal(8192d, row.GetProperty("host_available_memory_mb").GetDouble());
            Assert.Equal("live", row.GetProperty("accounting_source").GetString());
            Assert.Equal(0, row.GetProperty("run_ordinal").GetInt32());
            Assert.Equal(1, row.GetProperty("version").GetInt32());
            Assert.Equal("12345678123456781234567812345678", row.GetProperty("goal_id").GetString());
            var completion = Assert.Single(completions, item => item.CurrentTarget == lane);
            Assert.Equal(TimeSpan.FromMilliseconds(expected), completion.Elapsed);
            Assert.True(completion.LoadContext!.InFlightPaidWorkerDispatchCount.IsAvailable);
            Assert.Equal(3d, completion.LoadContext.InFlightPaidWorkerDispatchCount.Value);
            Assert.Contains("load_inflight_paid_workers=3", GateLoadContextProbe.FormatProgressTokens(completion.LoadContext));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RunOwned_UnavailableAccounting_PreservesLaneVerdicts(int exitCode)
    {
        var control = await RunScenarioAsync(accountingAvailable: true, laneExitCode: exitCode);
        var unavailable = await RunScenarioAsync(accountingAvailable: false, laneExitCode: exitCode);
        Assert.Equal(control.Result.Passed, unavailable.Result.Passed);
        Assert.Equal(control.Result.ExitCode, unavailable.Result.ExitCode);
        Assert.Equal(2, unavailable.Rows.Length);
        var controlLanes = control.Result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal)).ToArray();
        var unavailableLanes = unavailable.Result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, controlLanes.Length);
        Assert.All(controlLanes, check =>
        {
            Assert.Equal(exitCode, check.ExitCode);
            Assert.Equal(exitCode == 0, check.Passed);
        });
        Assert.Equal(controlLanes.Select(check => (check.Name, check.Passed, check.ExitCode)),
            unavailableLanes.Select(check => (check.Name, check.Passed, check.ExitCode)));
        Assert.All(unavailable.Rows, row =>
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty("cpu_ms").ValueKind);
            Assert.Equal("accounting-unavailable", row.GetProperty("cpu_ms_unavailable_reason").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("peak_mem_bytes").ValueKind);
            Assert.Equal("accounting-unavailable", row.GetProperty("peak_mem_bytes_unavailable_reason").GetString());
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RunOwned_AlphaScheduling_ReachesReceiptWithoutChangingVerdicts(int exitCode)
    {
        var control = await RunScenarioAsync(accountingAvailable: true, laneExitCode: exitCode);
        var scheduled = await RunScenarioAsync(accountingAvailable: true, laneExitCode: exitCode, withScheduling: true);
        Assert.Equal(control.Result.Passed, scheduled.Result.Passed);
        Assert.Equal(control.Result.ExitCode, scheduled.Result.ExitCode);
        Assert.Equal(2, control.Rows.Length);
        Assert.Equal(2, scheduled.Rows.Length);
        var alpha = Assert.Single(scheduled.Rows, row => row.GetProperty("lane").GetString()!.EndsWith("Alpha", StringComparison.Ordinal));
        var remainder = Assert.Single(scheduled.Rows, row => row.GetProperty("lane").GetString()!.EndsWith("Remainder", StringComparison.Ordinal));
        Assert.Equal("1/0", alpha.GetProperty("sched_root_throttle_masks").GetString());
        Assert.Equal("not-sampled", remainder.GetProperty("sched_unavailable_reason").GetString());
        Assert.Equal(JsonValueKind.Null, remainder.GetProperty("sched_root_throttle_masks").ValueKind);
        Assert.All(control.Rows, row => Assert.Equal("not-sampled", row.GetProperty("sched_unavailable_reason").GetString()));
        var controlLanes = control.Result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal)).OrderBy(check => check.Name).ToArray();
        var scheduledLanes = scheduled.Result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal)).OrderBy(check => check.Name).ToArray();
        Assert.Equal(2, controlLanes.Length);
        Assert.Equal(2, scheduledLanes.Length);
        Assert.All(controlLanes, check => { Assert.Equal(exitCode, check.ExitCode); Assert.Equal(exitCode == 0, check.Passed); });
        Assert.Equal(controlLanes.Select(check => (check.Name, check.Passed, check.ExitCode)),
            scheduledLanes.Select(check => (check.Name, check.Passed, check.ExitCode)));
    }

    private async Task<(AcceptanceVerificationResult Result, JsonElement[] Rows, AcceptanceGateProgress[] Progress)> RunScenarioAsync(
        bool accountingAvailable, int laneExitCode, bool withScheduling = false)
    {
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
        var progress = new System.Collections.Concurrent.ConcurrentQueue<AcceptanceGateProgress>();
        // Enter the shard batch, while keeping each lane's advance of the shared clock independent.
        var batchLanes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = batchLanes.Enqueue;
        var executedLanes = new List<string>();
        var recordedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            var logs = Path.Combine(HostLedgerStateDirectory.Resolve(root), "logs");
            Directory.CreateDirectory(logs);
            for (var index = 0; index < 3; index++)
                File.WriteAllText(Path.Combine(logs, index + ".heartbeat.json"), JsonSerializer.Serialize(new
                { kind = "worker", state = "running", lastObservedAt = DateTimeOffset.UtcNow }));

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

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                var alpha = filterIndex >= 0 && filterIndex + 1 < args.Length &&
                    args[filterIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                var excludedIndex = Array.IndexOf(args, "--filter-not-class");
                Assert.True(alpha || excludedIndex >= 0 && excludedIndex + 1 < args.Length &&
                    args[excludedIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal),
                    "Expected an inclusive Alpha filter or an exclusive Remainder filter.");
                var lane = "infrastructure tests: " + (alpha ? "Alpha" : "Remainder");
                executedLanes.Add(lane);
                var cpu = alpha ? 240000 : 60000;
                time.Advance(TimeSpan.FromMilliseconds(cpu));
                recordedAt[lane] = time.GetUtcNow();
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(laneExitCode, "deterministic lane verdict",
                    ResourceAccounting: accountingAvailable ? new TaskProcessResourceAccounting(cpu, 1288490188, 0) : null,
                    ResourceAccountingExpected: true,
                    LaneScheduling: withScheduling && alpha ? new(2, 4, "Normal", "1/0", 0, 0, 4, 0, null) : null));
            }

            using var memory = GateHostHealthProbe.PushPhysicalAvailableMbProbe(() => GateLoadSample.Available(8192));
            using var health = GateHostHealthProbe.PushProbe(() => new(GateLoadSample.Unavailable("fixture"), GateLoadSample.Unavailable("fixture")));
            using var gates = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
            using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(() => new(10, 4, 1000));
            var verifier = new GoalAcceptanceVerifier(TestOverrides, Runner, time);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var result = await verifier.RunOwnedAsync(root, new GoalId("12345678123456781234567812345678"),
                changedFiles: null, stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease,
                CancellationToken.None, new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
            Assert.Equal(new[] { "infrastructure tests: Alpha", "infrastructure tests: Remainder" }, batchLanes.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "infrastructure tests: Alpha", "infrastructure tests: Remainder" }, executedLanes.Order(StringComparer.Ordinal));
            var path = GateLaneReceiptLedger.ResolveStorePath(root);
            Assert.True(File.Exists(path), "Local shard completion must persist lane receipts.");
            var rows = File.ReadAllLines(path).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray();
            foreach (var row in rows)
                Assert.Equal(recordedAt[row.GetProperty("lane").GetString()!], row.GetProperty("recorded_at").GetDateTimeOffset());
            return (result, rows, progress.ToArray());
        }
        finally
        {
            TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = null;
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }
}
