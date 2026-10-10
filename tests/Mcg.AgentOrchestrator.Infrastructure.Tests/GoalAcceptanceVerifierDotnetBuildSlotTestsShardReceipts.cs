using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsShardReceipts : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_trusted_manifest_git_read_drains_large_output")]
    public void GoalAcceptanceVerifierTrustedManifestGitReadDrainsLargeOutput()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();

        var trustedManifest = GoalAcceptanceVerifier.ResolveGitText(
            repositoryRoot,
            "show",
            "main:config/acceptance-manifest.json");

        Assert.NotNull(trustedManifest);
        Assert.True(
            trustedManifest.Length > 4096,
            $"Expected the trusted manifest fixture to exceed a Windows pipe buffer; actual length was {trustedManifest.Length}.");
        Assert.Contains("\"maxConcurrentShards\"", trustedManifest, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_real_process_shards_keep_receipts_in_attempt_artifacts_after_releasing_build_lease")]
    public async Task GoalAcceptanceVerifierRealProcessShardsKeepReceiptsInAttemptArtifactsAfterReleasingSlots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        TestOverrides.ResolveShardCoreBudgetForTests = () => 1;
        var root = CreateRealProcessShardManifestWorkspace();
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        const string infrastructureTestProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        const string shardProbeProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/Mcg.AgentOrchestrator.RealProcessShardProbe.csproj";
        var invocations = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        var executablePaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var resultsDirectories = new System.Collections.Concurrent.ConcurrentBag<string>();
        var testProcessIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        var ambientAttemptPrefix = Path.Combine(root, "ambient-gate-attempt", "not-a-slot");
        var ownedAttemptPrefix = Path.Combine(root, "owned-gate-attempt", "attempt-1");
        var previousAttemptPrefix = Environment.GetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        IReadOnlyDictionary<string, string> shardEnvironment = new Dictionary<string, string>();
        DotnetBuildEnvironmentLease? primaryLease = null;
        DotnetBuildEnvironment? primaryEnvironment = null;
        var primaryBuildPermit = -1;
        try
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                ambientAttemptPrefix);
            var verifier = new GoalAcceptanceVerifier(TestOverrides, async (args, _, timeout, cancellationToken) =>
            {
                invocations.Enqueue(args);
                var processArgs = args;
                if (args.Length >= 3 &&
                    args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    args[1].Equals("build", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(infrastructureTestProject, args[2]);
                    processArgs = [.. args];
                    processArgs[2] = shardProbeProject;
                }

                var isShardTest =
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.RealProcessShardProbe") &&
                    (args.Contains("--filter-class") || args.Contains("--filter-not-class"));
                if (isShardTest)
                {
                    Assert.True(
                        DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(primaryBuildPermit),
                        "MTP execution must not retain the build-pool lease.");
                    executablePaths.Add(args[1]);
                    var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
                    Assert.InRange(resultsDirectoryIndex, 0, args.Length - 2);
                    resultsDirectories.Add(args[resultsDirectoryIndex + 1]);
                }

                return await RunRealShardProcessAsync(
                    processArgs,
                    repositoryRoot,
                    timeout,
                    shardEnvironment,
                    process =>
                    {
                        if (isShardTest)
                        {
                            testProcessIds.Add(process.Id);
                        }
                    },
                    cancellationToken);
            });

            primaryLease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            primaryEnvironment = primaryLease.Environment;
            var primarySlot = StableSlotIndex(primaryLease.Environment.ArtifactsPath);
            primaryBuildPermit = primarySlot;
            var run = verifier.RunOwnedAsync(
                root,
                goalId: new GoalId("12345678123456781234567812345678"),
                changedFiles: null,
                stableSlotIndex: primarySlot,
                stableSlotLease: primaryLease,
                cancellationToken: CancellationToken.None,
                executionOptions: new AcceptanceRunExecutionOptions(ResultsPrefix: ownedAttemptPrefix));
            var result = await run;
            Assert.True(
                result.Passed,
                string.Join(
                    " | ",
                    result.Checks!
                        .Where(check => !check.Passed)
                        .Select(check => $"{check.Name}: exit={check.ExitCode}; output={check.OutputTail}")));
            Assert.True(Assert.Single(testProcessIds) > 0);
            var invocationArray = invocations.ToArray();
            Assert.Single(invocationArray, call =>
                call.Length >= 2 &&
                call[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                call[1].Equals("build", StringComparison.OrdinalIgnoreCase));
            var firstShardIndex = Array.FindIndex(
                invocationArray,
                call => IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.RealProcessShardProbe"));
            Assert.True(firstShardIndex >= 0);
            Assert.DoesNotContain(
                invocationArray.Skip(firstShardIndex),
                call => call.Length > 0 &&
                    call[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    (call.Length < 2 || !call[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            Assert.Single(executablePaths.Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.All(executablePaths, path => Assert.True(File.Exists(path), $"Missing prebuilt MTP managed assembly '{path}'."));
            var expectedAppHost = Path.Combine(
                primaryEnvironment.ArtifactsPath,
                "bin",
                "Mcg.AgentOrchestrator.RealProcessShardProbe",
                "debug",
                $"Mcg.AgentOrchestrator.RealProcessShardProbe{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
            Assert.True(File.Exists(expectedAppHost), $"Missing validated MTP apphost '{expectedAppHost}'.");
            var attemptResultsDirectory = Path.GetDirectoryName(ownedAttemptPrefix)!;
            Assert.Single(resultsDirectories);
            Assert.All(
                resultsDirectories,
                path => Assert.Equal(attemptResultsDirectory, path, ignoreCase: true));
            Assert.Single(result.TestResultPaths!);
            var completedProbeTests = TestCoverageInvariant.ReadCompletedTests(result.TestResultPaths!);
            Assert.Contains(
                "ShardProbeAlphaTests.SynchronizesWithBetaShard",
                completedProbeTests);
            Assert.All(
                result.TestResultPaths!,
                path => Assert.StartsWith(
                    attemptResultsDirectory + Path.DirectorySeparatorChar,
                    path,
                    StringComparison.OrdinalIgnoreCase));
            Assert.All(
                Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount)
                    .Where(slot => slot != primarySlot),
                slot => Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(slot)));

            primaryLease.Dispose();
            primaryLease = null;
            Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(primarySlot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
                previousAttemptPrefix);
            primaryLease?.Dispose();
            if (primaryEnvironment is not null)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(primaryEnvironment);
            }

            TestOverrides.ResolveShardCoreBudgetForTests = null;
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_shard_receipts_use_attempt_artifacts_instead_of_releasable_slot")]
    public void GoalAcceptanceVerifierShardReceiptsUseAttemptArtifactsInsteadOfReleasableSlot()
    {
        var attemptDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mcg-shard-attempt-{Guid.NewGuid():N}");
        var attemptPrefix = Path.Combine(attemptDirectory, "attempt-123");
        var slotRoot = Path.Combine(Path.GetTempPath(), $"mcg-shard-slot-{Guid.NewGuid():N}");
        var environment = new DotnetBuildEnvironment(
            "run-slot-1",
            slotRoot,
            Path.Combine(slotRoot, "artifacts"),
            Path.Combine(slotRoot, "build-slots", "slot-1.lock"),
            [],
            "slot-1");
        var resultsDirectory = GoalAcceptanceVerifier.ResolveInfrastructureShardResultsDirectory(
            environment,
            attemptPrefix);

        Assert.Equal(attemptDirectory, resultsDirectory, ignoreCase: true);
        Assert.False(
            resultsDirectory.StartsWith(
                environment.ArtifactsPath,
                StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_shards_use_distinct_attempt_heartbeat_files")]
    public void GoalAcceptanceVerifierConcurrentShardsUseDistinctAttemptHeartbeatFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-shard-heartbeats-{Guid.NewGuid():N}");
        var environment = new DotnetBuildEnvironment(
            "goal-heartbeat",
            root,
            Path.Combine(root, "build-artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"),
            [],
            "goal-heartbeat");
        var attemptPrefix = Path.Combine(root, "attempt-owner");

        var first = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane alpha",
            environment,
            stableSlotIndex: 0,
            attemptResultsPrefix: attemptPrefix);
        var second = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane beta",
            environment,
            stableSlotIndex: 0,
            attemptResultsPrefix: attemptPrefix);
        var punctuationFirst = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane alpha+beta",
            environment,
            stableSlotIndex: 0,
            attemptResultsPrefix: attemptPrefix);
        var punctuationSecond = GoalAcceptanceVerifier.ResolveGateHeartbeatPathForTests(
            "infrastructure lane alpha beta",
            environment,
            stableSlotIndex: 0,
            attemptResultsPrefix: attemptPrefix);

        Assert.NotEqual(first, second);
        Assert.NotEqual(punctuationFirst, punctuationSecond);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, first, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, second, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(environment.ArtifactsPath, first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(environment.ArtifactsPath, second, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_owner_results_fallback_never_roots_in_goal_worktree")]
    public void GoalAcceptanceVerifierOwnerResultsFallbackNeverRootsInGoalWorktree()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"mcg-owner-root-{Guid.NewGuid():N}");
        var worktreePath = Path.Combine(repositoryRoot, ".orchestrator-worktrees", "abc12345");

        var resolved = GoalAcceptanceVerifier.ResolveOwnerResultsRepositoryRoot(worktreePath);

        Assert.Equal(repositoryRoot, resolved, ignoreCase: true);
        Assert.False(resolved.StartsWith(worktreePath, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_concurrent_gate_evidence_and_operator_owners_do_not_cross_contaminate")]
    public async Task GoalAcceptanceVerifierConcurrentGateEvidenceAndOperatorOwnersDoNotCrossContaminate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-owner-chaos-{Guid.NewGuid():N}");
        var attemptA = Path.Combine(root, "attempt-a");
        var attemptB = Path.Combine(root, "attempt-b");
        var operatorDirectory = Path.Combine(root, "operator");
        var executionLockPath = Path.Combine(root, "build-slots", "build-0.lock");
        var gateBuild = new DotnetBuildEnvironment(
            "gate-chaos",
            Path.Combine(root, "gate-build"),
            Path.Combine(root, "gate-build", "artifacts"),
            executionLockPath,
            [],
            "gate-chaos",
            BuildPermitIndex: 0);
        var operatorBuild = new DotnetBuildEnvironment(
            "operator-chaos",
            Path.Combine(root, "operator-build"),
            Path.Combine(root, "operator-build", "artifacts"),
            executionLockPath,
            [],
            "operator-chaos",
            BuildPermitIndex: 0);
        var environment = new DotnetBuildEnvironment(
            "goal-chaos",
            root,
            Path.Combine(root, "build-artifacts"),
            Path.Combine(root, "build-slots", "build-0.lock"),
            [],
            "goal-chaos");
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var custodyRefusals = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var activeBuildOwners = 0;
        var maximumConcurrentBuildOwners = 0;
        try
        {
            static string Trx(string id, string testName) =>
                $"<TestRun><TestDefinitions><UnitTest id=\"{id}\" name=\"{testName}\"><TestMethod className=\"ChaosTests\" name=\"{testName[(testName.LastIndexOf('.') + 1)..]}\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"{id}\" testName=\"{testName}\" outcome=\"Passed\" /></Results></TestRun>";

            static void RecordMaximum(ref int location, int value)
            {
                var observed = Volatile.Read(ref location);
                while (value > observed)
                {
                    var prior = Interlocked.CompareExchange(ref location, value, observed);
                    if (prior == observed)
                    {
                        return;
                    }

                    observed = prior;
                }
            }

            Task<(string Path, long Length, string Hash, TestCoverageInvariantResult Coverage)> RunOwnerAsync(
                string directory,
                string testName,
                bool ownsBuildPermit) => Task.Run(() =>
            {
                DotnetBuildEnvironmentLease? lease = null;
                try
                {
                    if (ownsBuildPermit)
                    {
                        lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                                gateBuild,
                                TimeSpan.Zero)).Lease;
                        var concurrent = Interlocked.Increment(ref activeBuildOwners);
                        RecordMaximum(ref maximumConcurrentBuildOwners, concurrent);
                    }

                    var resolved = GoalAcceptanceVerifier.ResolveInfrastructureShardResultsDirectory(
                        environment,
                        Path.Combine(directory, "owner"));
                    Directory.CreateDirectory(resolved);
                    var receipt = Path.Combine(resolved, "lane.trx");
                    File.WriteAllText(receipt, Trx(Guid.NewGuid().ToString("N"), testName));
                    var length = new FileInfo(receipt).Length;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receipt)));
                    ready.Signal();
                    release.Wait();

                    if (!receipt.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        custodyRefusals.Enqueue($"receipt escaped owner directory: {receipt}");
                    }

                    var coverage = TestCoverageInvariant.Evaluate(
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testName },
                        [new TestPartitionCoverage(
                            testName,
                            Completed: true,
                            TestResultPaths: [receipt],
                            AttemptId: Path.GetFileName(directory))],
                        currentAttemptId: Path.GetFileName(directory));
                    return (receipt, length, hash, coverage);
                }
                catch (Exception ex)
                {
                    faults.Enqueue(ex.ToString());
                    throw;
                }
                finally
                {
                    if (ownsBuildPermit)
                    {
                        Interlocked.Decrement(ref activeBuildOwners);
                    }

                    lease?.Dispose();
                }
            });

            var ownerA = RunOwnerAsync(attemptA, "ChaosTests.AttemptA", ownsBuildPermit: true);
            var ownerB = RunOwnerAsync(attemptB, "ChaosTests.AttemptB", ownsBuildPermit: false);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Concurrent owners did not reach the event gate.");
            var operatorBlocked = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(operatorBuild, TimeSpan.Zero));
            Assert.Contains(
                operatorBlocked.BusySlots,
                slot => slot.SlotIndex == gateBuild.BuildPermitIndex);
            Directory.CreateDirectory(operatorDirectory);
            var operatorReceipt = Path.Combine(operatorDirectory, "operator.trx");
            File.WriteAllText(operatorReceipt, Trx("operator", "ChaosTests.Operator"));
            release.Set();

            var receipts = await Task.WhenAll(ownerA, ownerB);
            using (var operatorLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    operatorBuild,
                    TimeSpan.FromSeconds(2))).Lease)
            {
                var concurrent = Interlocked.Increment(ref activeBuildOwners);
                RecordMaximum(ref maximumConcurrentBuildOwners, concurrent);
                Interlocked.Decrement(ref activeBuildOwners);
            }

            Assert.StartsWith(attemptA + Path.DirectorySeparatorChar, receipts[0].Path, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(attemptB + Path.DirectorySeparatorChar, receipts[1].Path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(receipts, receipt => receipt.Path.StartsWith(operatorDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.All(receipts, receipt =>
            {
                Assert.True(File.Exists(receipt.Path));
                Assert.Equal(receipt.Length, new FileInfo(receipt.Path).Length);
                Assert.Equal(
                    receipt.Hash,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receipt.Path))));
                Assert.True(receipt.Coverage.Passed, receipt.Coverage.Summary);
                Assert.Single(receipt.Coverage.ExecutedTests);
            });
            Assert.DoesNotContain(
                "ChaosTests.AttemptB",
                TestCoverageInvariant.ReadRecordedTests([receipts[0].Path]));
            Assert.DoesNotContain(
                "ChaosTests.AttemptA",
                TestCoverageInvariant.ReadRecordedTests([receipts[1].Path]));
            Assert.True(File.Exists(operatorReceipt));
            Assert.False(operatorReceipt.StartsWith(attemptA, StringComparison.OrdinalIgnoreCase));
            Assert.False(operatorReceipt.StartsWith(attemptB, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, maximumConcurrentBuildOwners);
            Assert.Empty(faults);
            Assert.Empty(custodyRefusals);
        }
        finally
        {
            release.Set();
            DeleteDirectoryWithRetry(root);
        }
    }

}
