using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsStructuralCoveragePermitHold : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public Task PendingBaselineWithRunningLanes_RefusesExecutionPermit() =>
        RunPermitScenarioAsync(coverageApplies: true, failPrecheck: false, holdBaseline: true);

    [Fact]
    public Task FinishedPreparationWithRunningLanes_ReleasesExecutionPermit() =>
        RunPermitScenarioAsync(coverageApplies: true, failPrecheck: false, holdBaseline: false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public Task UnscheduledPreparationWithRunningLanes_ReleasesExecutionPermit(
        bool coverageApplies, bool failPrecheck) =>
        RunPermitScenarioAsync(coverageApplies, failPrecheck, holdBaseline: false);

    [Fact]
    public async Task SequentialMtpWithCoverage_ReleasesPermitBeforeExecutionAndDiscovery()
    {
        var (root, mainRoot) = CreateWorkspace(coverageApplies: true, failPrecheck: false, maxConcurrentShards: 1);
        var goalId = GoalId.New();
        var baselineStarts = 0;
        var buildCalls = 0;
        var executionCalls = 0;
        var discoveryWorktrees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.OnTrustedMainBaselineBuildStartingForTests = _ => Interlocked.Increment(ref baselineStarts);
        DotnetBuildEnvironmentLease? lease = null;
        try
        {
            Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] arguments, string worktree, CancellationToken token)
            {
                var isBuild = arguments.Length > 1 && arguments[0] == "dotnet" && arguments[1] == "build";
                var isDiscovery = arguments.Contains("--list-tests", StringComparer.OrdinalIgnoreCase);
                var isCore = IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Core.Tests");
                var isInfrastructure = IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Infrastructure.Tests");
                if (isBuild || isDiscovery || isCore || isInfrastructure)
                {
                    var probe = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                        lease!.Environment, TimeSpan.Zero);
                    try
                    {
                        if (isBuild)
                            Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(probe);
                        else
                            Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(probe);
                    }
                    finally
                    {
                        (probe as DotnetBuildLeaseAcquisition.Acquired)?.Lease.Dispose();
                    }
                }

                if (isBuild)
                {
                    buildCalls++;
                    if (worktree == root)
                        WriteCandidateBuildArtifacts(arguments);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }
                if (isDiscovery)
                {
                    discoveryWorktrees.Add(worktree);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        isCore ? "DISCOVERED_TEST:CoreShardTests.Passes"
                            : "DISCOVERED_TEST:AlphaShardTests.Passes\nDISCOVERED_TEST:BetaShardTests.Passes"));
                }
                if (isCore || isInfrastructure)
                {
                    executionCalls++;
                    var name = isCore ? "CoreShardTests.Passes"
                        : arguments.Any(argument => argument.Contains("AlphaShardTests", StringComparison.Ordinal))
                            ? "AlphaShardTests.Passes" : "BetaShardTests.Passes";
                    WriteMtpTrx(arguments, 1, [name]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty));
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunAsync);
            lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(root, goalId, changedFiles: ["src/Sample.cs"],
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease);
            Assert.True(result.Passed, result.OutputTail);
            Assert.True(Assert.Single(result.Checks!, check => check.Name == "structural test coverage").Passed);
            Assert.Equal(2, baselineStarts);
            Assert.True(buildCalls > baselineStarts, "The candidate and both baseline builds must run.");
            Assert.Equal(3, executionCalls);
            Assert.Contains(root, discoveryWorktrees);
            Assert.Contains(mainRoot, discoveryWorktrees);
        }
        finally
        {
            lease?.Dispose();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    private async Task RunPermitScenarioAsync(bool coverageApplies, bool failPrecheck, bool holdBaseline)
    {
        var (root, mainRoot) = CreateWorkspace(coverageApplies, failPrecheck);
        var goalId = GoalId.New();
        var releaseLanes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lanesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baselineStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBaseline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new ConcurrentQueue<AcceptanceGateProgress>();
        var laneStarts = 0;
        var laneFinishes = 0;
        var baselineStarts = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.OnTrustedMainBaselineBuildStartingForTests = _ =>
        {
            Interlocked.Increment(ref baselineStarts);
            baselineStarting.TrySetResult();
            if (holdBaseline)
                AwaitSignalAsync(releaseBaseline.Task, "baseline release").GetAwaiter().GetResult();
        };
        TestOverrides.OnStructuralCoveragePreparationFinishedForTests = () => preparationFinished.TrySetResult();
        Task<AcceptanceVerificationResult>? run = null;
        DotnetBuildEnvironmentLease? lease = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] arguments, string worktree, CancellationToken token)
            {
                if (arguments.Length > 1 && arguments[0] == "dotnet" && arguments[1] == "build")
                {
                    if (worktree == root)
                        WriteCandidateBuildArtifacts(arguments);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                if (arguments.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                {
                    if (worktree == root)
                        await lanesStarted.Task.WaitAsync(token);
                    return new GoalAcceptanceVerifier.CommandResult(0,
                        arguments.Any(argument => argument.Contains("Mcg.AgentOrchestrator.Core.Tests", StringComparison.Ordinal))
                            ? "DISCOVERED_TEST:CoreShardTests.Passes"
                            : "DISCOVERED_TEST:AlphaShardTests.Passes\nDISCOVERED_TEST:BetaShardTests.Passes");
                }

                if (IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Core.Tests"))
                {
                    WriteMtpTrx(arguments, 1, ["CoreShardTests.Passes"]);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                if (IsMtpExecutableCall(arguments, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    if (Interlocked.Increment(ref laneStarts) == 2)
                        lanesStarted.TrySetResult();
                    await releaseLanes.Task.WaitAsync(token);
                    var alpha = arguments.Any(argument => argument.Contains("AlphaShardTests", StringComparison.Ordinal));
                    WriteMtpTrx(arguments, 1, [alpha ? "AlphaShardTests.Passes" : "BetaShardTests.Passes"]);
                    Interlocked.Increment(ref laneFinishes);
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunAsync);
            lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2));
            run = verifier.RunOwnedAsync(root, goalId, changedFiles: ["src/Sample.cs"],
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease,
                cancellation.Token, new AcceptanceRunExecutionOptions(ProgressSink: progress.Enqueue));
            await AwaitSignalAsync(lanesStarted.Task, "both infrastructure lanes started");
            Assert.Equal(2, Volatile.Read(ref laneStarts));
            Assert.Equal(0, Volatile.Read(ref laneFinishes));

            if (coverageApplies && !failPrecheck)
            {
                if (holdBaseline)
                {
                    await AwaitSignalAsync(baselineStarting.Task, "trusted main baseline starting");
                    // The hook precedes the baseline's own permit acquisition. Probing inside
                    // the fake build would also be refused on the original release/reacquire path.
                    var probe = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                        lease.Environment, TimeSpan.Zero);
                    try
                    {
                        Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(probe);
                    }
                    finally
                    {
                        // The negative-control arm acquires a real permit; always free it.
                        (probe as DotnetBuildLeaseAcquisition.Acquired)?.Lease.Dispose();
                    }
                    releaseBaseline.TrySetResult();
                }
                await AwaitSignalAsync(preparationFinished.Task, "structural coverage preparation finished");
                Assert.Equal(2, Volatile.Read(ref baselineStarts));
            }

            // The API's timeout allows the independent precheck result to be published.
            // The verdict is the acquisition outcome, never elapsed real time.
            using (var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                    lease.Environment, TimeSpan.FromSeconds(30))).Lease)
            {
                Assert.Equal(0, Volatile.Read(ref laneFinishes));
                if (!coverageApplies || failPrecheck)
                    Assert.Equal(0, Volatile.Read(ref baselineStarts));
            }

            releaseLanes.TrySetResult();
            await AwaitSignalAsync(run, "gate completion");
            var result = await run;
            if (failPrecheck)
            {
                Assert.False(result.Passed);
                Assert.False(Assert.Single(result.Checks!, check => check.Name == "missing precheck file").Passed);
                Assert.Equal(0, Volatile.Read(ref baselineStarts));
            }
            else
            {
                Assert.True(result.Passed, result.OutputTail);
                if (coverageApplies)
                    Assert.True(Assert.Single(result.Checks!, check => check.Name == "structural test coverage").Passed);
                else
                    Assert.DoesNotContain(result.Checks!, check => check.Name == "structural test coverage");
            }
            Assert.Equal(2, Volatile.Read(ref laneFinishes));
            Assert.DoesNotContain(progress, item => item.Phase == StructuralCoveragePermitWait.PhaseName);
        }
        finally
        {
            releaseBaseline.TrySetResult();
            releaseLanes.TrySetResult();
            cancellation.Cancel();
            if (run is not null)
            {
                try { await AwaitSignalAsync(run, "gate cleanup"); }
                catch when (run.IsCompleted) { /* Preserve the test's assertion or gate fault. */ }
            }
            lease?.Dispose();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    private static async Task AwaitSignalAsync(Task signal, string name)
    {
        try { await signal.WaitAsync(TimeSpan.FromMinutes(1)); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Signal did not arrive: {name}.", exception);
        }
    }

    private static (string CandidateRoot, string MainRoot) CreateWorkspace(
        bool coverageApplies, bool failPrecheck, int maxConcurrentShards = 2)
    {
        var precheck = failPrecheck
            ? """{ "name": "missing precheck file", "type": "file-exists", "filePath": "missing.txt" },"""
            : string.Empty;
        var candidateRoot = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": {{maxConcurrentShards}},
                "enforceStructuralCoverage": {{(coverageApplies ? "true" : "false")}},
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests" },
                  { "name": "Beta", "filter": "FullyQualifiedName~BetaShardTests" }
                ],
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                  },
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                  }
                ]
              },
              "checks": [
                {{precheck}}
                { "name": "core tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" },
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var mainRoot = Path.Combine(Path.GetTempPath(), "mcg-acceptance-main", Guid.NewGuid().ToString("N"));
        foreach (var root in new[] { candidateRoot, mainRoot })
        {
            foreach (var projectName in new[] { "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" })
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
}
