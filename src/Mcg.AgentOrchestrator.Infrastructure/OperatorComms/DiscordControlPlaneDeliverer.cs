using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IControlPlaneMessageTransport
{
    Task<ulong> SendAsync(
        ControlPlaneDeliveryChannel channel,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default);

    Task EditAsync(
        ControlPlaneDeliveryChannel channel,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default);
}

public sealed class RecordingControlPlaneMessageTransport : IControlPlaneMessageTransport
{
    private ulong _nextMessageId = 1000;

    public List<(ControlPlaneDeliveryChannel Channel, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> Sent { get; } = [];

    public List<(ControlPlaneDeliveryChannel Channel, ulong MessageId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> Edited { get; } = [];

    public Task<ulong> SendAsync(
        ControlPlaneDeliveryChannel channel,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        Sent.Add((channel, content, buttons));
        return Task.FromResult(++_nextMessageId);
    }

    public Task EditAsync(
        ControlPlaneDeliveryChannel channel,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        Edited.Add((channel, messageId, content, buttons));
        return Task.CompletedTask;
    }
}

public sealed class DiscordControlPlaneDeliverer
{
    public const string PendingRollupKey = "control-plane:pending-rollup";
    public const string BoardHeartbeatKey = "control-plane:board-heartbeat";
    public const string DigestRollupKey = "control-plane:digest-rollup";
    public const string DailyBacklogDigestKey = "control-plane:daily-backlog-digest";

    private readonly IControlPlaneDeliveryStore _store;
    private readonly IControlPlaneMessageTransport _transport;
    private readonly ControlPlaneDeliveryPolicy _policy;

    public DiscordControlPlaneDeliverer(
        IControlPlaneDeliveryStore store,
        IControlPlaneMessageTransport transport,
        ControlPlaneDeliveryPolicy? policy = null)
    {
        _store = store;
        _transport = transport;
        _policy = policy ?? new ControlPlaneDeliveryPolicy();
    }

    public async Task<ControlPlaneDeliveryBatchResult> DeliverAsync(
        IEnumerable<ControlPlaneDecisionCard> cards,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var operations = new List<ControlPlaneDeliveryOperation>();
        var candidates = (await CollapseStormsAsync(cards, now, cancellationToken))
            .OrderByDescending(card => card.IsBoardIntegrity)
            .ThenBy(card => card.RaisedAt)
            .ToList();

        if (_policy.IsMuted(now))
        {
            operations.AddRange(candidates.Select(card => Suppressed(card, "muted")));
            return new ControlPlaneDeliveryBatchResult(operations);
        }

        var decisionPushesToday = await _store.CountPushesAsync(
            ControlPlaneDeliveryChannel.Decisions,
            now.AddHours(-24),
            cancellationToken);
        var remainingBudget = Math.Max(0, _policy.DailyDecisionBudget - decisionPushesToday);
        var budgetedPushCount = 0;
        foreach (var card in candidates)
        {
            if (!card.IsBoardIntegrity &&
                !card.IsResolved &&
                !_policy.IsQuietHours(now) &&
                await WouldPushDecisionCardAsync(card, now, cancellationToken))
            {
                budgetedPushCount++;
            }
        }

        var reserveRollupSlot = budgetedPushCount > remainingBudget && remainingBudget > 0;
        var remainingCardBudget = reserveRollupSlot ? remainingBudget - 1 : remainingBudget;
        var overBudget = new List<ControlPlaneDecisionCard>();

        foreach (var card in candidates)
        {
            if (card.IsResolved)
            {
                var resolvedOperation = await UpsertCardAsync(card, now, cancellationToken);
                operations.Add(resolvedOperation);
                if (ConsumesDecisionBudget(resolvedOperation, card))
                    remainingCardBudget = Math.Max(0, remainingCardBudget - 1);
                continue;
            }

            if (_policy.IsQuietHours(now) && !card.IsBoardIntegrity)
            {
                operations.Add(Suppressed(card, "quiet-hours"));
                continue;
            }

            var wouldPush = card.IsBoardIntegrity ||
                await WouldPushDecisionCardAsync(card, now, cancellationToken);
            if (!card.IsBoardIntegrity && wouldPush && remainingCardBudget <= 0)
            {
                overBudget.Add(card);
                continue;
            }

            var operation = await UpsertCardAsync(card, now, cancellationToken);
            operations.Add(operation);
            if (ConsumesDecisionBudget(operation, card))
                remainingCardBudget--;
        }

        if (overBudget.Count > 0)
        {
            operations.Add(reserveRollupSlot
                ? await UpsertRollupAsync(overBudget, now, cancellationToken)
                : new ControlPlaneDeliveryOperation(
                    ControlPlaneDeliveryOperationKind.Suppressed,
                    ControlPlaneDeliveryChannel.Decisions,
                    PendingRollupKey,
                    null,
                    "decision-budget",
                    string.Empty));
        }

        return new ControlPlaneDeliveryBatchResult(operations);
    }

    public async Task<ControlPlaneDeliveryBatchResult> DeliverBoardHeartbeatAsync(
        ControlPlaneBoardSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var mark = await _store.TryGetAsync(BoardHeartbeatKey, cancellationToken);
        if (mark is not null && now - mark.LastDeliveredAt < _policy.EffectiveBoardHeartbeatCadence)
        {
            return new ControlPlaneDeliveryBatchResult(
                [new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Suppressed, ControlPlaneDeliveryChannel.Board, BoardHeartbeatKey, mark.MessageId, "heartbeat-cadence", string.Empty)]);
        }

        var content = RenderBoard(snapshot);
        var hash = Hash(content);
        if (mark is null)
        {
            var id = await _transport.SendAsync(ControlPlaneDeliveryChannel.Board, content, [], cancellationToken);
            await _store.UpsertAsync(new ControlPlaneDeliveryMark(BoardHeartbeatKey, ControlPlaneDeliveryChannel.Board, id, now, now, null, hash, false), cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Board, BoardHeartbeatKey, now, ControlPlaneDeliveryOperationKind.Send, cancellationToken);
            return new ControlPlaneDeliveryBatchResult(
                [new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Send, ControlPlaneDeliveryChannel.Board, BoardHeartbeatKey, id, "heartbeat", content)]);
        }

        await _transport.EditAsync(ControlPlaneDeliveryChannel.Board, mark.MessageId, content, [], cancellationToken);
        await _store.UpsertAsync(mark with { LastDeliveredAt = now, ContentHash = hash }, cancellationToken);
        await RecordPushAsync(ControlPlaneDeliveryChannel.Board, BoardHeartbeatKey, now, ControlPlaneDeliveryOperationKind.Edit, cancellationToken);
        return new ControlPlaneDeliveryBatchResult(
            [new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Edit, ControlPlaneDeliveryChannel.Board, BoardHeartbeatKey, mark.MessageId, "heartbeat", content)]);
    }

    public async Task<ControlPlaneDeliveryOperation> DeliverDigestAsync(
        IReadOnlyList<ControlPlaneDecisionCard> cards,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var mark = await _store.TryGetAsync(DigestRollupKey, cancellationToken);
        if (mark is not null && now - mark.LastDeliveredAt < _policy.EffectiveDigestCadence)
        {
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Suppressed, ControlPlaneDeliveryChannel.Digest, DigestRollupKey, mark.MessageId, "digest-cadence", string.Empty);
        }

        var content = RenderDigest(cards, now);
        var hash = Hash(content);
        if (mark is null)
        {
            var id = await _transport.SendAsync(ControlPlaneDeliveryChannel.Digest, content, [], cancellationToken);
            await _store.UpsertAsync(new ControlPlaneDeliveryMark(DigestRollupKey, ControlPlaneDeliveryChannel.Digest, id, now, now, null, hash, false), cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Digest, DigestRollupKey, now, ControlPlaneDeliveryOperationKind.Send, cancellationToken);
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Send, ControlPlaneDeliveryChannel.Digest, DigestRollupKey, id, "digest", content);
        }

        await _transport.EditAsync(ControlPlaneDeliveryChannel.Digest, mark.MessageId, content, [], cancellationToken);
        await _store.UpsertAsync(mark with { LastDeliveredAt = now, ContentHash = hash }, cancellationToken);
        await RecordPushAsync(ControlPlaneDeliveryChannel.Digest, DigestRollupKey, now, ControlPlaneDeliveryOperationKind.Edit, cancellationToken);
        return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Edit, ControlPlaneDeliveryChannel.Digest, DigestRollupKey, mark.MessageId, "digest", content);
    }

    public async Task<ControlPlaneDeliveryOperation> DeliverDailyBacklogDigestAsync(
        IReadOnlyList<ControlPlaneBacklogDigestItem> backlogItems,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var mark = await _store.TryGetAsync(DailyBacklogDigestKey, cancellationToken);
        if (mark is not null && now - mark.LastDeliveredAt < _policy.EffectiveBacklogDigestCadence)
        {
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Suppressed, ControlPlaneDeliveryChannel.Digest, DailyBacklogDigestKey, mark.MessageId, "daily-backlog-digest-cadence", string.Empty);
        }

        var content = RenderDailyBacklogDigest(backlogItems, now);
        var hash = Hash(content);
        if (mark is null)
        {
            var id = await _transport.SendAsync(ControlPlaneDeliveryChannel.Digest, content, [], cancellationToken);
            await _store.UpsertAsync(new ControlPlaneDeliveryMark(DailyBacklogDigestKey, ControlPlaneDeliveryChannel.Digest, id, now, now, null, hash, false), cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Digest, DailyBacklogDigestKey, now, ControlPlaneDeliveryOperationKind.Send, cancellationToken);
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Send, ControlPlaneDeliveryChannel.Digest, DailyBacklogDigestKey, id, "daily-backlog-digest", content);
        }

        await _transport.EditAsync(ControlPlaneDeliveryChannel.Digest, mark.MessageId, content, [], cancellationToken);
        await _store.UpsertAsync(mark with { LastDeliveredAt = now, ContentHash = hash }, cancellationToken);
        await RecordPushAsync(ControlPlaneDeliveryChannel.Digest, DailyBacklogDigestKey, now, ControlPlaneDeliveryOperationKind.Edit, cancellationToken);
        return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Edit, ControlPlaneDeliveryChannel.Digest, DailyBacklogDigestKey, mark.MessageId, "daily-backlog-digest", content);
    }

    private async Task<ControlPlaneDeliveryOperation> UpsertCardAsync(
        ControlPlaneDecisionCard card,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var content = RenderCard(card);
        var buttons = card.IsResolved ? [] : BuildButtons(card);
        var hash = Hash(content);
        var mark = await _store.TryGetAsync(card.DedupKey, cancellationToken);
        if (mark is null)
        {
            if (card.IsResolved)
                return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Suppressed, ControlPlaneDeliveryChannel.Decisions, card.DedupKey, null, "resolved-unseen", content);

            var messageId = await _transport.SendAsync(ControlPlaneDeliveryChannel.Decisions, content, buttons, cancellationToken);
            await _store.UpsertAsync(
                new ControlPlaneDeliveryMark(card.DedupKey, ControlPlaneDeliveryChannel.Decisions, messageId, now, now, null, hash, card.IsResolved),
                cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Decisions, card.DedupKey, now, ControlPlaneDeliveryOperationKind.Send, cancellationToken);
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Send, ControlPlaneDeliveryChannel.Decisions, card.DedupKey, messageId, "new-card", content);
        }

        if (card.IsResolved && !mark.Resolved)
        {
            await _transport.EditAsync(ControlPlaneDeliveryChannel.Decisions, mark.MessageId, content, [], cancellationToken);
            await _store.UpsertAsync(mark with { LastDeliveredAt = now, ContentHash = hash, Resolved = true }, cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Decisions, card.DedupKey, now, ControlPlaneDeliveryOperationKind.Edit, cancellationToken);
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Edit, ControlPlaneDeliveryChannel.Decisions, card.DedupKey, mark.MessageId, "resolved", content);
        }

        var reminderBaseline = mark.LastReminderAt ?? mark.FirstDeliveredAt;
        var reminderDue = !card.IsResolved &&
            now - reminderBaseline >= _policy.EffectiveReminderCadence;
        if (hash != mark.ContentHash || reminderDue)
        {
            await _transport.EditAsync(ControlPlaneDeliveryChannel.Decisions, mark.MessageId, content, buttons, cancellationToken);
            await _store.UpsertAsync(mark with
            {
                LastDeliveredAt = now,
                LastReminderAt = reminderDue ? now : mark.LastReminderAt,
                ContentHash = hash,
                Resolved = card.IsResolved
            }, cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Decisions, card.DedupKey, now, ControlPlaneDeliveryOperationKind.Edit, cancellationToken);
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Edit, ControlPlaneDeliveryChannel.Decisions, card.DedupKey, mark.MessageId, reminderDue ? "reminder" : "content-change", content);
        }

        return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Suppressed, ControlPlaneDeliveryChannel.Decisions, card.DedupKey, mark.MessageId, "dedup", content);
    }

    private async Task<bool> WouldPushDecisionCardAsync(
        ControlPlaneDecisionCard card,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var mark = await _store.TryGetAsync(card.DedupKey, cancellationToken);
        if (mark is null)
            return !card.IsResolved;

        if (card.IsResolved)
            return !mark.Resolved;

        var hash = Hash(RenderCard(card));
        var reminderBaseline = mark.LastReminderAt ?? mark.FirstDeliveredAt;
        return hash != mark.ContentHash ||
            now - reminderBaseline >= _policy.EffectiveReminderCadence;
    }

    private async Task<ControlPlaneDeliveryOperation> UpsertRollupAsync(
        IReadOnlyList<ControlPlaneDecisionCard> cards,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var content = $"Pending control-plane rollup: {cards.Count} over-budget item(s).\n" +
            string.Join('\n', cards.Take(10).Select(card => $"- [{card.Kind}] {ShortGoal(card.GoalId)} {Truncate(card.Title, 120)}"));
        var hash = Hash(content);
        var mark = await _store.TryGetAsync(PendingRollupKey, cancellationToken);
        if (mark is null)
        {
            var messageId = await _transport.SendAsync(ControlPlaneDeliveryChannel.Decisions, content, [], cancellationToken);
            await _store.UpsertAsync(new ControlPlaneDeliveryMark(PendingRollupKey, ControlPlaneDeliveryChannel.Decisions, messageId, now, now, null, hash, false), cancellationToken);
            await RecordPushAsync(ControlPlaneDeliveryChannel.Decisions, PendingRollupKey, now, ControlPlaneDeliveryOperationKind.Send, cancellationToken);
            return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Send, ControlPlaneDeliveryChannel.Decisions, PendingRollupKey, messageId, "over-budget-rollup", content);
        }

        await _transport.EditAsync(ControlPlaneDeliveryChannel.Decisions, mark.MessageId, content, [], cancellationToken);
        await _store.UpsertAsync(mark with { LastDeliveredAt = now, ContentHash = hash }, cancellationToken);
        await RecordPushAsync(ControlPlaneDeliveryChannel.Decisions, PendingRollupKey, now, ControlPlaneDeliveryOperationKind.Edit, cancellationToken);
        return new ControlPlaneDeliveryOperation(ControlPlaneDeliveryOperationKind.Edit, ControlPlaneDeliveryChannel.Decisions, PendingRollupKey, mark.MessageId, "over-budget-rollup", content);
    }

    private async Task<IReadOnlyList<ControlPlaneDecisionCard>> CollapseStormsAsync(
        IEnumerable<ControlPlaneDecisionCard> cards,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var source = cards
            .Where(card => card.Source is ControlPlaneCardSource.CollaborationDecision or
                ControlPlaneCardSource.OperatorInboxEscalation or
                ControlPlaneCardSource.StewardTriage)
            .ToList();
        var existingSystemicStorms = await _store.ListSystemicStormsAsync(cancellationToken);
        var existingSystemicKinds = existingSystemicStorms
            .Select(state => state.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collapsedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ControlPlaneDecisionCard>();

        foreach (var group in source.GroupBy(card => card.Kind, StringComparer.OrdinalIgnoreCase))
        {
            sourceKinds.Add(group.Key);
            var grouped = group.ToList();
            var unresolved = grouped.Where(card => !card.IsResolved).ToList();
            var inWindow = unresolved.Where(card => now - card.RaisedAt <= _policy.EffectiveStormWindow).ToList();
            var existingSystemic = existingSystemicKinds.Contains(group.Key);
            var groupCards = existingSystemic ? unresolved : inWindow;
            if (!existingSystemic && groupCards.Count < _policy.SystemicMergeThreshold)
            {
                foreach (var card in inWindow.Where(card => !card.IsBoardIntegrity))
                    collapsedKeys.Add(card.DedupKey);

                continue;
            }

            foreach (var card in grouped)
                collapsedKeys.Add(card.DedupKey);

            if (existingSystemic && groupCards.Count == 0)
            {
                result.Add(new ControlPlaneDecisionCard(
                    ControlPlaneCardSource.OperatorInboxEscalation,
                    "system",
                    $"Systemic{group.Key}",
                    "storm-window",
                    ControlPlaneDecisionCard.ComputeFingerprint(group.Key),
                    $"{group.Key} escalation storm resolved",
                    "All underlying escalations for this systemic card are resolved.",
                    grouped.Min(card => card.RaisedAt),
                    IsResolved: true,
                    IsBoardIntegrity: grouped.Any(card => card.IsBoardIntegrity)));
                continue;
            }

            result.Add(new ControlPlaneDecisionCard(
                ControlPlaneCardSource.OperatorInboxEscalation,
                "system",
                $"Systemic{group.Key}",
                "storm-window",
                ControlPlaneDecisionCard.ComputeFingerprint(group.Key),
                $"{groupCards.Count} {group.Key} escalations need one systemic decision",
                string.Join('\n', groupCards.OrderBy(card => card.RaisedAt).Take(12).Select(card => $"- {ShortGoal(card.GoalId)}: {Truncate(card.Title, 120)}")),
                groupCards.Min(card => card.RaisedAt),
                IsBoardIntegrity: groupCards.Any(card => card.IsBoardIntegrity)));
        }

        foreach (var state in existingSystemicStorms.Where(state => !sourceKinds.Contains(state.Kind)))
            result.Add(ResolvedSystemicStormCard(state, now));

        result.AddRange(source.Where(card => !collapsedKeys.Contains(card.DedupKey)));
        return result;
    }

    private static ControlPlaneDecisionCard ResolvedSystemicStormCard(
        SystemicStormDeliveryState state,
        DateTimeOffset raisedAt)
    {
        var parts = state.DedupKey.Split(':');
        var fingerprint = parts.Length == 4
            ? parts[3]
            : ControlPlaneDecisionCard.ComputeFingerprint(state.Kind);
        return new ControlPlaneDecisionCard(
            ControlPlaneCardSource.OperatorInboxEscalation,
            "system",
            $"Systemic{state.Kind}",
            "storm-window",
            fingerprint,
            $"{state.Kind} escalation storm resolved",
            "All underlying escalations for this systemic card are resolved.",
            raisedAt,
            IsResolved: true);
    }

    private static bool ConsumesDecisionBudget(
        ControlPlaneDeliveryOperation operation,
        ControlPlaneDecisionCard card) =>
        !card.IsBoardIntegrity &&
        operation.Channel == ControlPlaneDeliveryChannel.Decisions &&
        operation.Kind is ControlPlaneDeliveryOperationKind.Send or ControlPlaneDeliveryOperationKind.Edit;

    private Task RecordPushAsync(
        ControlPlaneDeliveryChannel channel,
        string dedupKey,
        DateTimeOffset pushedAt,
        ControlPlaneDeliveryOperationKind kind,
        CancellationToken cancellationToken) =>
        _store.RecordPushAsync(channel, dedupKey, pushedAt, kind, cancellationToken);

    private static ControlPlaneDeliveryOperation Suppressed(ControlPlaneDecisionCard card, string reason) =>
        new(ControlPlaneDeliveryOperationKind.Suppressed, ControlPlaneDeliveryChannel.Decisions, card.DedupKey, null, reason, string.Empty);

    private static IReadOnlyList<DiscordButtonDefinition> BuildButtons(ControlPlaneDecisionCard card) =>
        card.ActionList
            .Take(25)
            .Select(action => new DiscordButtonDefinition(action.Label, action.CustomId, action.Style))
            .ToList();

    private static string RenderCard(ControlPlaneDecisionCard card)
    {
        var header = $"[{card.Kind}] {card.Title}";
        if (card.IsResolved)
            header = $"~~{header}~~";
        return $"{header}\nGoal: {ShortGoal(card.GoalId)}\n{Truncate(card.Body, 1600)}";
    }

    private static string RenderBoard(ControlPlaneBoardSnapshot snapshot)
    {
        var state = snapshot.LoopAlive ? "alive" : "stale";
        return $"Board heartbeat: {state}\n" +
            $"tickAge={FormatAge(snapshot.TickAge)} active={snapshot.ActiveLanes} held={snapshot.HeldLanes} escalated={snapshot.EscalatedLanes}\n" +
            $"observedAt={snapshot.ObservedAt:O}";
    }

    private static string RenderDigest(IReadOnlyList<ControlPlaneDecisionCard> cards, DateTimeOffset now)
    {
        var open = cards.Count(card => !card.IsResolved);
        var resolved = cards.Count(card => card.IsResolved);
        return $"Control-plane digest {now:O}\nopen={open} resolved={resolved}\n" +
            string.Join('\n', cards.Take(20).Select(card => $"- [{card.Kind}] {ShortGoal(card.GoalId)} {Truncate(card.Title, 100)}"));
    }

    private static string RenderDailyBacklogDigest(IReadOnlyList<ControlPlaneBacklogDigestItem> backlogItems, DateTimeOffset now)
    {
        var open = backlogItems.Count(item => item.Status == BacklogItemStatus.Open);
        var done = backlogItems.Count(item => item.Status == BacklogItemStatus.Done);
        var lines = backlogItems
            .OrderByDescending(item => item.UpdatedAt)
            .Take(20)
            .Select(item => $"- [{item.Status}] {ShortGoal(item.SourceGoalId ?? string.Empty)} {Truncate(item.Title, 100)}");
        return $"Daily backlog digest {now:O}\nopen={open} done={done}\n" +
            string.Join('\n', lines);
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m" : $"{Math.Max(0, (int)age.TotalSeconds)}s";

    private static string ShortGoal(string goalId) =>
        string.IsNullOrWhiteSpace(goalId) ? "none" : goalId[..Math.Min(8, goalId.Length)];

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
