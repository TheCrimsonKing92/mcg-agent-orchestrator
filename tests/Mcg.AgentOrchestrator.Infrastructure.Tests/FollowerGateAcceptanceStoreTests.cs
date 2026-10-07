using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fact owns and removes its database and evidence files.
public sealed class FollowerGateAcceptanceStoreTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void FirstReceiptWinsEvenWhenLaterVerdictDiffers()
    {
        WithStore((store, root) =>
        {
            var first = Receipt("first", "follower") with
            { Outcome = FollowerGateRunOutcome.Passed, GateExitCode = 0, GateTestResultPaths = [WritePassingTrx(root, "pass.trx")] };
            var saved = store.SaveGateReceipt(first);
            var later = store.SaveGateReceipt(first with { ReceiptId = "later", Outcome = FollowerGateRunOutcome.Failed, GateExitCode = 1 });
            Assert.Equal(saved.ReceiptId, later.ReceiptId);
            Assert.Equal(FollowerGateRunOutcome.Passed, later.Outcome);
            Assert.Equal(first.Binding, later.Binding);
            Assert.Single(store.ReadReceiptsForFollower(new("follower")));
        });
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void InvalidPassingEvidenceIsRejectedWithoutStoring(int exitCode, bool hasTrx)
    {
        WithStore((store, root) =>
        {
            var receipt = Receipt("invalid", "follower") with { Outcome = FollowerGateRunOutcome.Passed,
                GateExitCode = exitCode, GateTestResultPaths = hasTrx ? [WritePassingTrx(root, "pass.trx")] : [] };
            Assert.Throws<ArgumentException>(() => store.SaveGateReceipt(receipt));
            Assert.Null(store.TryReadReceipt(receipt.IdentityValue));
            Assert.Empty(store.ReadReceiptsForFollower(new("follower")));
        });
    }

    [Fact]
    public void InvalidatedReasonRoundTripsAndFollowerHistoryIsNewestFirst()
    {
        WithStore((store, _) =>
        {
            var older = Receipt("old", "follower");
            var newer = Receipt("new", "follower") with
            { CompletedAt = DateTimeOffset.UnixEpoch.AddDays(1), Outcome = FollowerGateRunOutcome.Invalidated,
                InvalidReason = FollowerGateInvalidReason.LeaderTreeDiffers };
            store.SaveGateReceipt(newer);
            store.SaveGateReceipt(Receipt("other", "other-follower") with { CompletedAt = DateTimeOffset.UnixEpoch.AddDays(2) });
            store.SaveGateReceipt(older);
            var read = Assert.IsType<FollowerGateRunReceipt>(store.TryReadReceipt(newer.IdentityValue));
            Assert.Equal(FollowerGateRunOutcome.Invalidated, read.Outcome);
            Assert.Equal(FollowerGateInvalidReason.LeaderTreeDiffers, read.InvalidReason);
            Assert.Equal(newer.Binding, read.Binding);
            Assert.Equal(new[] { "new", "old" }, store.ReadReceiptsForFollower(new("follower")).Select(receipt => receipt.ReceiptId));
        });
    }

    private static FollowerGateRunReceipt Receipt(string id, string follower)
    {
        var binding = new FollowerGateReceipt(new("leader"), new string('a', 40), new string('b', 40),
            new string('c', 40), new(follower), new string('d', 40), new string('e', 40), id);
        return new(id, FollowerGateIdentity.Create(binding), binding, FollowerGateRunOutcome.Failed,
            DateTimeOffset.UnixEpoch, [], 1, [], null);
    }

    private static void WithStore(Action<FollowerGateAcceptanceStore, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"follower-store-{Guid.NewGuid():N}");
        try { action(new(Path.Combine(root, "receipts.db")), root); }
        finally { DeleteDirectory(root); }
    }
}
