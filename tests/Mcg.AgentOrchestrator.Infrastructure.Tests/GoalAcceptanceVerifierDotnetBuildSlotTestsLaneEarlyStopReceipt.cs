using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsLaneEarlyStop;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLaneEarlyStopReceipt : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ConfirmedFailure_RecordsPartialCoverageWithoutCancelledLaneFailures(bool returnFailureAfterCancellation)
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        ConfigureWorkspace(root, 5, 2, remote: true);
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
        SetPartitionVerdictKeyHooks("receipt-stop-tree", "receipt-stop-main", "receipt-stop-commit");
        var clock = new ManualRemoteLaneClock();
        var configuration = Path.Combine(root, "executors.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "early-stop-executor", leaseSeconds = 60 } },
            lanes = new[] { Lane(0) }
        }));
        RemoteLaneOfferSeeding.PrepareFixture(root, TestOverrides);
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = configuration;
        TestOverrides.RemoteLaneTimeProviderForTests = clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        var handle = new FakeRemoteLaneExecutor.Handle(clock.GetUtcNow());
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle))
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var starts = new ConcurrentQueue<int>();
        var roles = new ConcurrentDictionary<int, int>();
        var running = Signal();
        var cancelled = Signal();
        var releaseCancelledLane = Signal();
        var ordinal = -1;
        var confirmingRuns = 0;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string _, CancellationToken token)
            {
                if (!IsInfrastructurePartitionTestCall(args)) return BuildResult(args);
                var index = LaneIndex(args, 5);
                Assert.NotEqual(0, index); // This lane is offloaded, never a local fallback after the stop.
                if (!roles.TryGetValue(index, out var role))
                {
                    role = Interlocked.Increment(ref ordinal);
                    Assert.True(roles.TryAdd(index, role));
                    starts.Enqueue(index);
                }
                if (role == 0)
                {
                    WriteMtpTrx(args, 1, [$"EarlyStop{index}Tests.Executes"]);
                    return new(0, "Passed: 1");
                }
                if (role == 1)
                {
                    Interlocked.Increment(ref confirmingRuns);
                    await Event(running.Task, "running local sibling", token);
                    await Event(handle.Polled.Task, "remote lane polled before confirmation", token);
                    WriteMtpTrx(args, MtpFailureFixturePath());
                    return new(1, "Fixture test failure.");
                }
                Assert.Equal(2, role); // A fourth local start means the scheduler replenished after confirmation.
                running.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await Event(releaseCancelledLane.Task, "late remote result published before local drain", CancellationToken.None);
                    if (!returnFailureAfterCancellation) throw;
                    WriteMtpTrx(args, MtpFailureFixturePath());
                    return new(1, "Cancelled child returned nonzero.");
                }
                throw new InvalidOperationException("Blocked lane returned without cancellation.");
            }

            AcceptanceVerificationResult? result = null;
            var console = await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(async () =>
            {
                using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
                using var cancellation = new CancellationTokenSource();
                var gate = new GoalAcceptanceVerifier(TestOverrides, Runner).RunOwnedAsync(root, GoalId.New(), null,
                    StableSlotIndex(lease.Environment.ArtifactsPath), lease, cancellation.Token, new AcceptanceRunExecutionOptions());
                try
                {
                    await Event(cancelled.Task, "confirmed failure cancelled its local sibling");
                    // The stop has fired; hold the local drain while the abandoned remote machine finishes.
                    var request = Assert.Single(fake.Requests);
                    var remoteDirectory = Path.Combine(root, "late-remote");
                    string[] remoteArgs = ["--results-directory", remoteDirectory, "--report-trx-filename", "late.trx"];
                    WriteMtpTrx(remoteArgs, 1, ["EarlyStop0Tests.Executes"]);
                    handle.Publish(new RemoteLaneResult(request.ExecutorId, request.Lane, request.FilterHash,
                        request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity,
                        0, [Path.Combine(remoteDirectory, "late.trx")]));
                    Assert.NotNull(handle.TryGetResult());
                    releaseCancelledLane.TrySetResult();
                    await Event(gate, "gate drained after partial-coverage stop");
                    result = await gate;
                }
                finally
                {
                    releaseCancelledLane.TrySetResult();
                    await cancellation.CancelAsync();
                    try { await Event(gate, "gate cleanup"); }
                    catch (OperationCanceledException) { }
                }
            });

            Assert.NotNull(result); // The gate returned, rather than raising missing-outcome or cancellation exceptions.
            Assert.False(result.Passed);
            Assert.Equal(2, confirmingRuns);
            Assert.True(cancelled.Task.IsCompletedSuccessfully);
            Assert.True(handle.IsAbandoned);
            Assert.Equal(3, starts.Count);
            var completedLane = Lane(starts.ElementAt(0));
            var confirmingLane = Lane(starts.ElementAt(1));
            var cancelledLane = Lane(starts.ElementAt(2));
            var pendingLane = Lane(Enumerable.Range(1, 4).Except(starts).Single());
            var receipt = Assert.Single(result.Checks!, check => check.Name == AcceptanceLaneEarlyStop.ReceiptName);
            var summary = $"lane-early-stop confirming_lane=\"{confirmingLane}\" coverage=partial lanes_stopped=3 " +
                $"not_run=[\"{pendingLane}\"] cancelled_mid_run=[\"{cancelledLane}\"] remote_left_to_finish=[\"{Lane(0)}\"]";
            Assert.Equal(summary, receipt.ResultSummary);
            Assert.Equal(summary, receipt.OutputTail);
            Assert.Contains(summary, result.OutputTail);
            Assert.DoesNotContain(completedLane, receipt.ResultSummary);
            Assert.True(Assert.Single(result.Checks!, check => check.Name == completedLane).Passed);
            Assert.False(Assert.Single(result.Checks!, check => check.Name == confirmingLane).Passed);
            Assert.DoesNotContain(result.Checks!, check => check.Name == cancelledLane || check.Name == pendingLane);
            Assert.Single(console.Split('\n'), line => line.Contains("phase=lane-early-stop", StringComparison.Ordinal));

            Assert.DoesNotContain(result.Checks!, check => check.Name == Lane(0));
        }
        finally { ResetPartitionVerdictKeyHooks(); DeleteDirectoryWithRetry(root); }
    }
}
