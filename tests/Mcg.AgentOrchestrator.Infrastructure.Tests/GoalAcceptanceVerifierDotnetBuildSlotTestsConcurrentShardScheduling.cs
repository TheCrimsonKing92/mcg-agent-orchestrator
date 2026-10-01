using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsConcurrentShardScheduling : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_partitions_checked_in_infrastructure_manifest_check")]
    public async Task GoalAcceptanceVerifierPartitionsCheckedInInfrastructureManifestCheck()
    {
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        try
        {
            var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Enqueue(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : ""));
            });
            var result = await verifier.RunAsync(root);

            Assert.True(result.Passed);
            var checks = result.Checks ?? throw new InvalidOperationException("Expected acceptance checks.");
            var expectedInfrastructureChecks = lanes.Select(lane => $"infrastructure tests: {lane.Name}");
            Assert.Equal(
                expectedInfrastructureChecks.Order(StringComparer.Ordinal),
                checks
                    .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                    .Select(check => check.Name)
                    .Order(StringComparer.Ordinal));
            Assert.False(checks.Any(check => check.Name == "infrastructure tests"));

            var infrastructureCalls = calls
                .Where(call => call.Length > 2 &&
                    call[0] == "dotnet" &&
                    call[1] == "test" &&
                    call[2].EndsWith("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.Ordinal))
                .ToList();

            Assert.DoesNotContain(infrastructureCalls, call => !call.Contains("--filter"));
            Assert.Equal(lanes.Count, infrastructureCalls.Count);
            Assert.All(
                lanes,
                lane => Assert.Equal(
                    1,
                    infrastructureCalls.Count(call => HasArgumentPair(call, "--filter", lane.Filter))));
            Assert.Contains(infrastructureCalls, call =>
                call.Any(argument =>
                    argument.Contains("FullyQualifiedName!~GoalAcceptanceVerifierTests", StringComparison.Ordinal) &&
                    argument.Contains("Category!=HostIntegration", StringComparison.Ordinal)));
            Assert.DoesNotContain(infrastructureCalls, call =>
                call.Any(argument => argument.Contains("Dashboard", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_slot_gate_builds_solution_once_for_partitioned_targets")]
    public async Task GoalAcceptanceVerifierSlotGateBuildsSolutionOnceForPartitionedTargets()
    {
        var calls = new List<string[]>();
        var root = CreateCheckedInManifestShapeWorkspace();
        File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    0,
                    args.Length > 1 && args[0] == "dotnet" && args[1] == "test"
                        ? "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."
                        : "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                new GoalId("12345678123456781234567812345678"),
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"],
                stableSlotIndex: 0);

            Assert.True(result.Passed);
            var buildCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build")
                .ToArray();
            var testCalls = calls
                .Where(call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "test")
                .ToArray();
            Assert.Single(buildCalls);
            Assert.Contains("Mcg.AgentOrchestrator.sln", buildCalls[0], StringComparer.OrdinalIgnoreCase);
            Assert.All(testCalls, call => Assert.Contains("--no-build", call));
            Assert.DoesNotContain(testCalls, call => call.Contains("Mcg.AgentOrchestrator.sln", StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain(calls.SkipWhile(call => call != buildCalls[0]).Skip(1),
                call => call.Length >= 2 && call[0] == "dotnet" && call[1] == "build");
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_shards_preserve_sequential_mixed_verdicts")]
    public async Task GoalAcceptanceVerifierConcurrentShardsPreserveSequentialMixedVerdicts()
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks("tree-concurrency", "main-concurrency", "commit-concurrency");
        var alphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainderFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var parallelBatchChecks = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var timingProgress = new System.Collections.Concurrent.ConcurrentQueue<AcceptanceGateProgress>();
        var activeShardWorkers = 0;
        var peakShardWorkers = 0;
        try
        {
            TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = parallelBatchChecks.Enqueue;
            static Task<GoalAcceptanceVerifier.CommandResult> RunSequentialFixedVerdict(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                WriteMtpTrx(args);
                var filterClassIndex = Array.IndexOf(args, "--filter-class");
                var alpha = filterClassIndex >= 0 &&
                    args[filterClassIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                return Task.FromResult(alpha
                    ? new GoalAcceptanceVerifier.CommandResult(7, "alpha failed deterministically")
                    : new GoalAcceptanceVerifier.CommandResult(0, "passed deterministically"));
            }

            async Task<GoalAcceptanceVerifier.CommandResult> RunConcurrentFixedVerdict(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var active = Interlocked.Increment(ref activeShardWorkers);
                int observedPeak;
                do
                {
                    observedPeak = Volatile.Read(ref peakShardWorkers);
                }
                while (active > observedPeak &&
                       Interlocked.CompareExchange(ref peakShardWorkers, active, observedPeak) != observedPeak);

                var filterClassIndex = Array.IndexOf(args, "--filter-class");
                var alpha = filterClassIndex >= 0 &&
                    args[filterClassIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                try
                {
                    if (alpha)
                    {
                        alphaStarted.TrySetResult();
                        await remainderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        completionOrder.Enqueue("Alpha");
                        return new GoalAcceptanceVerifier.CommandResult(7, "alpha failed deterministically");
                    }

                    await alphaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    completionOrder.Enqueue("Remainder");
                    remainderFinished.TrySetResult();
                    return new GoalAcceptanceVerifier.CommandResult(0, "passed deterministically");
                }
                finally
                {
                    Interlocked.Decrement(ref activeShardWorkers);
                }
            }

            async Task<AcceptanceVerificationResult> RunScenarioAsync(
                int maxConcurrentShards,
                Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
                string goalId)
            {
                var root = CreateManifestWorkspace($$"""
                    {
                      "version": 1,
                      "engine": {
                        "maxConcurrentShards": {{maxConcurrentShards}},
                        "infrastructureTestLanes": [
                          { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests" },
                          {
                            "name": "Remainder",
                            "filter": "FullyQualifiedName!~AlphaShardTests&Category!=HostIntegration"
                          }
                        ],
                        "mtpInvocations": [
                          {
                            "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                            "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                            "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                            "arguments": [
                              "{executable}",
                              "--no-ansi",
                              "--progress",
                              "off",
                              "--results-directory",
                              "{resultsDirectory}",
                              "--report-trx",
                              "--report-trx-filename",
                              "{trxFileName}"
                            ]
                          }
                        ]
                      },
                      "checks": [
                        {
                          "name": "infrastructure tests",
                          "type": "dotnet-test",
                          "runner": "mtp",
                          "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                          "arguments": ["--verbosity", "minimal"]
                        }
                      ],
                      "forbiddenChangedPathGlobs": []
                    }
                    """);
                try
                {
                    var verifier = new GoalAcceptanceVerifier(TestOverrides, runner);
                    using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.FromSeconds(2));
                    return await verifier.RunOwnedAsync(
                        root,
                        new GoalId(goalId),
                        changedFiles: null,
                        stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                        stableSlotLease: lease,
                        CancellationToken.None,
                        new AcceptanceRunExecutionOptions(ProgressSink: timingProgress.Enqueue));
                }
                finally
                {
                    DeleteDirectoryWithRetry(root);
                }
            }

            using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
                () => throw new InvalidOperationException("deterministic probe failure"));
            var sequential = await RunScenarioAsync(
                1,
                RunSequentialFixedVerdict,
                "11111111111111111111111111111111");
            var concurrent = await RunScenarioAsync(
                2,
                RunConcurrentFixedVerdict,
                "22222222222222222222222222222222");
            var sequentialShards = sequential.Checks!
                .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                .Select(check => (check.Name, check.Passed, check.ExitCode, check.OutputTail))
                .ToArray();
            var concurrentShards = concurrent.Checks!
                .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                .Select(check => (check.Name, check.Passed, check.ExitCode, check.OutputTail))
                .ToArray();

            Assert.False(sequential.Passed);
            Assert.False(concurrent.Passed);
            Assert.Equal(sequential.ExitCode, concurrent.ExitCode);
            Assert.Equal(sequential.OutputTail, concurrent.OutputTail);
            Assert.Equal(sequentialShards, concurrentShards);
            Assert.Equal(2, peakShardWorkers);
            Assert.Equal(new[] { "Remainder", "Alpha" }, completionOrder);
            Assert.Equal(
                new[] { "infrastructure tests: Alpha", "infrastructure tests: Remainder" },
                parallelBatchChecks.Order(StringComparer.Ordinal));
            Assert.Contains(
                timingProgress,
                item => item.GoalId == "22222222222222222222222222222222" &&
                    item.Phase == "shard-complete" &&
                    item.LoadContext?.ConcurrentShardCount is { IsAvailable: true, Value: 2 } &&
                    item.LoadContext.HostCpuUtilizationPercent is
                    {
                        IsAvailable: false,
                        Value: null,
                        UnavailableReason: "probe-error:InvalidOperationException"
                    });
            Assert.Contains(
                timingProgress,
                item => item.GoalId == "22222222222222222222222222222222" &&
                    item.Phase == "shards-complete" &&
                    item.LoadContext?.ConcurrentShardCount is { IsAvailable: true, Value: 0 });
            var batchTiming = Assert.Single(
                timingProgress,
                item => item.GoalId == "22222222222222222222222222222222" &&
                    item.Phase == "shards-complete");
            Assert.Equal("2-infrastructure-shards", batchTiming.CurrentTarget);
            Assert.NotNull(batchTiming.SlotIndex);
            Assert.True(batchTiming.Elapsed >= TimeSpan.Zero);
            Assert.Null(batchTiming.PhaseBreakdown);
            var gateTiming = Assert.IsType<AcceptanceGatePhaseBreakdown>(Assert.Single(
                timingProgress,
                item => item.GoalId == "22222222222222222222222222222222" &&
                    item.Phase == "gate-phase-breakdown").PhaseBreakdown);
            Assert.Equal(batchTiming.Elapsed, gateTiming.LaneExecutionDuration);
            Assert.Contains(gateTiming.Phases, phase => phase.Name == AcceptanceGatePhaseNames.SharedPrebuild);
            Assert.Contains(gateTiming.Phases, phase =>
                phase.Name == AcceptanceGatePhaseNames.LaneExecution &&
                phase.Duration == batchTiming.Elapsed);
            Assert.Collection(
                concurrentShards,
                alpha =>
                {
                    Assert.Equal("infrastructure tests: Alpha", alpha.Name);
                    Assert.False(alpha.Passed);
                    Assert.Equal(7, alpha.ExitCode);
                    Assert.NotNull(alpha.OutputTail);
                    Assert.StartsWith("alpha failed deterministically", alpha.OutputTail);
                    Assert.Contains(
                        "[FAIL] infrastructure tests: Alpha:",
                        alpha.OutputTail,
                        StringComparison.Ordinal);
                },
                remainder =>
                {
                    Assert.Equal("infrastructure tests: Remainder", remainder.Name);
                    Assert.True(remainder.Passed);
                    Assert.Equal(0, remainder.ExitCode);
                    Assert.Null(remainder.OutputTail);
                });
        }
        finally
        {
            TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = null;
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_exclusive_resource_key_order_is_exact_and_declaration_independent")]
    public void GoalAcceptanceVerifierExclusiveResourceKeyOrderIsExactAndDeclarationIndependent()
    {
        string[] expected = ["alpha-only", "Beta-only", "shared-a"];

        Assert.Equal(
            expected,
            GoalAcceptanceVerifier.OrderExclusiveResourceKeys(
                [" shared-a ", "Beta-only", "alpha-only"]));
        Assert.Equal(
            expected,
            GoalAcceptanceVerifier.OrderExclusiveResourceKeys(
                ["alpha-only", "Beta-only", " shared-a "]));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_extracted_infrastructure_project_does_not_claim_shard_scheduler_metadata")]
    public async Task GoalAcceptanceVerifierExtractedInfrastructureProjectDoesNotClaimShardSchedulerMetadata()
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks("tree-extracted", "main-extracted", "commit-extracted");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Worker profiles",
                    "filter": "FullyQualifiedName~WorkerProfileTests",
                    "estimatedSerialSeconds": 100,
                    "exclusiveResourceKeys": [ "xunit:EnvMutation" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  },
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                },
                {
                  "name": "provider environment tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var acquisitionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = acquisitionOrder.Enqueue;
        try
        {
            Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic extracted-project fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests",
                        "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                WriteMtpTrx(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("56565656565656565656565656565656"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed);
            Assert.Equal(
                ["infrastructure tests: Worker profiles"],
                acquisitionOrder);
        }
        finally
        {
            TestOverrides.OnInfrastructureShardResourcesAcquiredForTests = null;
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_exclusive_resource_waiters_do_not_block_disjoint_shards")]
    public async Task GoalAcceptanceVerifierExclusiveResourceWaitersDoNotBlockDisjointShards()
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks("tree-resources", "main-resources", "commit-resources");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Conflict alpha",
                    "filter": "FullyQualifiedName~ConflictAlphaTests",
                    "estimatedSerialSeconds": 100,
                    "exclusiveResourceKeys": [ "shared-a", "alpha-only" ]
                  },
                  {
                    "name": "Conflict beta",
                    "filter": "FullyQualifiedName~ConflictBetaTests",
                    "estimatedSerialSeconds": 90,
                    "exclusiveResourceKeys": [ "shared-a", "beta-only" ]
                  },
                  {
                    "name": "Beta-only follower",
                    "filter": "FullyQualifiedName~BetaOnlyFollowerTests",
                    "estimatedSerialSeconds": 80,
                    "exclusiveResourceKeys": [ "beta-only" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var firstConflictStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaOnlyFollowerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstConflictFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeExecutions = 0;
        var peakExecutions = 0;
        var activeConflicts = 0;
        var peakConflicts = 0;
        var conflictEntries = 0;
        var betaOnlyFollowerOverlappedConflict = 0;
        try
        {
            static void RecordPeak(ref int peak, int active)
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref peak);
                }
                while (active > observed &&
                       Interlocked.CompareExchange(ref peak, active, observed) != observed);
            }

            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var filter = args[filterIndex + 1];
                var isConflict = filter.Contains("Conflict", StringComparison.Ordinal);
                var active = Interlocked.Increment(ref activeExecutions);
                RecordPeak(ref peakExecutions, active);
                var conflictOrdinal = 0;
                if (isConflict)
                {
                    var activeConflictCount = Interlocked.Increment(ref activeConflicts);
                    RecordPeak(ref peakConflicts, activeConflictCount);
                    conflictOrdinal = Interlocked.Increment(ref conflictEntries);
                }

                try
                {
                    if (conflictOrdinal == 1)
                    {
                        firstConflictStarted.TrySetResult();
                        await betaOnlyFollowerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    else if (conflictOrdinal == 2)
                    {
                        Assert.True(
                            firstConflictFinished.Task.IsCompleted,
                            "The second conflicting shard entered before the first released its resource keys.");
                    }
                    else
                    {
                        await firstConflictStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        if (!firstConflictFinished.Task.IsCompleted)
                        {
                            Interlocked.Exchange(ref betaOnlyFollowerOverlappedConflict, 1);
                        }

                        betaOnlyFollowerStarted.TrySetResult();
                        await firstConflictFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }
                finally
                {
                    if (isConflict)
                    {
                        Interlocked.Decrement(ref activeConflicts);
                        if (conflictOrdinal == 1)
                        {
                            firstConflictFinished.TrySetResult();
                        }
                    }

                    Interlocked.Decrement(ref activeExecutions);
                }
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("44444444444444444444444444444444"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed);
            Assert.Equal(2, conflictEntries);
            Assert.Equal(1, peakConflicts);
            Assert.Equal(2, peakExecutions);
            Assert.Equal(1, betaOnlyFollowerOverlappedConflict);
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_cancellation_leaves_pending_resource_keys_unreserved")]
    public async Task GoalAcceptanceVerifierCancellationLeavesPendingResourceKeysUnreserved()
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks(
            "tree-execution-wait-cancel",
            "main-execution-wait-cancel",
            "commit-execution-wait-cancel");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Resource holder",
                    "filter": "FullyQualifiedName~ResourceHolderTests",
                    "estimatedSerialSeconds": 100,
                    "exclusiveResourceKeys": [ "z-holder" ]
                  },
                  {
                    "name": "Slot holder",
                    "filter": "FullyQualifiedName~SlotHolderTests",
                    "estimatedSerialSeconds": 90
                  },
                  {
                    "name": "Resource waiter",
                    "filter": "FullyQualifiedName~ResourceWaiterTests",
                    "estimatedSerialSeconds": 80,
                    "exclusiveResourceKeys": [ "shared", "z-holder" ]
                  },
                  {
                    "name": "Shared-key probe",
                    "filter": "FullyQualifiedName~SharedKeyProbeTests",
                    "estimatedSerialSeconds": 70,
                    "exclusiveResourceKeys": [ "shared" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var resourceHolderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slotHolderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedKeyProbeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResourceHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlotHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSharedKeyProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterRan = 0;
        using var cancellation = new CancellationTokenSource();
        Task? verification = null;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var filter = args[filterIndex + 1];
                WriteMtpTrx(args);
                if (filter.Contains("ResourceHolder", StringComparison.Ordinal))
                {
                    resourceHolderStarted.TrySetResult();
                    await releaseResourceHolder.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                if (filter.Contains("SlotHolder", StringComparison.Ordinal))
                {
                    slotHolderStarted.TrySetResult();
                    await releaseSlotHolder.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                if (filter.Contains("SharedKeyProbe", StringComparison.Ordinal))
                {
                    sharedKeyProbeStarted.TrySetResult();
                    await releaseSharedKeyProbe.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                Interlocked.Exchange(ref waiterRan, 1);
                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            verification = verifier.RunAsync(
                root,
                new GoalId("77777777777777777777777777777777"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease,
                cancellationToken: cancellation.Token);

            await Task.WhenAll(
                resourceHolderStarted.Task,
                slotHolderStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            releaseSlotHolder.TrySetResult();
            await sharedKeyProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            releaseResourceHolder.TrySetResult();
            releaseSharedKeyProbe.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref waiterRan));
            Assert.False(
                verification.Exception?.Flatten().InnerExceptions
                    .Any(exception => exception is SemaphoreFullException) ?? false,
                "Cancellation over-released the execution-slot semaphore.");
        }
        finally
        {
            cancellation.Cancel();
            releaseResourceHolder.TrySetResult();
            releaseSlotHolder.TrySetResult();
            releaseSharedKeyProbe.TrySetResult();
            if (verification is not null && !verification.IsCompleted)
            {
                _ = await Record.ExceptionAsync(
                    () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory(DisplayName = "GoalAcceptanceVerifier_releases_exclusive_resources_after_shard_fault_or_cancellation")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task GoalAcceptanceVerifierReleasesExclusiveResourcesAfterShardFaultOrCancellation(
        bool cancelFirstShard)
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        SetPartitionVerdictKeyHooks(
            $"tree-release-{cancelFirstShard}",
            $"main-release-{cancelFirstShard}",
            $"commit-release-{cancelFirstShard}");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Faulting",
                    "filter": "FullyQualifiedName~FaultingTests",
                    "estimatedSerialSeconds": 100,
                    "exclusiveResourceKeys": [ "shared" ]
                  },
                  {
                    "name": "Waiting",
                    "filter": "FullyQualifiedName~WaitingTests",
                    "estimatedSerialSeconds": 90,
                    "exclusiveResourceKeys": [ "shared" ]
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var faultingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFaulting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var faulting = args[filterIndex + 1].Contains("FaultingTests", StringComparison.Ordinal);
                if (faulting)
                {
                    faultingStarted.TrySetResult();
                    await releaseFaulting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (cancelFirstShard)
                    {
                        throw new OperationCanceledException("Synthetic shard cancellation.");
                    }

                    throw new InvalidOperationException("Synthetic shard fault.");
                }

                waitingStarted.TrySetResult();
                WriteMtpTrx(args);
                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var verification = verifier.RunAsync(
                root,
                new GoalId(cancelFirstShard
                    ? "55555555555555555555555555555555"
                    : "66666666666666666666666666666666"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            await faultingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseFaulting.TrySetResult();
            if (cancelFirstShard)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<AcceptanceGateEngineException>(() => verification);
                var innerException = Assert.IsType<InvalidOperationException>(exception.InnerException);
                Assert.Equal("Synthetic shard fault.", innerException.Message);
                Assert.Equal("lane-execution", exception.GatePhase);
                Assert.Equal("infrastructure tests: Faulting", exception.GateTarget);
            }

            Assert.True(
                waitingStarted.Task.IsCompleted,
                "The waiting shard did not acquire the resource released by the faulted shard.");
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_shards_start_longest_estimated_lanes_first")]
    public async Task GoalAcceptanceVerifierConcurrentShardsStartLongestEstimatedLanesFirst()
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        SetPartitionVerdictKeyHooks("tree-estimates", "main-estimates", "commit-estimates");
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Light first",
                    "filter": "FullyQualifiedName~LightFirstTests",
                    "estimatedSerialSeconds": 1
                  },
                  {
                    "name": "Heavy alpha",
                    "filter": "FullyQualifiedName~HeavyAlphaTests",
                    "estimatedSerialSeconds": 100
                  },
                  {
                    "name": "Light second",
                    "filter": "FullyQualifiedName~LightSecondTests",
                    "estimatedSerialSeconds": 2
                  },
                  {
                    "name": "Heavy beta",
                    "filter": "FullyQualifiedName~HeavyBetaTests",
                    "estimatedSerialSeconds": 99
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var heavyAlphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heavyBetaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            Assert.False(File.Exists(AcceptanceLaneDurationStore.ResolveStorePath(root)));

            async Task<GoalAcceptanceVerifier.CommandResult> RunEstimatedShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var filter = args[filterIndex + 1];
                startOrder.Enqueue(filter);
                if (filter.Contains("HeavyAlphaTests", StringComparison.Ordinal))
                {
                    heavyAlphaStarted.TrySetResult();
                    await heavyBetaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (filter.Contains("HeavyBetaTests", StringComparison.Ordinal))
                {
                    heavyBetaStarted.TrySetResult();
                    await heavyAlphaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunEstimatedShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("33333333333333333333333333333333"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed);
            var firstWave = startOrder.Take(2).ToArray();
            Assert.Equal(2, firstWave.Length);
            Assert.Contains(firstWave, filter => filter.Contains("HeavyAlphaTests", StringComparison.Ordinal));
            Assert.Contains(firstWave, filter => filter.Contains("HeavyBetaTests", StringComparison.Ordinal));
            Assert.Equal(
                [
                    "infrastructure tests: Light first",
                    "infrastructure tests: Heavy alpha",
                    "infrastructure tests: Light second",
                    "infrastructure tests: Heavy beta"
                ],
                result.Checks!
                    .Where(check => check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal))
                    .Select(check => check.Name));
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task ConcurrentShards_ObservedHistory_UsesObservedOrder()
    {
        var root = CreateLaneOrderingWorkspace();
        try
        {
            SeedLaneDurations(root, sampleCount: 3,
                ("Light first", "FullyQualifiedName~LightFirstTests", 500),
                ("Heavy alpha", "FullyQualifiedName~HeavyAlphaTests", 1),
                ("Light second", "FullyQualifiedName~LightSecondTests", 500),
                ("Heavy beta", "FullyQualifiedName~HeavyBetaTests", 1));

            var (result, firstWave) = await RunLaneOrderingAsync(
                root,
                "observed",
                "LightFirstTests",
                "LightSecondTests");

            Assert.True(result.Passed);
            Assert.Contains(firstWave, filter => filter.Contains("LightFirstTests", StringComparison.Ordinal));
            Assert.Contains(firstWave, filter => filter.Contains("LightSecondTests", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("below-threshold")]
    public async Task ConcurrentShards_InsufficientHistory_UsesSeedOrder(string history)
    {
        var root = CreateLaneOrderingWorkspace();
        try
        {
            if (history == "empty")
            {
                var path = AcceptanceLaneDurationStore.ResolveStorePath(root);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
            else
            {
                SeedLaneDurations(root, sampleCount: 2,
                    ("Light first", "FullyQualifiedName~LightFirstTests", 500),
                    ("Heavy alpha", "FullyQualifiedName~HeavyAlphaTests", 1),
                    ("Light second", "FullyQualifiedName~LightSecondTests", 500),
                    ("Heavy beta", "FullyQualifiedName~HeavyBetaTests", 1));
            }

            var (result, firstWave) = await RunLaneOrderingAsync(
                root,
                history,
                "HeavyAlphaTests",
                "HeavyBetaTests");

            Assert.True(result.Passed);
            Assert.Contains(firstWave, filter => filter.Contains("HeavyAlphaTests", StringComparison.Ordinal));
            Assert.Contains(firstWave, filter => filter.Contains("HeavyBetaTests", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task ConcurrentShards_ChangedFilter_DiscardsHistory()
    {
        var root = CreateLaneOrderingWorkspace("FullyQualifiedName~LightFirstRenamedTests");
        try
        {
            SeedLaneDurations(root, sampleCount: 3,
                ("Light first", "FullyQualifiedName~LightFirstTests", 500));
            var changed = LaneCheck(
                "Light first",
                "FullyQualifiedName~LightFirstRenamedTests",
                seed: 1);
            using (AcceptanceLaneDurationStore.PushRecordingScope(root))
            {
                Assert.Equal(1d, AcceptanceLaneDurationStore.ResolveSortSeconds(changed));
            }

            var (result, firstWave) = await RunLaneOrderingAsync(
                root,
                "changed-filter",
                "HeavyAlphaTests",
                "HeavyBetaTests");

            Assert.True(result.Passed);
            Assert.Contains(firstWave, filter => filter.Contains("HeavyAlphaTests", StringComparison.Ordinal));
            Assert.Contains(firstWave, filter => filter.Contains("HeavyBetaTests", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public void EffectivePlanIdentity_ObservedHistory_IsByteIdentical()
    {
        var root = CreateLaneOrderingWorkspace();
        try
        {
            var withoutHistory = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root);
            var lightFirst = LaneCheck("Light first", "FullyQualifiedName~LightFirstTests", seed: 1);
            SeedLaneDurations(root, sampleCount: 3,
                ("Light first", "FullyQualifiedName~LightFirstTests", 500));
            using (AcceptanceLaneDurationStore.PushRecordingScope(root))
            {
                Assert.Equal(500d, AcceptanceLaneDurationStore.ResolveSortSeconds(lightFirst));
            }

            var withHistory = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root);

            Assert.Equal(withoutHistory, withHistory);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FailedGate_DoesNotPersistShardDurations()
    {
        var root = CreateLaneOrderingWorkspace(failForbiddenPathCheck: true);
        try
        {
            var (result, _) = await RunLaneOrderingAsync(
                root,
                "failed-gate",
                "HeavyAlphaTests",
                "HeavyBetaTests",
                forbiddenChangedPath: "bin/generated.dll");

            Assert.False(result.Passed);
            Assert.Contains(result.Checks!, check =>
                check.Name == "forbidden changed paths" && !check.Passed);
            Assert.False(File.Exists(AcceptanceLaneDurationStore.ResolveStorePath(root)));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static string CreateLaneOrderingWorkspace(
        string lightFirstFilter = "FullyQualifiedName~LightFirstTests",
        bool failForbiddenPathCheck = false)
    {
        var forbiddenChangedPathGlobs = failForbiddenPathCheck ? "[ \"bin/**\" ]" : "[]";
        return CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  {
                    "name": "Light first",
                    "filter": "{{lightFirstFilter}}",
                    "estimatedSerialSeconds": 1
                  },
                  {
                    "name": "Heavy alpha",
                    "filter": "FullyQualifiedName~HeavyAlphaTests",
                    "estimatedSerialSeconds": 100
                  },
                  {
                    "name": "Light second",
                    "filter": "FullyQualifiedName~LightSecondTests",
                    "estimatedSerialSeconds": 2
                  },
                  {
                    "name": "Heavy beta",
                    "filter": "FullyQualifiedName~HeavyBetaTests",
                    "estimatedSerialSeconds": 99
                  }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
                }
              ],
              "forbiddenChangedPathGlobs": {{forbiddenChangedPathGlobs}}
            }
            """);
    }

    private static void SeedLaneDurations(
        string root,
        int sampleCount,
        params (string Name, string Filter, double Seconds)[] lanes)
    {
        for (var sample = 0; sample < sampleCount; sample++)
        {
            using var scope = AcceptanceLaneDurationStore.PushRecordingScope(root);
            foreach (var lane in lanes)
            {
                var check = LaneCheck(lane.Name, lane.Filter, seed: 1);
                AcceptanceLaneDurationStore.Record(
                    check,
                    new AcceptanceCheckResult(check.Name, true, 0, null),
                    TimeSpan.FromSeconds(lane.Seconds));
            }

            AcceptanceLaneDurationStore.Flush();
        }
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck LaneCheck(
        string laneName,
        string filter,
        double seed) =>
        new()
        {
            Name = $"infrastructure tests: {laneName}",
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", filter],
            EstimatedSerialSeconds = seed
        };

    private async Task<(AcceptanceVerificationResult Result, string[] FirstWave)> RunLaneOrderingAsync(
        string root,
        string hookSuffix,
        string firstExpectedFilter,
        string secondExpectedFilter,
        string? forbiddenChangedPath = null)
    {
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        SetPartitionVerdictKeyHooks($"tree-{hookSuffix}", $"main-{hookSuffix}", $"commit-{hookSuffix}");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args,
                string _,
                CancellationToken _cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(
                        args,
                        "deterministic shard fixture",
                        "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                if (args.SequenceEqual(["git", "diff", "--name-only", "main...HEAD"]))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, forbiddenChangedPath ?? string.Empty);
                }

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var filter = args[filterIndex + 1];
                startOrder.Enqueue(filter);
                if (filter.Contains(firstExpectedFilter, StringComparison.Ordinal))
                {
                    firstStarted.TrySetResult();
                    await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (filter.Contains(secondExpectedFilter, StringComparison.Ordinal))
                {
                    secondStarted.TrySetResult();
                    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("44444444444444444444444444444444"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);
            var firstWave = startOrder.Take(2).ToArray();
            Assert.Equal(2, firstWave.Length);
            return (result, firstWave);
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
        }
    }

    private static bool TryWriteMtpBuildArtifacts(
        string[] arguments,
        string content,
        params string[] projectNames)
    {
        if (arguments.Length == 0 ||
            !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Length >= 2 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (arguments.Length < 2 ||
            !arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var artifactsPath = GetArtifactsPath(arguments);
        foreach (var projectName in projectNames)
        {
            var outputDirectory = Path.Combine(artifactsPath, "bin", projectName, "debug");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(
                Path.Combine(
                    outputDirectory,
                    projectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)),
                content);
            File.WriteAllText(Path.Combine(outputDirectory, $"{projectName}.dll"), content);
        }

        return true;
    }

}
