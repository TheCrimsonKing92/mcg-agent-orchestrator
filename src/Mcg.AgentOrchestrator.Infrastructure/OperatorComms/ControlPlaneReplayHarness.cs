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
        return await ReplayCoreAsync(cards, [], false, from, to, cancellationToken);
    }

    public async Task<ControlPlaneReplayReport> ReplayAsync(
        IEnumerable<ControlPlaneDecisionCard> cards,
        IEnumerable<ControlPlaneBacklogDigestItem> backlogItems,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        return await ReplayCoreAsync(cards, backlogItems, true, from, to, cancellationToken);
    }

    private async Task<ControlPlaneReplayReport> ReplayCoreAsync(
        IEnumerable<ControlPlaneDecisionCard> cards,
        IEnumerable<ControlPlaneBacklogDigestItem> backlogItems,
        bool includeDailyBacklogDigest,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var ordered = cards
            .Where(card => card.RaisedAt >= from && card.RaisedAt <= to)
            .OrderBy(card => card.RaisedAt)
            .ToList();
        var backlog = backlogItems
            .Where(item => item.UpdatedAt <= to)
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
        var store = new InMemoryControlPlaneDeliveryStore();
        var transport = new RecordingControlPlaneMessageTransport();
        var deliverer = new DiscordControlPlaneDeliverer(store, transport, _policy);
        var operations = new List<ControlPlaneDeliveryOperation>();
        var firstStorms = FindFirstStorms(ordered);
        var initialStormKeys = firstStorms.Values
            .SelectMany(storm => storm.InitialMemberKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchesByRaisedAt = ordered
            .GroupBy(card => card.RaisedAt)
            .ToDictionary(group => group.Key, group => group.ToList());
        var replayTimes = ordered
            .Select(card => card.RaisedAt)
            .Concat(ordered
                .Where(card => !initialStormKeys.Contains(card.DedupKey))
                .Select(card => card.RaisedAt + _policy.EffectiveStormWindow + TimeSpan.FromTicks(1)))
            .Where(time => time <= to)
            .Distinct()
            .OrderBy(time => time)
            .ToList();

        var visible = new List<ControlPlaneDecisionCard>();
        foreach (var replayAt in replayTimes)
        {
            var cardsToReveal = batchesByRaisedAt.TryGetValue(replayAt, out var batch)
                ? batch
                    .Where(card => !initialStormKeys.Contains(card.DedupKey) ||
                        firstStorms.TryGetValue(card.Kind, out var storm) &&
                        card.RaisedAt == storm.TriggerAt)
                    .ToList()
                : [];
            foreach (var storm in firstStorms.Values.Where(storm => storm.TriggerAt == replayAt))
            {
                cardsToReveal.AddRange(ordered.Where(card =>
                    storm.InitialMemberKeys.Contains(card.DedupKey) &&
                    card.RaisedAt < storm.TriggerAt));
            }

            if (cardsToReveal.Count == 0)
            {
                if (visible.Count > 0)
                    operations.AddRange((await deliverer.DeliverAsync(visible, replayAt, cancellationToken)).Operations);

                continue;
            }

            visible.AddRange(cardsToReveal.OrderBy(card => card.RaisedAt));
            operations.AddRange((await deliverer.DeliverAsync(visible, replayAt, cancellationToken)).Operations);
        }

        operations.AddRange((await deliverer.DeliverBoardHeartbeatAsync(new ControlPlaneBoardSnapshot(
            LoopAlive: true,
            TickAge: TimeSpan.Zero,
            ActiveLanes: 0,
            HeldLanes: 0,
            EscalatedLanes: ordered.Count(card => !card.IsResolved),
            ObservedAt: to), to, cancellationToken)).Operations);
        operations.Add(await deliverer.DeliverDigestAsync(ordered, to, cancellationToken));
        if (includeDailyBacklogDigest)
            operations.Add(await deliverer.DeliverDailyBacklogDigestAsync(backlog, to, cancellationToken));

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

    private Dictionary<string, ReplayStorm> FindFirstStorms(IReadOnlyList<ControlPlaneDecisionCard> cards)
    {
        var storms = new Dictionary<string, ReplayStorm>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in cards
            .Where(card => !card.IsResolved)
            .GroupBy(card => card.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var orderedGroup = group.OrderBy(card => card.RaisedAt).ToList();
            foreach (var candidate in orderedGroup)
            {
                var inWindow = orderedGroup
                    .Where(card => card.RaisedAt <= candidate.RaisedAt &&
                        candidate.RaisedAt - card.RaisedAt <= _policy.EffectiveStormWindow)
                    .ToList();
                if (inWindow.Count < _policy.SystemicMergeThreshold)
                    continue;

                storms[group.Key] = new ReplayStorm(
                    candidate.RaisedAt,
                    inWindow.Select(card => card.DedupKey).ToHashSet(StringComparer.OrdinalIgnoreCase));
                break;
            }
        }

        return storms;
    }

    private sealed record ReplayStorm(DateTimeOffset TriggerAt, HashSet<string> InitialMemberKeys);
}
