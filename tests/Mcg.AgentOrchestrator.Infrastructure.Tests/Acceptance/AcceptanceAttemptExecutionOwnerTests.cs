using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

public sealed class AcceptanceAttemptExecutionOwnerTests
{
    [Fact]
    public async Task Compatibility_owner_cache_reads_receipt_written_by_its_invocation()
    {
        var root = CreateTempRoot();
        var attemptId = "compatibility-attempt";
        await using var owner = CreateOwner(
            root,
            attemptId,
            "candidate",
            "main",
            publishResultsPrefix: false);
        var environment = new Dictionary<string, string?>();
        owner.ApplyApparatusReceiptEnvironment(environment);
        var receiptPath = Assert.IsType<string>(
            environment[TempRootApparatusLossReceiptStore.ReceiptPathVariable]);
        var sharedRoot = Path.Combine(root, "shared-temp");
        var firstStartedAt = DateTimeOffset.Parse("2026-09-18T20:00:00Z");
        var secondStartedAt = firstStartedAt.AddSeconds(1);
        TempRootApparatusLossReceiptStore.Append(
            receiptPath,
            new TempRootApparatusLossReceiptV1(
                1,
                attemptId,
                "compatibility-receipt",
                sharedRoot,
                [
                    DestroyedOwner(sharedRoot, 7101, firstStartedAt),
                    DestroyedOwner(sharedRoot, 7102, secondStartedAt)
                ],
                firstStartedAt.AddMinutes(1)));
        var first = Partition("first");
        var second = Partition("second");
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(
                new AcceptancePartitionVerdictCacheOptions(
                    new GoalId("6032a3d4a62f4587a4477de83d86af11"),
                    root,
                    [first, second],
                    FullRerunEveryN: 5,
                    WithinAttemptRerunEnabled: true,
                    _ => "candidate",
                    _ => "main",
                    _ => "commit",
                    () => attemptId,
                    () => "manifest",
                    () => false,
                    () => TempRootApparatusLossReceiptStore.Read(owner.ApparatusReceiptPath))));

        cache.ObserveSharedApparatusEvidence(first, FailedOwnerResult(first.Name, 7101, firstStartedAt));
        cache.ObserveSharedApparatusEvidence(second, FailedOwnerResult(second.Name, 7102, secondStartedAt));

