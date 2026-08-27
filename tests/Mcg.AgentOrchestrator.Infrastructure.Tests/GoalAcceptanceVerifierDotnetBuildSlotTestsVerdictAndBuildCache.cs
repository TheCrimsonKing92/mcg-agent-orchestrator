using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsVerdictAndBuildCache : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_reuses_green_partitions_on_reroll")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheReusesGreenPartitionsOnReroll()
    {
        var root = CreateTwoLanePartitionVerdictManifestWorkspace();
        var remainderLaneFilter = ResolveLaneFilter(root, "Remainder");
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        var remainderRuns = 0;
        var previousPrefix = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsInfrastructurePartitionTestCall(args) && args.Contains(remainderLaneFilter))
                {
                    remainderRuns++;
                    return Task.FromResult(remainderRuns < 3
                        ? new GoalAcceptanceVerifier.CommandResult(1, "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1.")
                        : CreatePassingVstestResult(args, "RemainderPartition.Passes"));
                }

                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-one"));
            var first = await verifier.RunAsync(root, goalId);
            Assert.False(first.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            Assert.False(File.Exists(Path.Combine(root, ".orchestrator", "acceptance-partition-verdicts.json")));
            var sharedJournal = GoalOperationJournal.Read(root, goalId);
            Assert.Contains(sharedJournal.Entries, entry =>
                entry.GoalId == goalId &&
                entry.Operation == "acceptance:partition-verdict" &&
                entry.PartitionId == "cli");

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-two"));
            var second = await verifier.RunAsync(root, goalId);
            Assert.False(second.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 1, CountInfrastructurePartitionTestCalls(calls));
            var secondReceipt = Assert.Single(second.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("source_attempt_id=attempt-one", secondReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=remainder,verdict=RED}", secondReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=RED", secondReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("verifying_commit_sha=commit-a", secondReceipt.ResultSummary, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                Path.Combine(root, ".orchestrator", "attempt-three"));
            var third = await verifier.RunAsync(root, goalId);
            Assert.True(third.Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 2,
                CountInfrastructurePartitionTestCalls(calls));
            var thirdReceipt = Assert.Single(third.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("{partition_id=remainder,verdict=GREEN}", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=GREEN", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("before_reroll_wall_time=20-25m", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("after_reroll_wall_time=2-7m", thirdReceipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previousPrefix);
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_records_later_partitions_after_early_failure")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheRecordsLaterPartitionsAfterEarlyFailure()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var cliLaneFilter = ResolveLaneFilter(root, "Cli");
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsInfrastructurePartitionTestCall(args) &&
                    args.Contains(cliLaneFilter))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.False(result.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            var receipt = Assert.Single(result.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("{partition_id=cli,verdict=RED}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=remainder,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=RED", receipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_within_attempt_rerun_tolerates_flaky_partition")]
    public async Task GoalAcceptanceVerifierWithinAttemptRerunToleratesFlakyPartition()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var cliLaneFilter = ResolveLaneFilter(root, "Cli");
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        var cliRuns = 0;
        SetPartitionVerdictKeyHooks("tree-b", "main-b", "commit-b");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsInfrastructurePartitionTestCall(args) &&
                    args.Contains(cliLaneFilter))
                {
                    cliRuns++;
                    // Intermittent flake: fail the first run, pass the within-attempt re-run.
                    if (cliRuns == 2)
                    {
                        WriteVstestTrx(args, "CliPartition.Passes");
                    }

                    return Task.FromResult(cliRuns < 2
                        ? CreateFailedResultWithHeartbeat(
                            goalId,
                            root,
                            "infrastructure tests: Cli",
                            "retry-driving stderr from the original Cli partition")
                        : new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args, "UnrelatedPartition.Passes")
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.True(
                result.Passed,
                string.Join(Environment.NewLine, result.Checks.Select(static check =>
                    $"{check.Name}: classification={check.FailureClassification}; output={check.OutputTail}")));
            Assert.True(result.Retried);
            Assert.Equal(2, cliRuns);
            var unrelatedPartitionCommands = calls
                .Where(IsInfrastructurePartitionTestCall)
                .Where(arguments => !arguments.Contains(cliLaneFilter))
                .Select(arguments => string.Join('\u001f', arguments))
                .ToArray();
            Assert.Equal(
                unrelatedPartitionCommands.Length,
                unrelatedPartitionCommands.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_within_attempt_rerun_tolerates_flaky_mtp_partition")]
    public async Task GoalAcceptanceVerifierWithinAttemptRerunToleratesFlakyMtpPartition()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: Cli", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal", "--filter", "FullyQualifiedName~CliCommandTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("dddddddddddddddddddddddddddddddd");
        var calls = new List<string[]>();
        var mtpRuns = 0;
        SetPartitionVerdictKeyHooks("tree-mtp", "main-mtp", "commit-mtp");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    mtpRuns++;
                    if (mtpRuns == 1)
                    {
                        return Task.FromResult(CreateFailedResultWithHeartbeat(
                            goalId,
                            root,
                            "infrastructure tests: Cli",
                            "retry-driving stderr from the original MTP Cli partition"));
                    }

                    WriteMtpTrx(args, 1, ["CliPartition.Passes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.True(
                result.Passed,
                string.Join(Environment.NewLine, result.Checks.Select(static check =>
                    $"{check.Name}: classification={check.FailureClassification}; output={check.OutputTail}")));
            Assert.True(result.Retried);
            Assert.Equal(2, mtpRuns);
            var partition = Assert.Single(
                result.Checks!,
                check => check.Name.Equals("infrastructure tests: Cli", StringComparison.Ordinal));
            Assert.Equal(1, partition.TestResultRunOrdinal);
            Assert.False(string.IsNullOrWhiteSpace(partition.TestResultAttemptId));
            Assert.DoesNotContain(calls, call =>
                call.Length >= 3 &&
                call[0] == "dotnet" &&
                call[1] == "test" &&
                call[2] == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_exit_zero_cleanup_retains_retry_diagnostic_bytes")]
    public async Task GoalAcceptanceVerifierExitZeroCleanupRetainsRetryDiagnosticBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-retry-diagnostic", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await GoalAcceptanceVerifier.RunProcessForTestsAsync(
                ["powershell", "-NoProfile", "-Command", "[Console]::Error.Write('retry-driving stderr')"],
                root,
                TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(result.StderrPath));
            Assert.Equal("retry-driving stderr", result.Stderr);

            var retained = AcceptanceAttemptArtifactCustody.RetainRetryDiagnostic(
                Path.Combine(root, "attempt", "acceptance"),
                fallbackArtifactsPath: null,
                "exit-zero-run-0",
                result.StderrPath,
                result.Stderr,
                "{\"fallback\":true}");
            Assert.True(File.Exists(retained.Path));
            Assert.Equal("retry-driving stderr", File.ReadAllText(retained.Path));
            Assert.Equal(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(retained.Path))),
                retained.Sha256);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_aggregate_ignores_unrelated_check_failures")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheAggregateIgnoresUnrelatedCheckFailures()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                { "name": "post partition command", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.Length > 1 && args[0] == "git" && args[1] == "diff")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "unrelated failure"));
                }

                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            var result = await verifier.RunAsync(root, goalId);

            Assert.False(result.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            var receipt = Assert.Single(result.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("aggregate_verdict=GREEN", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=cli,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("post partition command", result.Checks!.Single(check => !check.Passed).Name, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_invalidates_on_candidate_or_main_sha_change")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheInvalidatesOnCandidateOrMainShaChange()
    {
        var root = CreateTwoLanePartitionVerdictManifestWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));

            SetPartitionVerdictKeyHooks("tree-b", "main-a", "commit-b");
            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count * 2,
                CountInfrastructurePartitionTestCalls(calls));

            SetPartitionVerdictKeyHooks("tree-b", "main-b", "commit-c");
            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count * 3,
                CountInfrastructurePartitionTestCalls(calls));
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task PartitionCache_ManifestChange_RerunsAllPartitions()
    {
        var root = CreateTwoLanePartitionVerdictManifestWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            var laneCount = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count;
            Assert.Equal(laneCount, CountInfrastructurePartitionTestCalls(calls));

            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var infrastructureCheck = manifest["checks"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(check => check["name"]!.GetValue<string>() == "infrastructure tests");
            infrastructureCheck["timeoutMinutes"] = 2;
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(laneCount * 2, CountInfrastructurePartitionTestCalls(calls));
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_filter_hash_change_runs_only_changed_partition")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheFilterHashChangeRunsOnlyChangedPartition()
    {
        var root = CreateCheckedInManifestShapeWorkspace();
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            CorruptPartitionCacheKey(root, "cli");

            var second = await verifier.RunAsync(root, goalId);
            Assert.True(second.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count + 1, CountInfrastructurePartitionTestCalls(calls));
            var receipt = Assert.Single(second.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("{partition_id=cli,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("{partition_id=goal-acceptance-verifier,verdict=GREEN}", receipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("partition_id=goal-acceptance-verifier,source_attempt_id=", receipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partition_verdict_cache_backstop_forces_full_rerun")]
    public async Task GoalAcceptanceVerifierPartitionVerdictCacheBackstopForcesFullRerun()
    {
        var root = CreateTwoLanePartitionVerdictManifestWorkspace();
        var cliLaneFilter = ResolveLaneFilter(root, "Cli");
        var goalId = new GoalId("12345678123456781234567812345678");
        var calls = new List<string[]>();
        var failFirstPartitionOnForcedRerun = false;
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["engine"]!["partitionVerdictFullRerunEveryN"] = 2;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        SetPartitionVerdictKeyHooks("tree-a", "main-a", "commit-a");
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (failFirstPartitionOnForcedRerun &&
                    IsInfrastructurePartitionTestCall(args) &&
                    args.Contains(cliLaneFilter))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(IsVstestCall(args)
                    ? CreatePassingVstestResult(args)
                    : new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            Assert.True((await verifier.RunAsync(root, goalId)).Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));

            var second = await verifier.RunAsync(root, goalId);
            Assert.True(second.Passed);
            Assert.Equal(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count, CountInfrastructurePartitionTestCalls(calls));
            Assert.Contains("reroll_attempt_count=1", second.Checks!.Single(check => check.Name == "infrastructure partition verdict cache").ResultSummary, StringComparison.Ordinal);

            failFirstPartitionOnForcedRerun = true;
            var third = await verifier.RunAsync(root, goalId);
            Assert.False(third.Passed);
            Assert.Equal(
                AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes.Count * 2,
                CountInfrastructurePartitionTestCalls(calls));
            var thirdReceipt = Assert.Single(third.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.Contains("forced_full_rerun=true", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("reroll_attempt_count=0", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("{partition_id=cli,verdict=RED}", thirdReceipt.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("aggregate_verdict=RED", thirdReceipt.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_uses_base_build_cache_for_unchanged_projects")]
    public async Task GoalAcceptanceVerifierSlotGateUsesBaseBuildCacheForUnchangedProjects()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        var cacheRoot = Path.Combine(root, "base-cache");
        var seedArtifacts = Path.Combine(root, "seed-artifacts");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var mainSha = new string('a', 40);
        string[] restoredProjects =
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj",
            "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj"
        ];
        foreach (var project in restoredProjects)
        {
            WriteProjectArtifacts(seedArtifacts, project, $"cached:{project}");
        }

        cache.Publish(mainSha, seedArtifacts, restoredProjects);
        GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.BaseBuildCacheForTests = cache;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]) ||
                    args.Length > 0 && args[0] == "git")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    var artifactsPath = GetArtifactsPath(args);
                    Assert.DoesNotContain(cacheRoot, artifactsPath, StringComparison.OrdinalIgnoreCase);
                    WriteProjectArtifacts(
                        artifactsPath,
                        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                        "changed");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    return Task.FromResult(CreatePassingVstestResult(args));
                }

                if (args.Contains("--report-trx-filename"))
                {
                    WriteMtpTrx(args, 1, ["GreenPartition.Passes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateNoWorkerResultTests.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            Assert.Single(buildCalls);
            Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", buildCalls[0]);
            Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", buildCalls[0], StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(calls, call => call.Any(arg => arg.Contains(cacheRoot, StringComparison.OrdinalIgnoreCase)));
            var artifactsPath = GetArtifactsPath(buildCalls[0]);
            Assert.True(File.Exists(Path.Combine(artifactsPath, "bin", "Mcg.AgentOrchestrator.Core", "debug_net10.0", "cache.txt")));
            Assert.Contains("BASE_BUILD_CACHE ", output, StringComparison.Ordinal);
            Assert.Contains($"main_sha={mainSha}", output, StringComparison.Ordinal);
            Assert.Contains("build_phase_ms=", output, StringComparison.Ordinal);
            Assert.Contains("Core=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Providers=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.ProviderEnvironment.Tests=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Cli.Tests=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("built_projects=Infrastructure.Tests", output, StringComparison.Ordinal);
            Assert.Contains(result.Checks!, check =>
                check.ResultSummary?.Contains("base-build-cache", StringComparison.Ordinal) == true &&
                check.ResultSummary.Contains($"main_sha={mainSha}", StringComparison.Ordinal) &&
                check.ResultSummary.Contains("build_phase_ms=", StringComparison.Ordinal));
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = null;
            GoalAcceptanceVerifier.BaseBuildCacheForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_uses_base_build_cache_for_verifier_source_scope")]
    public async Task GoalAcceptanceVerifierSlotGateUsesBaseBuildCacheForVerifierSourceScope()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        var cacheRoot = Path.Combine(root, "base-cache");
        var seedArtifacts = Path.Combine(root, "seed-artifacts");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var mainSha = new string('c', 40);
        string[] restoredProjects =
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"
        ];
        foreach (var project in restoredProjects)
        {
            WriteProjectArtifacts(seedArtifacts, project, $"cached:{project}");
        }

        cache.Publish(mainSha, seedArtifacts, restoredProjects);
        GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.BaseBuildCacheForTests = cache;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]) ||
                    args.Length > 0 && args[0] == "git")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    var artifactsPath = GetArtifactsPath(args);
                    Assert.DoesNotContain(cacheRoot, artifactsPath, StringComparison.OrdinalIgnoreCase);
                    WriteProjectArtifacts(artifactsPath, args[2], $"changed:{args[2]}");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    return Task.FromResult(CreatePassingVstestResult(args));
                }

                if (args.Contains("--report-trx-filename"))
                {
                    WriteMtpTrx(args, 1, ["GreenPartition.Passes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            AcceptanceVerificationResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                result = verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            Assert.NotNull(result);
            Assert.True(result!.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            Assert.Equal(7, buildCalls.Length);
            Assert.All(buildCalls, call => Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", call, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(buildCalls, call => call.Contains("src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj"));
            Assert.Contains(buildCalls, call => call.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj"));
            Assert.DoesNotContain(calls, call => call.Any(arg => arg.Contains(cacheRoot, StringComparison.OrdinalIgnoreCase)));
            var artifactsPath = GetArtifactsPath(buildCalls[0]);
            Assert.True(File.Exists(Path.Combine(artifactsPath, "bin", "Mcg.AgentOrchestrator.Core", "debug_net10.0", "cache.txt")));
            Assert.Contains("BASE_BUILD_CACHE ", output, StringComparison.Ordinal);
            Assert.Contains($"main_sha={mainSha}", output, StringComparison.Ordinal);
            Assert.Contains("Core=hit", output, StringComparison.Ordinal);
            Assert.Contains("Core.Tests=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Providers=hit", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure=changed", output, StringComparison.Ordinal);
            Assert.Contains("App=changed", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("Dashboard.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("TestSupport=changed", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.ProviderEnvironment.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Cli.Tests=changed", output, StringComparison.Ordinal);
            Assert.Contains("built_projects=Infrastructure,App,Infrastructure.Tests,Dashboard.Tests,TestSupport,Infrastructure.ProviderEnvironment.Tests,Infrastructure.Cli.Tests", output, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = null;
            GoalAcceptanceVerifier.BaseBuildCacheForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_base_build_cache_receipts_show_structural_cold_then_warm_attempts")]
    public async Task GoalAcceptanceVerifierBaseBuildCacheReceiptsShowStructuralColdThenWarmAttempts()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        var cacheRoot = Path.Combine(root, "base-cache");
        var cache = new DotnetBaseBuildCache(cacheRoot);
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var mainSha = new string('b', 40);
        string[] cacheableProjects =
        [
            "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure.Providers/Mcg.AgentOrchestrator.Infrastructure.Providers.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Mcg.AgentOrchestrator.Infrastructure.OperatorComms.csproj",
            "src/Mcg.AgentOrchestrator.Infrastructure/Mcg.AgentOrchestrator.Infrastructure.csproj",
            "src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.TestSupport/Mcg.AgentOrchestrator.TestSupport.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj"
        ];

        GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = _ => mainSha;
        GoalAcceptanceVerifier.BaseBuildCacheForTests = cache;
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (args.SequenceEqual(["dotnet", "build-server", "shutdown"]) ||
                    args.Length > 0 && args[0] == "git")
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
                }

                if (args.Length >= 3 && args[0] == "dotnet" && args[1] == "build")
                {
                    var artifactsPath = GetArtifactsPath(args);
                    if (args[2].EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var project in cacheableProjects)
                        {
                            WriteProjectArtifacts(artifactsPath, project, $"cold:{project}");
                        }
                    }
                    else
                    {
                        WriteProjectArtifacts(artifactsPath, args[2], $"warm:{args[2]}");
                    }

                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                if (args.Length >= 2 && args[0] == "dotnet" && args[1] == "test")
                {
                    return Task.FromResult(CreatePassingVstestResult(args));
                }

                if (args.Contains("--report-trx-filename"))
                {
                    WriteMtpTrx(args, 1, ["GreenPartition.Passes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            });

            static long ExtractBuildPhaseMilliseconds(string output)
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    output,
                    @"build_phase_ms=(\d+)",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                Assert.True(match.Success, output);
                return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            var firstOutput = AsyncLocalConsoleRouter.Capture(() =>
                verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateNoWorkerResultTests.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());
            var firstBuildArtifactsPath = GetArtifactsPath(calls.First(call =>
                call.Length >= 3 && call[0] == "dotnet" && call[1] == "build"));
            var staleRestoredProjectFile = Path.Combine(
                firstBuildArtifactsPath,
                "bin",
                "Mcg.AgentOrchestrator.Core",
                "debug_net10.0",
                "stale-extra.txt");
            File.WriteAllText(staleRestoredProjectFile, "stale");
            var secondOutput = AsyncLocalConsoleRouter.Capture(() =>
                verifier.RunAsync(
                    root,
                    new GoalId("12345678123456781234567812345678"),
                    changedFiles: ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ChaosGateNoWorkerResultTests.cs"],
                    stableSlotIndex: 0)
                    .GetAwaiter()
                    .GetResult());

            var firstBuildPhaseMs = ExtractBuildPhaseMilliseconds(firstOutput);
            var secondBuildPhaseMs = ExtractBuildPhaseMilliseconds(secondOutput);
            Console.WriteLine(
                $"BASE_BUILD_CACHE_MEASUREMENT main_sha={mainSha} cold_build_phase_ms={firstBuildPhaseMs} warm_build_phase_ms={secondBuildPhaseMs}");
            var buildCalls = calls
                .Where(call => call.Length >= 3 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            Assert.Equal(2, buildCalls.Length);
            Assert.Contains("Mcg.AgentOrchestrator.sln", buildCalls[0], StringComparer.OrdinalIgnoreCase);
            Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", buildCalls[1]);
            Assert.DoesNotContain("Mcg.AgentOrchestrator.sln", buildCalls[1], StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(calls, call => call.Any(arg => arg.Contains(cacheRoot, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains($"main_sha={mainSha}", firstOutput, StringComparison.Ordinal);
            Assert.Contains($"main_sha={mainSha}", secondOutput, StringComparison.Ordinal);
            Assert.Contains("build_phase_ms=", firstOutput, StringComparison.Ordinal);
            Assert.Contains("build_phase_ms=", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Core=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Providers=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.OperatorComms=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("App=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Core.Tests=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Dashboard.Tests=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("TestSupport=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.ProviderEnvironment.Tests=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Cli.Tests=miss", firstOutput, StringComparison.Ordinal);
            Assert.Contains("built_projects=Core,Infrastructure.Providers,Infrastructure.OperatorComms,Infrastructure,App,Core.Tests,Infrastructure.Tests,Dashboard.Tests,TestSupport,Infrastructure.ProviderEnvironment.Tests,Infrastructure.Cli.Tests", firstOutput, StringComparison.Ordinal);
            Assert.Contains("Core=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Providers=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.OperatorComms=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("App=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Core.Tests=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Tests=changed", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Dashboard.Tests=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("TestSupport=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.ProviderEnvironment.Tests=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("Infrastructure.Cli.Tests=hit", secondOutput, StringComparison.Ordinal);
            Assert.Contains("built_projects=Infrastructure.Tests", secondOutput, StringComparison.Ordinal);
            Assert.False(File.Exists(staleRestoredProjectFile));
            Assert.True(firstBuildPhaseMs >= 0, $"Expected non-negative cold build phase receipt; cold={firstBuildPhaseMs}ms");
            Assert.True(secondBuildPhaseMs >= 0, $"Expected non-negative warm build phase receipt; warm={secondBuildPhaseMs}ms");
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveBaseBuildMainShaForTests = null;
            GoalAcceptanceVerifier.BaseBuildCacheForTests = null;
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void WriteVstestTrx(string[] arguments, string testName)
    {
        var resultsDirectoryIndex = Array.IndexOf(arguments, "--results-directory");
        var loggerIndex = Array.IndexOf(arguments, "--logger");
        Assert.True(resultsDirectoryIndex >= 0 && resultsDirectoryIndex + 1 < arguments.Length);
        Assert.True(loggerIndex >= 0 && loggerIndex + 1 < arguments.Length);
        const string prefix = "trx;LogFileName=";
        Assert.StartsWith(prefix, arguments[loggerIndex + 1], StringComparison.OrdinalIgnoreCase);
        var resultsDirectory = arguments[resultsDirectoryIndex + 1];
        Directory.CreateDirectory(resultsDirectory);
        File.WriteAllText(
            Path.Combine(resultsDirectory, arguments[loggerIndex + 1][prefix.Length..]),
            $"<TestRun><TestDefinitions><UnitTest id=\"1\" name=\"{testName}\"><TestMethod className=\"CliPartition\" name=\"Passes\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"1\" testName=\"{testName}\" outcome=\"Passed\" /></Results><ResultSummary outcome=\"Completed\"><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
    }

    private static bool IsVstestCall(string[] arguments) =>
        arguments.Length > 1 &&
        arguments[0] == "dotnet" &&
        arguments[1] == "test";

    private static GoalAcceptanceVerifier.CommandResult CreatePassingVstestResult(
        string[] arguments,
        string testName = "GreenPartition.Passes")
    {
        WriteVstestTrx(arguments, testName);
        return new GoalAcceptanceVerifier.CommandResult(
            0,
            "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1.");
    }

    private static GoalAcceptanceVerifier.CommandResult CreateFailedResultWithHeartbeat(
        GoalId goalId,
        string worktreePath,
        string checkName,
        string stderr)
    {
        var heartbeatPath = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            checkName,
            worktreePath,
            invocationOrdinal: 0);
        var now = DateTimeOffset.UtcNow;
        GateHeartbeatArtifacts.Write(
            heartbeatPath,
            new GateHeartbeatSnapshot(
                goalId.Value,
                "verification-check",
                checkName,
                null,
                Environment.ProcessId,
                null,
                "completed",
                now,
                now,
                now,
                0,
                Encoding.UTF8.GetByteCount(stderr),
                Encoding.UTF8.GetByteCount(stderr),
                "test command",
                1,
                StderrPath: null));
        return new GoalAcceptanceVerifier.CommandResult(
            1,
            "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1.",
            Stderr: stderr);
    }
}
