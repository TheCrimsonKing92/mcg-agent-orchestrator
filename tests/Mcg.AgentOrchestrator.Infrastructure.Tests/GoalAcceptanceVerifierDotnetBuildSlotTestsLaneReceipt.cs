using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

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

    [Theory]
    [InlineData("mtp", false)]
    [InlineData("vstest", false)]
    [InlineData("vstest", true)]
    public async Task Run_DotnetReceipt_WritesNormalizedPeerAfterAttemptCopy(string runner, bool retry)
    {
        await WithReceiptAsync(runner, retry, "readable", failed: false, (receipt, path, baseline) =>
        {
            AssertOutcome(baseline, receipt);
            Assert.True(receipt.Passed);
            Assert.Equal(retry, receipt.LockRemediationApplied);
            var target = Assert.Single(receipt.NormalizedTestResultPaths!);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), target);
            Assert.True(File.Exists(target));
            Assert.Null(receipt.NormalizedTestResultWriteFailure);
            using var document = JsonDocument.Parse(File.ReadAllText(target));
            Assert.Equal("trx", document.RootElement.GetProperty("results")
                .GetProperty("tool").GetProperty("name").GetString());
            var read = new NormalizedTestResultSource().Read(target);
            Assert.Equal(TestResultReadStatus.Readable, read.Status);
            Assert.Single(read.Records);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_UnreadableTrx_RecordsFailureWithoutChangingOutcome(bool failed)
    {
        await WithReceiptAsync("vstest", false, "unreadable", failed, (receipt, path, baseline) =>
        {
            AssertOutcome(baseline, receipt);
            Assert.Null(receipt.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(receipt.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.SourceNotReadable, failure.Reason);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), failure.TargetPath);
            Assert.Equal(new TrxTestResultSource().Read(path).Detail, failure.Detail);
        });
    }

    [Theory]
    [InlineData("mtp", false)]
    [InlineData("mtp", true)]
    [InlineData("vstest", false)]
    [InlineData("vstest", true)]
    public async Task Run_ObstructedNormalizedTarget_PreservesOriginalOutcome(string runner, bool failed)
    {
        await WithReceiptAsync(runner, false, "obstructed", failed, (receipt, path, baseline) =>
        {
            AssertOutcome(baseline, receipt);
            Assert.Equal(!failed, receipt.Passed);
            Assert.Equal(failed ? 2 : 1, receipt.ExecutedTestCount);
            Assert.Equal(failed ? new[] { "Sample.Tests.Fails" } : [], receipt.FailingTestIdentities);
            Assert.Null(receipt.NormalizedTestResultPaths);
            var failure = Assert.IsType<NormalizedTestResultWriteFailure>(receipt.NormalizedTestResultWriteFailure);
            Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
            Assert.Equal(NormalizedTestResultWriter.PathFor(path), failure.TargetPath);
            Assert.True(Directory.Exists(failure.TargetPath));
        });
    }

    private async Task WithReceiptAsync(string runner, bool retry, string sourceMode, bool failed,
        Action<AcceptanceCheckResult, string, AcceptanceCheckResult> assertion)
    {
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "mtpInvocations": [{
                  "project": "tests/Sample.Tests/Sample.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [
                { "name": "normalized TRX", "type": "dotnet-test", "runner": "{{runner}}", "project": "tests/Sample.Tests/Sample.Tests.csproj", "arguments": [] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var attemptDirectory = Path.Combine(root, "attempt");
        var previousAttribution = LockAttribution.AttributeForTests;
        var calls = 0;
        string? sourcePath = null;
        AcceptanceCheckResult? baseline = null;
        try
        {
            // The compatibility owner can retain native reports in the build artifacts while
            // copying completed receipts to its own durable folder. Exercise that real copy.
            await using var owner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity("normalized-trx", "operator", root, "candidate", "main", "commit",
                    Path.Combine(attemptDirectory, "gate"), 0, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(root), publishResultsPrefix: false);
            if (retry)
                LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
                    path, [new BuildLockHolder(null, "foreign-csc", null, false)], "test");
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                var isTest = runner == "mtp" ? IsMtpExecutableCall(args, "Sample.Tests") : IsVstestExecution(args);
                if (!isTest)
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                calls++;
                if (retry && calls == 1)
                    return Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("Build artifacts unavailable"));
                if (runner == "mtp") WriteMtpTrx(args, 1, ["Sample.Tests.Passes"]);
                else WriteVstestTrx(args, "Sample.Tests.Passes");
                var directoryIndex = Array.IndexOf(args, "--results-directory");
                var fileIndex = Array.IndexOf(args, runner == "mtp" ? "--report-trx-filename" : "--logger");
                sourcePath = Path.Combine(args[directoryIndex + 1], runner == "mtp"
                    ? args[fileIndex + 1] : args[fileIndex + 1]["trx;LogFileName=".Length..]);
                Assert.True(File.Exists(sourcePath));
                if (sourceMode == "unreadable") File.WriteAllText(sourcePath, "not XML");
                else if (failed) File.WriteAllText(sourcePath, NormalizedGateResultStepTests.Trx);
                if (sourceMode == "obstructed")
                {
                    var target = NormalizedTestResultWriter.PathFor(Path.Combine(attemptDirectory, Path.GetFileName(sourcePath)));
                    Directory.CreateDirectory(target);
                    Assert.True(Directory.Exists(target));
                }
                var command = new GoalAcceptanceVerifier.CommandResult(failed ? 1 : 0,
                    failed ? "Failed: 1" : "Passed: 1");
                // Capture the native outcome before the production normalization step can run.
                var evidence = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence([sourcePath]);
                var passed = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(command, evidence,
                    requireTrxEvidence: false).Passed;
                baseline = new AcceptanceCheckResult("normalized TRX", passed, command.ExitCode, null,
                    ExecutedTestCount: evidence.ExecutedTestCount,
                    FailingTestIdentities: passed ? [] : GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities([sourcePath]));
                return Task.FromResult(command);
            });

            var result = await verifier.RunOwnedAsync(root, null, null, 0, null, owner);

            Assert.Equal(retry ? 2 : 1, calls);
            var receipt = Assert.Single(result.Checks!);
            var durableTrx = Assert.Single(receipt.TestResultPaths!);
            Assert.Equal(attemptDirectory, Path.GetDirectoryName(durableTrx));
            Assert.NotEqual(sourcePath, durableTrx);
            Assert.True(File.Exists(durableTrx));
            Assert.Equal(File.ReadAllText(sourcePath!), File.ReadAllText(durableTrx));
            Assert.False(File.Exists(NormalizedTestResultWriter.PathFor(sourcePath!)));
            assertion(receipt, durableTrx, Assert.IsType<AcceptanceCheckResult>(baseline));
        }
        finally
        {
            LockAttribution.AttributeForTests = previousAttribution;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void AssertOutcome(AcceptanceCheckResult baseline, AcceptanceCheckResult receipt)
    {
        Assert.Equal(baseline.Passed, receipt.Passed);
        Assert.Equal(baseline.ExitCode, receipt.ExitCode);
        Assert.Equal(baseline.ExecutedTestCount, receipt.ExecutedTestCount);
        Assert.Equal(baseline.FailingTestIdentities, receipt.FailingTestIdentities);
    }
}
