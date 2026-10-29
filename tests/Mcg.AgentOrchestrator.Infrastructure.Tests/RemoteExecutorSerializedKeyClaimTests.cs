using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: all claims and ledger rows live under the fixture's GUID host root.
public sealed class RemoteExecutorSerializedKeyClaimTests
{
    [Fact]
    public async Task SameKey_BlocksPeerAndAnotherAttemptButLeavesKeylessSlotFree()
    {
        using var f = new Fixture();
        using var first = f.Coordinator("first");
        using var second = f.Coordinator("second");
        Assert.Equal("one", (await first.ClaimForGateAsync(Lane("A", "K"), default))?.Id);
        f.AssertHeld("one", "K");
        Assert.Null(await first.ClaimForGateAsync(Lane("B", "K"), default));
        Assert.Null(await second.ClaimForGateAsync(Lane("D", "K"), default));
        Assert.Equal("one", (await first.ClaimForGateAsync(Lane("C"), default))?.Id);
        var rows = f.Health();
        Assert.Equal(2, rows.Count);
        foreach (var attempt in new[] { "first", "second" })
        {
            var row = Assert.Single(rows, row => row.GateAttemptId == attempt);
            Assert.Equal(RemoteLaneOutcomeCode.OfferSkippedNoExecutor, row.Outcome);
            Assert.StartsWith("serialized-key-busy=K no-idle-slot ", row.Reason!);
        }
    }

    [Fact]
    public async Task SameKey_SelectsAnotherExecutor()
    {
        using var f = new Fixture();
        using var coordinator = f.Coordinator("attempt", twoExecutors: true);
        Assert.Equal("one", (await coordinator.ClaimForGateAsync(Lane("A", "K"), default))?.Id);
        Assert.Equal("two", (await coordinator.ClaimForGateAsync(Lane("B", "K"), default))?.Id);
        f.AssertHeld("one", "K");
        f.AssertHeld("two", "K");
    }

    [Fact]
    public async Task LaterKeyBusy_RollsBackEarlierKeyWithoutTakingSlot()
    {
        using var f = new Fixture();
        using var holder = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", ["K2"], out _);
        Assert.NotNull(holder);
        using var coordinator = f.Coordinator("attempt");
        Assert.Null(await coordinator.ClaimForGateAsync(Lane("A", "K2", "K1"), default));
        using var earlier = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", ["K1"], out var busy);
        Assert.NotNull(earlier);
        Assert.Null(busy);
        Assert.False(RemoteExecutorOccupancy.IsOccupied(f.Root, "one"));
    }

