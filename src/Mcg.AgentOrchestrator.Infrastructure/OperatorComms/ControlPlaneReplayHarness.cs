namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class ControlPlaneReplayHarness
{
    private readonly ControlPlaneDeliveryPolicy _policy;

    public ControlPlaneReplayHarness(ControlPlaneDeliveryPolicy? policy = null)
    {
        _policy = policy ?? new ControlPlaneDeliveryPolicy();
    }

    public async Task<ControlPlaneReplayReport> ReplayAsync(
        IEnumerable<ControlPlaneDecisionCard> cards,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var ordered = cards
            .Where(card => card.RaisedAt >= from && card.RaisedAt <= to)
            .OrderBy(card => card.RaisedAt)
            .ToList();
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, _policy);
        var operations = new List<ControlPlaneDeliveryOperation>();

        var visible = new List<ControlPlaneDecisionCard>();
        foreach (var batch in ordered.GroupBy(card => card.RaisedAt))
        {
            visible.AddRange(batch);
            operations.AddRange((await deliverer.DeliverAsync(visible, batch.Key, cancellationToken)).Operations);
        }

        operations.AddRange((await deliverer.DeliverBoardHeartbeatAsync(new ControlPlaneBoardSnapshot(
            LoopAlive: true,
            TickAge: TimeSpan.Zero,
            ActiveLanes: 0,
            HeldLanes: 0,
            EscalatedLanes: ordered.Count(card => !card.IsResolved),
            ObservedAt: to), to, cancellationToken)).Operations);
        operations.Add(await deliverer.DeliverDigestAsync(ordered, to, cancellationToken));

        return new ControlPlaneReplayReport(
            CountPushOperations(operations, ControlPlaneDeliveryChannel.Decisions),
            CountPushOperations(operations, ControlPlaneDeliveryChannel.Board),
            CountPushOperations(operations, ControlPlaneDeliveryChannel.Digest),
            operations.Count(operation => operation.Kind == ControlPlaneDeliveryOperationKind.Suppressed),
            operations);
    }

    public async Task<ControlPlaneReplayReport> ReplayCollaborationStoreAsync(
        ICollaborationItemStore store,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var items = await store.ListAsync(null, cancellationToken);
        return await ReplayAsync(
            items.Select(ControlPlaneDecisionCard.FromCollaborationItem),
            from,
            to,
            cancellationToken);
    }

    private static int CountPushOperations(
        IReadOnlyList<ControlPlaneDeliveryOperation> operations,
        ControlPlaneDeliveryChannel channel) =>
        operations.Count(operation =>
            operation.Channel == channel &&
            operation.Kind is ControlPlaneDeliveryOperationKind.Send or ControlPlaneDeliveryOperationKind.Edit);
}
