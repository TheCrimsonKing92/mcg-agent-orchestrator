using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSharedApparatusInvalidation
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(Timeout = 60_000)]
    public async Task CorrelatedLoss_CancelsInFlightShardAndStopsPendingShards()
    {
        GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = () => 2;
        GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
        SetPartitionVerdictKeyHooks("shared-loss-tree", "shared-loss-main", "shared-loss-candidate");
        var root = CreateCheckedInManifestShapeWorkspace();
        var attemptPrefix = Path.Combine(root, "attempt", "shared-loss-attempt");
        var receiptPath = TempRootApparatusLossReceiptStore.ResolvePath(attemptPrefix)!;
        var sharedRoot = Path.Combine(root, "owned-temp-parent");
        var firstStartedAt = DateTimeOffset.Parse("2026-09-02T14:00:00Z");
        var secondStartedAt = firstStartedAt.AddSeconds(1);
        var firstOriginalCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOriginalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlightShardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shardCalls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var shardOrdinal = 0;
        DotnetBuildEnvironmentLease? lease = null;
        DotnetBuildEnvironment? environment = null;
        try
        {
            using var attemptScope = GoalAcceptanceVerifier.PushAcceptanceAttemptResultsPrefix(attemptPrefix);
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] args,
                string _,
                CancellationToken cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(args, "shared apparatus batch fixture"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                var filterIndex = Array.IndexOf(args, "--filter-class");
                if (filterIndex < 0)
                {
                    filterIndex = Array.IndexOf(args, "--filter-not-class");
                }
                if (filterIndex < 0)
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
                }

                WriteMtpTrx(args);
                if (!IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }

                var filter = args[filterIndex + 1];
                shardCalls.Enqueue(filter);
                var ordinal = Interlocked.Increment(ref shardOrdinal);
                if (ordinal == 1)
                {
                    await secondOriginalStarted.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken);
                    firstOriginalCompleted.TrySetResult();
                    return FailedShard(5101, firstStartedAt, "first owner lost");
                }

                if (ordinal == 2)
                {
                    secondOriginalStarted.TrySetResult();
                    await firstOriginalCompleted.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken);
                    TempRootApparatusLossReceiptStore.Append(
                        receiptPath,
                        new TempRootApparatusLossReceiptV1(
                            1,
                            "shared-loss-attempt",
                            "first-discriminating-receipt",
                            sharedRoot,
                            [
                                DestroyedOwner(sharedRoot, 5101, firstStartedAt),
                                DestroyedOwner(sharedRoot, 5102, secondStartedAt)
                            ],
                            firstStartedAt.AddMinutes(1)));
                    await inFlightShardStarted.Task.WaitAsync(
                        TimeSpan.FromSeconds(10),
                        TestContext.Current.CancellationToken);
                    return FailedShard(5102, secondStartedAt, "second owner lost");
                }

                Xunit.Assert.Equal(3, ordinal);
                inFlightShardStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The in-flight retry was not cancelled.");
            }

            var verifier = new GoalAcceptanceVerifier(RunAsync);
            lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            environment = lease.Environment;
            var slot = StableSlotIndex(environment.ArtifactsPath);

            var result = await verifier.RunAsync(
                root,
                new GoalId("6f9ddf547adb404187dc00863bb749c1"),
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"],
                stableSlotIndex: slot,
                stableSlotLease: lease,
                cancellationToken: TestContext.Current.CancellationToken);

            Xunit.Assert.False(result.Passed);
            Xunit.Assert.Equal(3, shardCalls.Count);
            Xunit.Assert.Equal(3, shardCalls.Distinct(StringComparer.Ordinal).Count());
            var invalidated = Xunit.Assert.Single(
                result.Checks!,
                check => check.FailureClassification ==
                    AcceptanceFailureClassifications.SharedGateApparatusInvalidated);
            Xunit.Assert.Contains(
                "receipt_id=first-discriminating-receipt",
                invalidated.FailureCauseEvidence?.Evidence,
                StringComparison.Ordinal);
            Xunit.Assert.Contains("affected_owners=", invalidated.FailureCauseEvidence?.Evidence);

            lease.Dispose();
            lease = null;
            Xunit.Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(slot));
        }
        finally
        {
            lease?.Dispose();
            if (environment is not null)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(environment);
            }
            GoalAcceptanceVerifier.ResolveShardCoreBudgetForTests = null;
            GoalAcceptanceVerifier.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    private static GoalAcceptanceVerifier.CommandResult FailedShard(
        int processId,
        DateTimeOffset startedAt,
        string output) =>
        new(
            7,
            output,
            ChildProcessId: processId,
            ChildProcessStartedAt: startedAt);

    private static TempRootApparatusDestroyedOwner DestroyedOwner(
        string sharedRoot,
        int processId,
        DateTimeOffset startedAt) =>
        new(processId, startedAt, TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId));

    private static bool TryWriteMtpBuildArtifacts(string[] arguments, string content)
    {
        if (arguments.Length == 0 ||
            !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Length >= 2 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (arguments.Length >= 2 && arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            var projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
            var outputDirectory = Path.Combine(GetArtifactsPath(arguments), "bin", projectName, "debug");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(
                Path.Combine(outputDirectory, projectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)),
                content);
            File.WriteAllText(Path.Combine(outputDirectory, $"{projectName}.dll"), content);
        }

        return true;
    }
}
