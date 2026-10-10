using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each test owns its stores and transport; the timestamp is immutable.
public sealed class StewardDispatcherBriefingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T18:00:00Z");

    [Fact]
    public async Task DailyBriefReceiptHashUsesBundleNextAndSpend()
    {
        var bundle = Bundle();
        const string next = "inspect";
        const decimal spend = 12.34m;
        var store = new InMemoryStewardTriageReceiptStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var dispatcher = new StewardDispatcher(new StewardComposer(), store, transport);
        var composer = new StewardComposer();

        var direct = composer.ComposeDailyBrief(bundle, next, spend, Now);
        var dispatched = await dispatcher.ComposeDailyBriefAsync(
            bundle, next, spend, Now, TestContext.Current.CancellationToken);
        var expectedHash = StewardInputHasher.Hash(new { bundle, next, spend });

        Assert.Equal(expectedHash, direct.Receipt.InputsHash);
        Assert.Equal(expectedHash, dispatched.Receipt.InputsHash);
        Assert.Equal(new StewardDailyBrief(Now, 1, 1, next, spend), dispatched.Value);
        Assert.Equal(StewardOutputKind.DailyBrief, dispatched.Receipt.OutputKind);
        Assert.Equal(Now, dispatched.Receipt.CreatedAt);
        Assert.Equal(["escalation-1"], dispatched.Receipt.InputIds);
        Assert.Equal(expectedHash, composer.ComposeDailyBrief(bundle, next, spend, Now.AddDays(1)).Receipt.InputsHash);
        Assert.Empty(store.Receipts);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task DailyBriefCompositionFailureFaultsReturnedTask()
    {
        var dispatcher = new StewardDispatcher(new StewardComposer(),
            new InMemoryStewardTriageReceiptStore(), new RecordingControlPlaneMessageTransport());
        Task<StewardOutput<StewardDailyBrief>>? task = null;

        var synchronousException = Record.Exception(() =>
        {
            task = dispatcher.ComposeDailyBriefAsync(null!, "inspect", 0m, Now, TestContext.Current.CancellationToken);
        });

        Assert.Null(synchronousException);
        Assert.NotNull(task);
        await Assert.ThrowsAsync<NullReferenceException>(() => task);
    }

    [Fact]
    public async Task HeartbeatReportsStoredReceiptCountsAndRatiosWithoutSideEffects()
    {
        var store = new InMemoryStewardTriageReceiptStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var dispatcher = new StewardDispatcher(new StewardComposer(), store, transport);

        Assert.Equal(new StewardHeartbeat(0, 0, 0, 0, 0),
            await dispatcher.GetHeartbeatAsync(Now, TestContext.Current.CancellationToken));
        await store.AppendAsync(new StewardTriageReceipt("receipt-1", StewardOutputKind.DecisionCard,
            "hash", Now, ["inbox-1", "inbox-2", "inbox-3"],
            [StewardInboxDisposition.CardCreated("inbox-1", "card-1"),
             StewardInboxDisposition.ReceiptLinkedAction("inbox-2", "action-1"),
             StewardInboxDisposition.RaisedRaw("inbox-3", "raw-1")], "summary"), TestContext.Current.CancellationToken);

        var heartbeat = await dispatcher.GetHeartbeatAsync(Now.AddMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(new StewardHeartbeat(3, 2, 1, 2d / 3, 1d / 3), heartbeat);
        Assert.Single(store.Receipts);
        Assert.Empty(transport.Sent);
    }

    private static StewardBriefingBundle Bundle() => new(
        [new StewardEscalationItem("escalation-1", StewardEscalationCategory.Normal, "goal-123",
            "FailedTask", "diagnostic", 3, Now, "title", "diagnostic", "task-1")],
        [new StewardGoalTaskRecord("goal-123", "task-1", "Landed", 0, 0, 0),
         new StewardGoalTaskRecord("goal-456", "task-2", "Active", 0, 0, 0)],
        new StewardNumericReceiptSummary(0, 0, [], 0, 0, 0), [],
        new StewardPolicySnapshot("policy", [], Now),
        new StewardInterruptBudgetLedger(1, 0, 1, Now), [], []);
}
