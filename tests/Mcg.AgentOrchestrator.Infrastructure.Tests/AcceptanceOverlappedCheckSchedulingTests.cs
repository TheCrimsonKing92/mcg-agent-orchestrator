using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierTestsOverlappedCheckScheduling
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task VerifierAttemptCarriesKnownLaneTimingAndSlotWaitIntoBreakdown()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests" },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~AlphaShardTests&Category!=HostIntegration" }
                ],
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--no-ansi", "--progress", "off", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        try
        {
            Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] arguments,
                string _,
                CancellationToken __)
            {
                if (TryWriteMtpBuildArtifacts(arguments))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));

                var filterClassIndex = Array.IndexOf(arguments, "--filter-class");
                var isAlpha = filterClassIndex >= 0 &&
                    arguments[filterClassIndex + 1].Contains("AlphaShardTests", StringComparison.Ordinal);
                if (isAlpha)
                    time.Advance(TimeSpan.FromMilliseconds(400));
                else
                    time.Advance(TimeSpan.FromMilliseconds(750));
                WriteMtpTrx(arguments);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "passed deterministically"));
            }

            var verifier = new GoalAcceptanceVerifier(RunAsync, time);
            using var sink = GoalAcceptanceVerifier.PushGateProgressSink(progress.Add);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2));
            var expectedSlotWait = lease.SlotWaitDuration;

            var result = await verifier.RunAsync(
                root,
                new GoalId("55555555555555555555555555555555"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed, result.OutputTail);
            var emitted = Assert.Single(progress, item => item.Phase == "gate-phase-breakdown");
            var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(emitted.PhaseBreakdown);
            Assert.Equal(2, breakdown.EffectiveShardConcurrency);
            Assert.Equal(1, breakdown.PeakShardConcurrency);
            Assert.Equal(TimeSpan.FromMilliseconds(750), breakdown.LongestLaneDuration);
            Assert.Equal(expectedSlotWait, breakdown.SlotWaitDuration);
            Assert.Contains("shard_concurrency_effective=2", emitted.CurrentTarget, StringComparison.Ordinal);
            Assert.Contains("longest_lane_ms=750", emitted.CurrentTarget, StringComparison.Ordinal);
            Assert.DoesNotContain("slot_wait_ms=unavailable", emitted.CurrentTarget, StringComparison.Ordinal);
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task IndependentCommandStartsBeforeLanesComplete_AndStructuralCoverageWaitsForLanes()
    {
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lanesCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedLaneCount = 0;
        var structuralCoverageObservedLaneCount = -1;
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~AlphaShardTests" },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~AlphaShardTests&Category!=HostIntegration" }
                ],
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--no-ansi", "--progress", "off", "--results-directory", "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [
                { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                { "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        GoalAcceptanceVerifier.OnStructuralCoverageStartedForTests = () =>
            structuralCoverageObservedLaneCount = Volatile.Read(ref completedLaneCount);
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] arguments,
                string _,
                CancellationToken cancellationToken)
            {
                if (arguments.Length > 0 && arguments[0] == "git")
                {
                    commandStarted.TrySetResult();
                    await lanesCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
                }
                if (TryWriteMtpBuildArtifacts(arguments))
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");

                WriteMtpTrx(arguments);
                await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (Interlocked.Increment(ref completedLaneCount) == 2)
                    lanesCompleted.TrySetResult();
                return new GoalAcceptanceVerifier.CommandResult(0, "passed deterministically");
            }

            var verifier = new GoalAcceptanceVerifier(RunAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId("44444444444444444444444444444444"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(commandStarted.Task.IsCompleted);
            Assert.Equal(2, completedLaneCount);
            Assert.Equal(2, structuralCoverageObservedLaneCount);
            Assert.True(result.Passed, result.OutputTail);
        }
        finally
        {
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            GoalAcceptanceVerifier.OnStructuralCoverageStartedForTests = null;
            DeleteDirectoryWithRetry(root);
        }
    }

    private static bool TryWriteMtpBuildArtifacts(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != "dotnet" ||
            arguments.Length >= 2 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return false;
        if (arguments.Length < 2 || arguments[1] != "build")
            return true;

        var baseOutputIndex = Array.IndexOf(arguments, "--artifacts-path");
        Assert.True(baseOutputIndex >= 0 && baseOutputIndex + 1 < arguments.Length);
        var output = Path.Combine(
            arguments[baseOutputIndex + 1],
            "bin",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "debug");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "Mcg.AgentOrchestrator.Infrastructure.Tests.dll"), "fixture");
        File.WriteAllText(
            Path.Combine(output, "Mcg.AgentOrchestrator.Infrastructure.Tests" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)),
            "fixture");
        return true;
    }
}