    [Theory]
    [InlineData("acceptance")]
    [InlineData("executor-busy")]
    [InlineData("unreachable")]
    [InlineData("cache-reuse")]
    [InlineData("abandon")]
    [InlineData("dispose")]
    public async Task Settlement_ReleasesOnlyTheSettledLanesKeys(string path)
    {
        using var f = new Fixture();
        using var coordinator = f.Coordinator("attempt");
        var lane = Lane("A", "K");
        var peer = Lane("B", "K2");
        var entry = Assert.IsType<RemoteLaneExecutorEntry>(await coordinator.ClaimForGateAsync(lane, default));
        Assert.NotNull(await coordinator.ClaimForGateAsync(peer, default));
        f.AssertHeld("one", "K");
        f.AssertHeld("one", "K2");
        switch (path)
        {
            case "acceptance":
                f.Executor.Submit = (request, _) => Task.FromResult(new RemoteLaneSubmission(f.CompletedHandle(request)));
                var accepted = await coordinator.RunRemoteAsync(lane, entry, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), default);
                Assert.NotNull(accepted.Accepted);
                Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(f.Health()).Outcome);
                break;
            case "executor-busy":
                f.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, "executor-busy"));
                Assert.Null((await coordinator.RunRemoteAsync(lane, entry, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), default)).Accepted);
                Assert.Equal(RemoteLaneOutcomeCode.ExecutorBusy, Assert.Single(f.Health()).Outcome);
                break;
            case "unreachable":
                Assert.Null((await coordinator.RunRemoteAsync(lane, entry, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), default)).Accepted);
                Assert.Equal(RemoteLaneOutcomeCode.Unreachable, Assert.Single(f.Health()).Outcome);
                break;
            case "cache-reuse": coordinator.Release(entry.Id, lane.Name); break;
            case "abandon": coordinator.AbandonAll(); break;
            case "dispose": coordinator.Dispose(); break;
            default: throw new InvalidOperationException(path);
        }
        using var probe = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, entry.Id, ["K"], out var busy);
        Assert.NotNull(probe);
        Assert.Null(busy);
        if (path is "abandon" or "dispose")
        {
            using var peerProbe = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, entry.Id, ["K2"], out _);
            Assert.NotNull(peerProbe);
        }
        else f.AssertHeld("one", "K2");
    }

    [Fact]
    public async Task SlotBusy_ReleasesKeyBeforeTryingNextExecutor()
    {
        using var f = new Fixture();
        using var slot0 = RemoteExecutorOccupancy.TryClaimExclusive(f.Root, "one", 0);
        using var slot1 = RemoteExecutorOccupancy.TryClaimExclusive(f.Root, "one", 1);
        Assert.NotNull(slot0);
        Assert.NotNull(slot1);
        using var coordinator = f.Coordinator("attempt", twoExecutors: true);
        Assert.Equal("two", (await coordinator.ClaimForGateAsync(Lane("A", "K"), default))?.Id);
        using var probe = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", ["K"], out _);
        Assert.NotNull(probe);
    }

    [Fact]
    public void LockFiles_UseOrderedDistinctHashedKeysAndDisposeIsIdempotent()
    {
        using var f = new Fixture();
        var claim = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", ["xunit:Z", "xunit:A", "xunit:A"], out var busy);
        Assert.NotNull(claim);
        Assert.Null(busy);
        var folder = RemoteExecutorOccupancy.Folder(f.Root, "one");
        var expected = new[] { "xunit:A", "xunit:Z" }.Select(key => "key-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16] + ".lock").Order();
        Assert.Equal(expected, Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        using (claim)
        {
            Assert.Null(RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", ["xunit:Z", "xunit:A"], out busy));
            Assert.Equal("xunit:A", busy);
        }
        claim.Dispose();
        using var next = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", ["xunit:A", "xunit:Z"], out _);
        Assert.NotNull(next);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad/name")]
    public void UnsafeExecutor_ReturnsNullWithoutBusyKey(string executor)
    {
        using var f = new Fixture();
        Assert.Null(RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, executor, ["K"], out var busy));
        Assert.Null(busy);
    }

    [Fact]
    public void EmptyKeys_ReturnsNoOpWithoutCreatingFolder()
    {
        using var f = new Fixture();
        using var claim = RemoteExecutorSerializedKeyClaim.TryClaim(f.Root, "one", [], out var busy);
        Assert.NotNull(claim);
        Assert.Null(busy);
        Assert.False(Directory.Exists(RemoteExecutorOccupancy.Folder(f.Root, "one")));
    }

    private static Check Lane(string name, params string[] keys) => new()
    {
        Name = "infrastructure tests: " + name, Type = "dotnet-test", Project = RemoteFirstFocusedEvidenceFixture.Project,
        Arguments = ["--filter", "FullyQualifiedName~AlphaTests"], ExclusiveResourceKeys = keys
    };

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "serialized-key-" + Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal FakeRemoteLaneExecutor Executor { get; } = new();
        internal Fixture() => Directory.CreateDirectory(Root);
        internal RemoteLaneCoordinator Coordinator(string attempt, bool twoExecutors = false) => new(
            new(twoExecutors ? [new("one", 60, Slots: 2), new("two", 60, Slots: 2)] : [new("one", 60, Slots: 2)],
                [Lane("A").Name, Lane("B").Name, Lane("C").Name, Lane("D").Name], null)
            {
                ExecutorSerializedResourceKeys = ["K", "K1", "K2"],
                FocusedEvidence = new("shadow", 1, ShadowPreemption: "cancellable")
            },
            new RemoteLaneCandidateIdentity(attempt, "goal", "commit", "tree", "main", "manifest"),
            Root, Path.Combine(Root, "retained", "result"), Executor, Clock, TimeSpan.FromMilliseconds(1), null);
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));
        internal void AssertHeld(string executor, string key)
        {
            using var probe = RemoteExecutorSerializedKeyClaim.TryClaim(Root, executor, [key], out var busy);
            Assert.Null(probe);
            Assert.Equal(key, busy);
        }
        internal FakeRemoteLaneExecutor.Handle CompletedHandle(RemoteLaneRequest request)
        {
            var trx = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".trx");
            File.WriteAllText(trx, "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
            var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
            handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [trx]));
            return handle;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
