using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsStructuralCoverageOverlap : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task TrustedPreparationCompletesForEveryProjectWhileAllLanesAreHeld()
    {
        var (root, mainRoot) = CreateWorkspace(includeCore: true);
        var goalId = GoalId.New();
        var releaseLanes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allLanesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baselinesDiscovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<string>();
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
        var laneStarts = 0;
        var laneFinishes = 0;
        var baselineDiscoveryCount = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.OnTrustedMainBaselineBuildStartingForTests = _ => events.Enqueue("main-build-hook");
        TestOverrides.OnStructuralCoveragePreparationFinishedForTests = () => preparationFinished.TrySetResult();
        TestOverrides.OnStructuralCoverageStartedForTests = () =>
        {
            Assert.Equal(2, Volatile.Read(ref laneFinishes));
            events.Enqueue("coverage-phase");
        };
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] arguments, string worktree, CancellationToken token)
            {
                if (arguments.Length > 1 && arguments[0] == "dotnet" && arguments[1] == "build")
                {
                    if (worktree == mainRoot)
                        events.Enqueue($"main-build:{CoverageProjectName(arguments)}");
                    else
                        WriteCandidateBuildArtifacts(arguments);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                if (arguments.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                {
                    if (worktree == root)
                        await allLanesStarted.Task.WaitAsync(token);
                    Assert.Equal(0, Volatile.Read(ref laneFinishes));
                    var project = CoverageProjectName(arguments);
                    events.Enqueue($"{(worktree == mainRoot ? "main" : "candidate")}-discovery:{project}");
                    if (worktree == mainRoot && Interlocked.Increment(ref baselineDiscoveryCount) == 2)
                        baselinesDiscovered.TrySetResult();
                    return new GoalAcceptanceVerifier.CommandResult(0,
                        project == "Core"
                            ? "DISCOVERED_TEST:CoreShardTests.Passes"
                            : "DISCOVERED_TEST:AlphaShardTests.Passes\nDISCOVERED_TEST:BetaShardTests.Passes");
                }

                if (IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(arguments, 1, ["CoreShardTests.Passes"]);
                    events.Enqueue("core-check-complete");
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                if (IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    var number = Interlocked.Increment(ref laneStarts);
                    if (number == 2)
                        allLanesStarted.TrySetResult();
                    events.Enqueue($"lane-start-{number}");
                    await releaseLanes.Task.WaitAsync(token);
                    var alpha = arguments.Any(argument => argument.Contains("AlphaShardTests", StringComparison.Ordinal));
                    WriteMtpTrx(arguments, 1, [alpha ? "AlphaShardTests.Passes" : "BetaShardTests.Passes"]);
                    Interlocked.Increment(ref laneFinishes);
                    events.Enqueue($"lane-finish-{number}");
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var run = verifier.RunOwnedAsync(root, goalId, changedFiles: ["src/Sample.cs"],
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease,
                CancellationToken.None, new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
            await Task.WhenAll(allLanesStarted.Task, baselinesDiscovered.Task, preparationFinished.Task)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, Volatile.Read(ref laneFinishes));
            var beforeRelease = events.ToArray();
            Assert.Contains("core-check-complete", beforeRelease);
            foreach (var project in new[] { "Core", "Infrastructure" })
            {
                Assert.Contains($"candidate-discovery:{project}", beforeRelease);
                Assert.Contains($"main-build:{project}", beforeRelease);
                Assert.Contains($"main-discovery:{project}", beforeRelease);
            }
            Assert.Equal(2, beforeRelease.Count(item => item == "main-build-hook"));
            var preparationEvents = beforeRelease.Where(item =>
                item.StartsWith("candidate-discovery:", StringComparison.Ordinal) ||
                item.StartsWith("main-build:", StringComparison.Ordinal) ||
                item.StartsWith("main-discovery:", StringComparison.Ordinal)).ToArray();
            Assert.Equal([
                "candidate-discovery:Core", "main-build:Core", "main-discovery:Core",
                "candidate-discovery:Infrastructure", "main-build:Infrastructure", "main-discovery:Infrastructure"
            ], preparationEvents);
            Assert.DoesNotContain("coverage-phase", beforeRelease);
            releaseLanes.TrySetResult();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result.Passed, result.OutputTail);
            Assert.True(Assert.Single(result.Checks!.Where(check => check.Name == "structural test coverage")).Passed);
            Assert.Equal(2, Volatile.Read(ref laneFinishes));
            Assert.Contains("coverage-phase", events);
            var phase = Assert.Single(progress, item => item.Phase == "gate-phase-breakdown");
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(phase.PhaseBreakdown);
            Assert.NotNull(breakdown.StructuralCoveragePreparationDuration);
            Assert.Equal(TimeSpan.Zero, breakdown.StructuralCoveragePreparationWaitDuration);
            Assert.Contains("structural_coverage_preparation_ms=", phase.CurrentTarget, StringComparison.Ordinal);
            Assert.Contains("structural_coverage_preparation_wait_ms=0", phase.CurrentTarget, StringComparison.Ordinal);
        }
        finally
        {
            releaseLanes.TrySetResult();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Theory]
    [InlineData("passing")]
    [InlineData("missing")]
    [InlineData("empty")]
    public async Task PreparedAndInlineComparisonHaveTheSameVerdictAndDetails(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-coverage-comparison", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var trx = Path.Combine(root, "result.trx");
            File.WriteAllText(trx, """
                <TestRun><TestDefinitions><UnitTest id="1" name="Sample.Tests.Passes"><TestMethod className="Sample.Tests" name="Passes" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="1" testName="Sample.Tests.Passes" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
                """);
            var evaluator = new AcceptanceStructuralCoverageEvaluator((_, _, _, _) =>
                Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                    scenario == "missing"
                        ? "DISCOVERED_TEST:Sample.Tests.Passes\nDISCOVERED_TEST:Sample.Tests.Missing"
                        : "DISCOVERED_TEST:Sample.Tests.Passes")));
            var request = new AcceptanceStructuralCoverageRequest(
                ["discover"], root, TimeSpan.FromMinutes(1), false,
                () => [new TestPartitionCoverage("partition", true,
                    scenario == "empty" ? [] : [trx])],
                () => [], null, [],
                _ => Task.FromResult<AcceptanceStructuralCoverageBaseline?>(null));

            var inline = await evaluator.EvaluateAsync(request, CancellationToken.None);
            var prepared = await evaluator.PrepareAsync(request, CancellationToken.None);
            var split = await evaluator.CompareAsync(request, prepared, CancellationToken.None);
            var expected = Assert.IsType<TestCoverageInvariantResult>(inline.Coverage);
            var actual = Assert.IsType<TestCoverageInvariantResult>(split.Coverage);
            Assert.Equal(expected.Passed, actual.Passed);
            Assert.Equal(expected.Summary, actual.Summary);
            Assert.Equal(expected.FailureClassification, actual.FailureClassification);
            Assert.Equal(expected.MissingTests, actual.MissingTests);
            Assert.Equal(expected.EmptyPartitions, actual.EmptyPartitions);
            // These literals pin the pre-split coverage result, independently of EvaluateAsync.
            var (passed, summary, classification, missing, empty) = scenario switch
            {
                "passing" => (true,
                    "structural coverage complete: discovered=1, executed=1, recorded=1, partitions=1",
                    (string?)null, Array.Empty<string>(), Array.Empty<string>()),
                "missing" => (false,
                    "structural coverage failed: discovered=2, executed=1, recorded=1, missing=1, emptyPartitions=0",
                    AcceptanceFailureClassifications.StructuralCoverageFailed,
                    new[] { "Sample.Tests.Missing" }, Array.Empty<string>()),
                "empty" => (false,
                    "structural coverage failed: discovered=1, executed=0, recorded=0, missing=1, emptyPartitions=1",
                    AcceptanceFailureClassifications.StructuralCoverageFailed,
                    new[] { "Sample.Tests.Passes" }, new[] { "partition" }),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            foreach (var coverage in new[] { expected, actual })
            {
                Assert.Equal(passed, coverage.Passed);
                Assert.Equal(summary, coverage.Summary);
                Assert.Equal(classification, coverage.FailureClassification);
                Assert.Equal(missing, coverage.MissingTests);
                Assert.Equal(empty, coverage.EmptyPartitions);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task LaneFailureSuppressesPreparationDeferralButGreenLanesSurfaceIt(
        bool laneFails, bool mainBuildIsHeld)
    {
        var (root, mainRoot) = CreateWorkspace();
        var goalId = GoalId.New();
        var releaseLanes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainBuildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothLanesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneStarts = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] arguments, string worktree, CancellationToken token)
            {
                if (arguments.Length > 1 && arguments[0] == "dotnet" && arguments[1] == "build")
                {
                    if (worktree == mainRoot)
                    {
                        mainBuildEntered.TrySetResult();
                        if (mainBuildIsHeld)
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return new GoalAcceptanceVerifier.CommandResult(1, "error CS1002: ; expected");
                    }
                    WriteCandidateBuildArtifacts(arguments);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }
                if (arguments.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                    return new GoalAcceptanceVerifier.CommandResult(0,
                        "DISCOVERED_TEST:AlphaShardTests.Passes\nDISCOVERED_TEST:BetaShardTests.Passes");
                if (IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    if (Interlocked.Increment(ref laneStarts) == 2)
                        bothLanesStarted.TrySetResult();
                    await releaseLanes.Task.WaitAsync(token);
                    var alpha = arguments.Any(argument => argument.Contains("AlphaShardTests", StringComparison.Ordinal));
                    WriteMtpTrx(arguments, 1, [alpha ? "AlphaShardTests.Passes" : "BetaShardTests.Passes"]);
                    return alpha && laneFails
                        ? new GoalAcceptanceVerifier.CommandResult(1, "Alpha lane failed")
                        : new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }
                return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var run = verifier.RunAsync(root, goalId, changedFiles: ["src/Sample.cs"],
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease);
            await Task.WhenAll(mainBuildEntered.Task, bothLanesStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, Volatile.Read(ref laneStarts));
            releaseLanes.TrySetResult();
            if (laneFails)
            {
                var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(result.Passed);
                Assert.StartsWith("infrastructure tests:",
                    result.Checks!.First(check => !check.Passed).Name, StringComparison.Ordinal);
                Assert.DoesNotContain(result.Checks!, check =>
                    check.Name.StartsWith("structural test coverage", StringComparison.Ordinal));
            }
            else
            {
                var deferral = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.Equal("trusted-main-build-failed", deferral.ReasonCode);
            }
        }
        finally
        {
            releaseLanes.TrySetResult();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    private static (string CandidateRoot, string MainRoot) CreateWorkspace(bool includeCore = false)
    {
        var coreInvocation = includeCore ? """
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                  },
            """ : string.Empty;
        var coreCheck = includeCore
            ? """{ "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" },"""
            : string.Empty;
        var candidateRoot = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "enforceStructuralCoverage": true,
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests" },
                  { "name": "Beta", "filter": "FullyQualifiedName~BetaShardTests" }
                ],
                "mtpInvocations": [
                  {{coreInvocation}}
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                  }
                ]
              },
              "checks": [
                {{coreCheck}}
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var mainRoot = Path.Combine(Path.GetTempPath(), "mcg-acceptance-main", Guid.NewGuid().ToString("N"));
        foreach (var root in new[] { candidateRoot, mainRoot })
        {
            var projectNames = includeCore
                ? new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" }
                : ["Mcg.AgentOrchestrator.Infrastructure.Tests"];
            foreach (var projectName in projectNames)
            {
                var project = Path.Combine(root, "tests", projectName, projectName + ".csproj");
                Directory.CreateDirectory(Path.GetDirectoryName(project)!);
                File.WriteAllText(project, "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            }
        }
        return (candidateRoot, mainRoot);
    }

    private static void WriteCandidateBuildArtifacts(string[] arguments)
    {
        foreach (var projectName in new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" })
        {
            var outputDirectory = Path.Combine(GetArtifactsPath(arguments), "bin", projectName, "debug");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory,
                projectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), "fixture");
            File.WriteAllText(Path.Combine(outputDirectory, projectName + ".dll"), "fixture");
        }
    }

    private static string CoverageProjectName(string[] arguments)
    {
        if (arguments.Any(argument => argument.Contains("Mcg.AgentOrchestrator.Core.Tests", StringComparison.OrdinalIgnoreCase)))
            return "Core";
        if (arguments.Any(argument => argument.Contains("Mcg.AgentOrchestrator.Infrastructure.Tests", StringComparison.OrdinalIgnoreCase)))
            return "Infrastructure";
        throw new InvalidOperationException("Coverage command did not identify a trusted test project.");
    }
}
