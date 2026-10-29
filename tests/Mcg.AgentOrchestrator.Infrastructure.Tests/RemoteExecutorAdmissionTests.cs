using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: fixture-owned roots, captured configurations, clock and transport.
public sealed class RemoteExecutorAdmissionTests
{
    [Theory]
    [InlineData("Gate")]
    [InlineData("FocusedEvidence")]
    [InlineData("Shadow")]
    public void Claim_DisabledSnapshot_SelectsEnabledPeerWithoutClaimingDisabled(string claimClass)
    {
        using var scenario = new Scenario();
        var configuration = scenario.Configuration(false) with
        { Executors = [new("one", 60, AdmissionEnabled: false), new("two", 60)] };
        using var coordinator = scenario.Coordinator(configuration, Enum.Parse<RemoteExecutorClaimClass>(claimClass));
        Assert.Equal("two", coordinator.TryClaimIdleExecutor()!.Id);
        Assert.Null(coordinator.TryClaimIdleExecutor());
        using var disabledSlot = RemoteExecutorOccupancy.TryClaimExclusive(scenario.Root, "one", 0);
        Assert.NotNull(disabledSlot);
        Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.Root, "one"));
        Assert.Empty(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Gate_AllDisabled_RecordsPolicyReasonWithoutHealthFailure()
    {
        using var scenario = new Scenario();
        using var coordinator = scenario.Coordinator(scenario.Configuration(false));
        Assert.True(coordinator.IsEligible(Scenario.Lane));
        Assert.Null(await coordinator.ClaimForGateAsync(Scenario.Lane, default));
        Assert.Null(coordinator.TryClaimIdleExecutor(Scenario.Lane));
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(scenario.Root)));
        Assert.Equal(RemoteLaneOutcomeCode.OfferSkippedNoExecutor, row.Outcome);
        Assert.Equal("no-enabled-executor", row.Reason);
        Assert.Null(row.FaultOwner);
        Assert.Equal("disabled", Assert.Single(row.Occupancy!).State);
        Assert.Empty(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Replacement_CapturedAttempt_CompletesOwnedJobAndReenableAffectsFuture()
    {
        using var scenario = new Scenario();
        var path = Path.Combine(scenario.Root, "executors.json");
        File.WriteAllText(path, "{\"executors\":[{\"id\":\"one\"}],\"lanes\":[\"infrastructure tests: A\"]}");
        var captured = RemoteLaneExecutorConfiguration.Load(path);
        using var oldAttempt = scenario.Coordinator(captured);
        var entry = Assert.IsType<RemoteLaneExecutorEntry>(oldAttempt.TryClaimIdleExecutor());
        var release = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Executor.Submit = (_, _) => release.Task;
        var running = oldAttempt.RunRemoteAsync(Scenario.Lane, entry, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), default);
        try
        {
            var request = await Event(scenario.Executor.Submitted.Task, "gate submission entered"); // Hang guard: submit entered.
            Replace(false);
            using var disabled = scenario.Coordinator(RemoteLaneExecutorConfiguration.Load(path));
            Assert.Null(disabled.TryClaimIdleExecutor(out var reason));
            Assert.Equal("no-enabled-executor", reason);
            Assert.True(RemoteExecutorOccupancy.IsOccupied(scenario.Root, "one"));
            var handle = scenario.CompletedHandle(request);
            release.SetResult(new(handle));
            var outcome = await Event(running, "owned gate settlement"); // Hang guard: owned settlement.
            Assert.NotNull(outcome.Accepted);
            Assert.Equal(1, handle.Abandons);
            Assert.Equal(0, handle.Cancellations);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.Root, "one"));
            // Pending offers belonging to the older capture retain its admission decision too.
            Assert.NotNull(oldAttempt.TryClaimIdleExecutor());
            oldAttempt.Release("one");
            Replace(true);
            Assert.Null(disabled.TryClaimIdleExecutor()); // Reenable never mutates a prior snapshot.
            using var future = scenario.Coordinator(RemoteLaneExecutorConfiguration.Load(path));
            Assert.NotNull(future.TryClaimIdleExecutor());
            var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(scenario.Root)));
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
            Assert.Equal(request.VerifyingCommitSha, row.Expected.Commit);
            Assert.Single(scenario.Executor.Requests);
        }
        finally
        {
            release.TrySetResult(new(null, "fixture-cleanup"));
            await Event(running, "captured gate job drained");
        }

        void Replace(bool enabled)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, RemoteLaneExecutorSettingRewrite.RewriteEnabled(File.ReadAllBytes(path), "one", enabled));
            File.Move(temporary, path, overwrite: true);
        }
    }

    internal sealed class Scenario : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("executor-admission-").FullName;
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal FakeRemoteLaneExecutor Executor { get; } = new();
        internal static Check Lane => new()
        {
            Name = "infrastructure tests: A", Type = "dotnet-test", Project = ExecutorScreeningPlanner.Project,
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        internal RemoteLaneExecutorConfiguration Configuration(bool enabled = true) => new(
            [new("one", 60, AdmissionEnabled: enabled)], [Lane.Name], null);
        internal RemoteLaneCoordinator Coordinator(RemoteLaneExecutorConfiguration configuration,
            RemoteExecutorClaimClass claimClass = RemoteExecutorClaimClass.Gate, string? root = null) => new(configuration,
            new RemoteLaneCandidateIdentity(Guid.NewGuid().ToString("N"), "goal", "commit", "tree", "main", "manifest"),
            root ?? Root, Path.Combine(Root, "retained", "result"), Executor, Clock, TimeSpan.FromMilliseconds(1), null,
            claimClass: claimClass);
        internal CountingHandle CompletedHandle(RemoteLaneRequest request)
        {
            var trx = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".trx");
            File.WriteAllText(trx, "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
            return new(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [trx]), Clock.GetUtcNow());
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    internal sealed class CountingHandle(RemoteLaneResult result, DateTimeOffset heartbeat) : IRemoteLaneHandle, IRemoteLaneQueuedJobCancellation
    {
        internal int Abandons { get; private set; }
        internal int Cancellations { get; private set; }
        internal TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? NewestHeartbeat => heartbeat;
        public RemoteLaneResult? TryGetResult() => result;
        public void Abandon() { Abandons++; Abandoned.TrySetResult(); }
        public void RequestQueuedJobCancellation() => Cancellations++;
    }

    internal static async Task<T> Event<T>(Task<T> task, string name)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
}
