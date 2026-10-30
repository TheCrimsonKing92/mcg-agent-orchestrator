using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsLaneEarlyStop;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class RemoteShardInvalidationStopTests
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task RemoteShardInvalidationExitsOwnedAttemptAndReleasesLeases()
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        // A single offloaded lane leaves no local check monitor that could mask the remote-wait defect.
        ConfigureWorkspace(root, 1, 3, remote: true);
        // Use the collection fixture's run-isolated root so slot identity is covered by its proof.
        var storageRoot = DotnetBuildEnvironmentManager.CaptureStorageRoot();
        TestOverrides.BuildStorageRootForTests = storageRoot;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        TestOverrides.RemoteLaneEarlyOfferDisabledForTests = true;
        SetPartitionVerdictKeyHooks("invalidated-tree", "invalidated-main", "invalidated-commit");
        var clock = new ManualRemoteLaneClock();
        var configuration = Path.Combine(root, "executors.json");
        const string executorId = "invalidated-executor";
        File.WriteAllText(configuration, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = executorId, leaseSeconds = 60 } },
            lanes = new[] { Lane(0) }
        }));
        RemoteLaneOfferSeeding.PrepareFixture(root, TestOverrides);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = configuration;
        TestOverrides.RemoteLaneTimeProviderForTests = clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        var handle = new FakeRemoteLaneExecutor.Handle(clock.GetUtcNow());
        var remote = new FakeRemoteLaneExecutor
        {
            Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle))
        };
        TestOverrides.RemoteLaneExecutorForTests = remote;
        var cleanup = new ConcurrentQueue<string>();
        var localStarts = new ConcurrentQueue<string>();
        var slotAcquired = new TaskCompletionSource<DotnetBuildEnvironmentLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var goal = new AgentOrchestratorKernel().CreateGoal("Cancel an invalidated remote wait");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "invalidated-commit", "invalidated-main");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, "attempts"), TrunkBranchName.Default, isProcessAlive: _ => true,
            cleanupObservedForTests: (_, phase) => cleanup.Enqueue(phase),
            acquireStableSlotLease: (_, _) =>
            {
                var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.FromSeconds(30));
                Assert.True(lease.IsExecutionLockHeld);
                slotAcquired.TrySetResult(lease);
                return lease;
            });
        var attempt = coordinator.CreateAttemptForTests(candidate);
        var probe = new AcceptanceAttemptCancellationProbe(() => AcceptanceAttemptCancellation.Decide(
            GoalStatus.Verifying, goalRecordReadable: true,
            attemptInvalidationRecorded: coordinator.TryGetLiveInvalidatedAttempt(goal.Id.Value, out _)));
        OperationCanceledException? cancellation = null;
        Task? ownedAttempt = null;
        string console = string.Empty;
        try
        {
            console = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                // Production RunAttempt owns a synchronous run delegate; run that same boundary on a worker thread.
                ownedAttempt = Task.Run(() => coordinator.RunAttemptForTests(attempt, candidate,
                    ConductorAutonomyPolicy.Conservative, (current, _, lease, token, options) =>
                    {
                        Assert.NotNull(lease);
                        Assert.False(token.CanBeCanceled);
                        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
                        {
                            if (IsInfrastructurePartitionTestCall(args))
                                localStarts.Enqueue(string.Join(' ', args));
                            return Task.FromResult(BuildResult(args));
                        });
                        try
                        {
                            var result = verifier.RunOwnedAsync(root, goal.Id, null,
                                StableSlotIndex(lease.Environment.ArtifactsPath), lease, token,
                                options with { CancellationProbe = probe.ShouldCancel, BoundaryCancellationProbe = probe.ShouldCancelNow })
                                .GetAwaiter().GetResult();
                            return ConductorParallelAcceptanceRunResult.Accepted(current,
                                result.Passed ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria : AcceptanceVerificationSummary.Failed);
                        }
                        catch (OperationCanceledException ex)
                        {
                            cancellation = ex;
                            throw;
                        }
                    }));
                try
                {
                    await Event(handle.Polled.Task, "remote shard polled without a result");
                    var hostRoot = AcceptancePartitionVerdictCache.ResolveHostStateRoot(root);
                    Assert.True(RemoteExecutorOccupancy.IsOccupied(hostRoot, executorId));
                    var slotLease = await slotAcquired.Task;
                    var custodyPath = Path.Combine(slotLease.Environment.ArtifactsPath, AcceptanceAttemptArtifactCustody.MarkerFileName);
                    // Prebuild releases the build permit before remote waits; artifact custody lasts until teardown.
                    Assert.True(File.Exists(custodyPath));
                    Assert.DoesNotContain("artifact-custody-released", cleanup);
                    Assert.DoesNotContain("stable-slot-released", cleanup);
                    Assert.Empty(localStarts);
                    Assert.Null(handle.TryGetResult());
                    Assert.Equal(clock.GetUtcNow(), handle.NewestHeartbeat);
                    Assert.True(coordinator.RequestSupersededMainStop(attempt, "replacement-main"));
                    Assert.True(coordinator.TryGetLiveInvalidatedAttempt(goal.Id.Value, out _));

                    await Event(ownedAttempt, "invalidated attempt exited remote wait and completed teardown");

                    Assert.NotNull(cancellation);
                    Assert.Equal(AcceptanceAttemptCancellationCause.AttemptInvalidated, probe.CancellationDecision?.Cause);
                    Assert.True(handle.IsAbandoned);
                    Assert.Single(remote.Requests);
                    Assert.Empty(localStarts);
                    Assert.False(RemoteExecutorOccupancy.IsOccupied(hostRoot, executorId));
                    using var nextClaim = RemoteExecutorOccupancy.TryClaimExclusive(hostRoot, executorId);
                    Assert.NotNull(nextClaim);
                    Assert.False(File.Exists(custodyPath));
                    Assert.False(slotLease.IsExecutionLockHeld);
                    Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(
                        StableSlotIndex(slotLease.Environment.ArtifactsPath), storageRoot));
                }
                finally
                {
                    // A broken cancellation bridge can still drain after a real result; never leave its task running.
                    if (!ownedAttempt.IsCompleted && remote.Requests.TryPeek(out var request))
                        PublishCleanupResult(root, request, handle);
                    await Event(ownedAttempt, "owned attempt cleanup completed");
                }
            });

            Assert.Contains("artifact-custody-released", cleanup);
            Assert.Contains("stable-slot-released", cleanup);
            Assert.True(Array.IndexOf(cleanup.ToArray(), "artifact-custody-released") <
                Array.IndexOf(cleanup.ToArray(), "stable-slot-released"));
            var release = Assert.Single(console.Split('\n'), line => line.StartsWith("ACCEPTANCE_LEASE_RELEASE ", StringComparison.Ordinal));
            Assert.Contains($"attempt={attempt.AttemptId} ", release);
            var final = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(attempt.MetadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, final!.Outcome);
            Assert.Equal("1", File.ReadAllText(attempt.ExitCodePath));
        }
        finally
        {
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void PublishCleanupResult(string root, RemoteLaneRequest request, FakeRemoteLaneExecutor.Handle handle)
    {
        var directory = Path.Combine(root, "cleanup-result");
        WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", "cleanup.trx"], 1, ["EarlyStop0Tests.Executes"]);
        handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
            request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, "cleanup.trx")]));
    }
}