        Assert.NotNull(cache.SharedApparatusInvalidation);
        Assert.Equal("compatibility-receipt", cache.SharedApparatusInvalidation.FirstReceipt.ReceiptId);
    }

    [Fact]
    public async Task Interleaved_attempts_keep_identity_cancellation_and_progress_isolated()
    {
        var root = CreateTempRoot();
        var progressA = new ConcurrentQueue<string>();
        var progressB = new ConcurrentQueue<string>();
        await using var ownerA = CreateOwner(root, "attempt-a", "candidate-a", "main-a", progressA);
        await using var ownerB = CreateOwner(root, "attempt-b", "candidate-b", "main-b", progressB);
        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var childA = ownerA.TrackStarted(WaitForCancellationAsync(ownerA.CancellationToken, startedA));
        var childB = ownerB.TrackStarted(WaitForReleaseAsync(releaseB.Task, startedB));
        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(5));

        ownerA.ReportProgress(CreateProgress("attempt-a", "phase-a"));
        ownerB.ReportProgress(CreateProgress("attempt-b", "phase-b"));
        ownerA.Cancel();
        await childA.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(childB.IsCompleted);
        Assert.Equal("candidate-a", ownerA.Identity.CandidateTreeSha);
        Assert.Equal("candidate-b", ownerB.Identity.CandidateTreeSha);
        Assert.Equal("main-a", ownerA.Identity.MainSha);
        Assert.Equal("main-b", ownerB.Identity.MainSha);
        Assert.Equal(["phase-a"], progressA);
        Assert.Equal(["phase-b"], progressB);
        Assert.NotEqual(ownerA.ResultsPrefix, ownerB.ResultsPrefix);
        var receiptEnvironmentA = new Dictionary<string, string?>();
        var receiptEnvironmentB = new Dictionary<string, string?>();
        ownerA.ApplyApparatusReceiptEnvironment(receiptEnvironmentA);
        ownerB.ApplyApparatusReceiptEnvironment(receiptEnvironmentB);
        Assert.Equal("attempt-a", receiptEnvironmentA[TempRootApparatusLossReceiptStore.GateInvocationIdVariable]);
        Assert.Equal("attempt-b", receiptEnvironmentB[TempRootApparatusLossReceiptStore.GateInvocationIdVariable]);
        Assert.NotEqual(
            receiptEnvironmentA[TempRootApparatusLossReceiptStore.ReceiptPathVariable],
            receiptEnvironmentB[TempRootApparatusLossReceiptStore.ReceiptPathVariable]);

        releaseB.SetResult();
        await childB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Synchronous_lane_exception_cancels_and_observes_started_independent_task()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-sync", "candidate", "main");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AcceptanceOverlappedCheckRunner.RunAsync(
                async () =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, owner.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        observed.SetResult();
                    }

                    return 1;
                },
                () => throw new InvalidOperationException("lane-sync-failure"),
                owner));

        Assert.Equal("lane-sync-failure", exception.Message);
        Assert.True(observed.Task.IsCompleted, "the started independent task must be observed before the lane failure escapes");
    }

    [Fact]
    public async Task Unresolved_child_is_an_actionable_hold()
    {
        var root = CreateTempRoot();
        var unresolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = CreateOwner(
            root,
            "attempt-held",
            "candidate",
            "main",
            options: new AcceptanceRunExecutionOptions(DrainTimeout: TimeSpan.FromMilliseconds(50)));
        owner.TrackStarted(unresolved.Task);

        var exception = await Assert.ThrowsAsync<AcceptanceExecutionDrainException>(() => owner.DisposeAsync().AsTask());
        Assert.Contains("teardown is on hold", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1 child task", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => owner.CreateInvocation("rearmed"));

        unresolved.SetResult();
        await owner.DisposeAsync();
    }

    [Fact]
    public void Execution_failure_remains_authoritative_when_drain_holds()
    {
        var root = CreateTempRoot();
        var unresolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = CreateOwner(
            root,
            "attempt-primary-failure",
            "candidate",
            "main",
            options: new AcceptanceRunExecutionOptions(DrainTimeout: TimeSpan.FromMilliseconds(50)));
        owner.TrackStarted(unresolved.Task);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceExecutionOwnerLifetime.Run<int>(owner, () => throw new InvalidOperationException("primary")));

        Assert.Equal("primary", exception.Message);
        Assert.Contains("acceptance-execution-owner-teardown-failure", exception.Data.Keys.Cast<string>());
        unresolved.SetResult();
    }

    [Fact]
    public async Task Focused_verification_has_its_own_identity_without_an_attempt_identity()
    {
        var root = CreateTempRoot();
        var identity = new AcceptanceFocusedVerificationIdentity(
            "focused-17",
            "goal-17",
            root,
            "candidate-17",
            Path.Combine(root, "focused-17"),
            SlotIndex: null);
        await using var owner = new AcceptanceFocusedVerificationOwner(
            identity,
            new AcceptanceGateEngineSettings());

        Assert.Equal("focused-17", owner.RunId);
        Assert.Equal("candidate-17", owner.Identity.CandidateSha);
        Assert.DoesNotContain("attempt", owner.Identity.GetType().GetProperties().Select(property => property.Name), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invocation_identity_is_unique_and_retains_attempt_attribution()
    {
        var root = CreateTempRoot();
        await using var owner = CreateOwner(root, "attempt-invocation", "candidate", "main");

        var first = owner.CreateInvocation("infrastructure-tests");
        var second = owner.CreateInvocation("infrastructure-tests");

        Assert.Equal(0, first.Ordinal);
        Assert.Equal(1, second.Ordinal);
        Assert.StartsWith("attempt-invocation:", first.InvocationId, StringComparison.Ordinal);
        Assert.NotEqual(first.InvocationId, second.InvocationId);
    }

    [Fact]
    public async Task Attempt_reacquisition_preserves_live_custody()
    {
        var root = CreateTempRoot();
        var storageRoot = new DotnetBuildStorageRoot(Path.Combine(root, "dotnet"));
        var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0, storageRoot: storageRoot);
        var attemptId = $"attempt-custody-{Guid.NewGuid():N}";
        var metadataPath = Path.Combine(root, $"{attemptId}.attempt.json");
        await using var owner = CreateOwner(
            root,
            attemptId,
            "candidate",
            "main",
            options: new AcceptanceRunExecutionOptions(LivenessCheckHint: metadataPath));
        AcceptanceAttemptArtifactCustody.Write(
            environment.ArtifactsPath,
            attemptId,
            metadataPath,
            Environment.ProcessId);

        using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
            environment,
            timeProvider: TimeProvider.System,
            artifactCustody: owner.ArtifactCustody);

        var markerPath = AcceptanceAttemptArtifactCustody.MarkerPath(environment.ArtifactsPath);
        Assert.True(File.Exists(markerPath));
        Assert.Contains(attemptId, File.ReadAllText(markerPath), StringComparison.Ordinal);
    }

    private static AcceptanceAttemptExecutionOwner CreateOwner(
        string root,
        string attemptId,
        string candidate,
        string main,
        ConcurrentQueue<string>? progress = null,
        AcceptanceRunExecutionOptions? options = null,
        bool publishResultsPrefix = true)
    {
        options ??= new AcceptanceRunExecutionOptions(
            ProgressSink: value => progress?.Enqueue(value.Phase));
        var identity = new AcceptanceAttemptIdentity(
            attemptId,
            "goal-17",
            root,
            candidate,
            main,
            candidate,
            Path.Combine(root, attemptId),
            SlotIndex: null,
            Environment.ProcessId,
            options.LivenessCheckHint);
        return new AcceptanceAttemptExecutionOwner(
            identity,
            new AcceptanceGateEngineSettings(),
            options: options,
            publishResultsPrefix: publishResultsPrefix);
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck Partition(string id) => new()
    {
        Name = $"infrastructure tests: {id}",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", $"FullyQualifiedName~{id}"]
    };

    private static AcceptanceCheckResult FailedOwnerResult(
        string name,
        int processId,
        DateTimeOffset startedAt) =>
        new(
            name,
            false,
            1,
            "ordinary shard failure",
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false,
                AcceptanceShardCompletionPredicates.NonzeroExit,
                false,
                1,
                1,
                1,
                "failed"),
            ChildProcessId: processId,
            ChildProcessStartedAt: startedAt);

    private static TempRootApparatusDestroyedOwner DestroyedOwner(
        string sharedRoot,
        int processId,
        DateTimeOffset startedAt) =>
        new(
            processId,
            startedAt,
            TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId));

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken, TaskCompletionSource started)
    {
        started.SetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitForReleaseAsync(Task release, TaskCompletionSource started)
    {
        started.SetResult();
        await release;
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-acceptance-owner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AcceptanceGateProgress CreateProgress(string goalId, string phase)
    {
        var now = DateTimeOffset.UtcNow;
        return new AcceptanceGateProgress(
            goalId,
            phase,
            "owner-test",
            SlotIndex: null,
            Environment.ProcessId,
            ChildProcessId: null,
            now,
            now,
            now,
            TimeSpan.Zero,
            OutputBytes: 0,
            HeartbeatPath: string.Empty);
    }
}
