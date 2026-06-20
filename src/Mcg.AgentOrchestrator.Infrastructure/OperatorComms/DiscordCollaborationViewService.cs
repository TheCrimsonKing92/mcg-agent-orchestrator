using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Surfaces the operator attention queue in Discord as ONE persistent message PER GOAL that needs
// attention — a per-goal priority list, edited in place as items are raised/resolved. A goal with no
// open attention items gets NO message (silence is the default). This replaces the old per-item delivery
// (a new message per CollaborationItem) which spammed a message for every escalation.
public sealed class DiscordCollaborationViewService
{
    private const string RefsFileName = "discord-collaboration-refs.json";
    private readonly ICollaborationItemStore _store;
    private readonly IDiscordForumApi _api;
    private readonly ulong _forumChannelId;
    private readonly string _stateDirectory;
    private readonly IReadOnlyList<string> _allowedUserIds;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public DiscordCollaborationViewService(
        ICollaborationItemStore store,
        IDiscordForumApi api,
        ulong forumChannelId,
        string stateDirectory,
        IReadOnlyList<string> allowedUserIds)
    {
        _store = store;
        _api = api;
        _forumChannelId = forumChannelId;
        _stateDirectory = stateDirectory;
        _allowedUserIds = allowedUserIds;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var queue = await _store.GetAttentionQueueAsync(cancellationToken);
        var refs = LoadRefs();

        // Group the (already prioritized) open attention items by goal.
        var byGoal = queue
            .Where(item => !string.IsNullOrWhiteSpace(item.CorrelationKey))
            .GroupBy(GoalKey)
            .ToDictionary(group => group.Key, OrderItems);

        // One aggregated message per goal that currently needs attention.
        foreach (var (goalKey, items) in byGoal)
        {
            await UpsertGoalMessageAsync(refs, goalKey, items, cancellationToken);
            foreach (var item in items)
                await _store.TryMarkDeliveredAsync(item.CorrelationKey!, cancellationToken);
        }

        // A goal that previously had a message but is now clear gets its message retired (all resolved),
        // never left dangling with stale buttons.
        foreach (var goalKey in refs.GoalMessages.Keys.ToArray())
        {
            if (!byGoal.ContainsKey(goalKey))
                await RetireGoalMessageAsync(refs, goalKey, cancellationToken);
        }

        SaveRefs(refs);
    }

    public async Task<DiscordInteractionResult> ApplyInteractionAsync(
        string customId,
        string userId,
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        var result = DiscordInteractionHandler.Process(customId, userId, interactionId, _allowedUserIds);
        if (result.ErrorMessage is not null || result.Decision is null)
            return result;

        var correlationKey = result.Decision.InboxItemId;
        var resolution = result.Decision.Command;
        await _store.TryResolveAsync(correlationKey, resolution, cancellationToken);

        var resolvedItem = (await _store.ListAsync(null, cancellationToken))
            .FirstOrDefault(candidate => candidate.CorrelationKey == correlationKey);
        if (resolvedItem is null)
            return new DiscordInteractionResult(null, false, null, $"No collaboration item for '{correlationKey}'.");

        // Re-render the resolved item's goal message in place: drop the resolved item; if it was the
        // goal's last open item, the message becomes "all resolved" with no buttons.
        var goalKey = GoalKey(resolvedItem);
        var refs = LoadRefs();
        if (refs.GoalMessages.TryGetValue(goalKey, out var messageRef))
        {
            var remaining = OrderItems(
                (await _store.GetAttentionQueueAsync(cancellationToken))
                    .Where(item => !string.IsNullOrWhiteSpace(item.CorrelationKey) && GoalKey(item) == goalKey));

            if (remaining.Count == 0)
            {
                await _api.EditMessageAsync(messageRef.ThreadId, messageRef.MessageId,
                    BuildAllResolvedContent(goalKey, userId), [], cancellationToken);
            }
            else
            {
                await _api.EditMessageAsync(messageRef.ThreadId, messageRef.MessageId,
                    BuildGoalContent(goalKey, remaining, $"✅ resolved one by {userId}"),
                    BuildGoalButtons(remaining), cancellationToken);
            }
        }

        return result;
    }

    private async Task UpsertGoalMessageAsync(
        DiscordCollaborationRefs refs, string goalKey, IReadOnlyList<CollaborationItem> items, CancellationToken cancellationToken)
    {
        var content = BuildGoalContent(goalKey, items, footer: null);
        var buttons = BuildGoalButtons(items);

        if (refs.GoalMessages.TryGetValue(goalKey, out var messageRef))
        {
            await _api.EditMessageAsync(messageRef.ThreadId, messageRef.MessageId, content, buttons, cancellationToken);
            return;
        }

        var threadId = await _api.CreateThreadAsync(_forumChannelId, BuildGoalThreadTitle(goalKey), content, cancellationToken);
        var messageId = await _api.SendMessageAsync(threadId, content, buttons, cancellationToken);
        refs.GoalMessages[goalKey] = new GoalMessageRef(threadId, messageId);
    }

    private async Task RetireGoalMessageAsync(
        DiscordCollaborationRefs refs, string goalKey, CancellationToken cancellationToken)
    {
        var messageRef = refs.GoalMessages[goalKey];
        await _api.EditMessageAsync(messageRef.ThreadId, messageRef.MessageId,
            BuildAllResolvedContent(goalKey, null), [], cancellationToken);
    }

    private static string GoalKey(CollaborationItem item) =>
        string.IsNullOrWhiteSpace(item.GoalId) ? "orchestrator" : item.GoalId;

    private static IReadOnlyList<CollaborationItem> OrderItems(IEnumerable<CollaborationItem> items) =>
        items
            .OrderBy(item => CollaborationItemLifecycle.AttentionPriority(item.Type))
            .ThenBy(item => item.RaisedAt)
            // Collapse duplicate escalations: a conductor that re-raises the same goal+reason each tick
            // produces many items sharing one correlation key — which would render as duplicate Discord
            // button customIds (Discord rejects with 50035) and a wall of repeated text. Show one per
            // key; key-less items stay distinct by id.
            .DistinctBy(item => string.IsNullOrWhiteSpace(item.CorrelationKey) ? item.Id : item.CorrelationKey)
            .ToList();

    private static string GoalLabel(string goalKey) =>
        goalKey == "orchestrator" ? "orchestrator" : goalKey[..Math.Min(8, goalKey.Length)];

    private static string BuildGoalThreadTitle(string goalKey) => $"[{GoalLabel(goalKey)}] needs attention";

    private static string BuildGoalContent(string goalKey, IReadOnlyList<CollaborationItem> items, string? footer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Goal `{GoalLabel(goalKey)}` — {items.Count} item(s) need attention:**");
        sb.AppendLine();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            sb.AppendLine($"{i + 1}. **[{item.Type}]** {item.Subject}");
            if (!string.IsNullOrWhiteSpace(item.Body))
                sb.AppendLine($"> {Truncate(item.Body, 220)}");
        }

        if (!string.IsNullOrWhiteSpace(footer))
        {
            sb.AppendLine();
            sb.AppendLine(footer);
        }

        return sb.ToString().Trim();
    }

    private static string BuildAllResolvedContent(string goalKey, string? resolvedBy) =>
        $"✅ Goal `{GoalLabel(goalKey)}` — all attention items resolved{(resolvedBy is null ? "" : $" (last by {resolvedBy})")}.";

    // One button per open item (numbered to match the list). Capped at Discord's 25-button limit; a
    // single goal essentially never exceeds it, but if it does the overflow stays in the text list.
    private static IReadOnlyList<DiscordButtonDefinition> BuildGoalButtons(IReadOnlyList<CollaborationItem> items)
    {
        var buttons = new List<DiscordButtonDefinition>();
        for (var i = 0; i < items.Count && buttons.Count < 25; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.CorrelationKey))
                continue;

            var (verb, resolution) = item.Type switch
            {
                CollaborationItemType.Verify => ("Verify", "verified"),
                CollaborationItemType.Clarification => ("Answer", "answered"),
                _ => ("Resolve", "resolved")
            };
            var customId = DiscordInteractionHandler.BuildDirectCustomId(item.CorrelationKey, resolution);
            buttons.Add(new DiscordButtonDefinition($"{verb} #{i + 1}", customId, DiscordButtonStyle.Success));
        }

        return buttons;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private DiscordCollaborationRefs LoadRefs()
    {
        var path = Path.Combine(_stateDirectory, RefsFileName);
        if (File.Exists(path))
        {
            try
            {
                var refs = JsonSerializer.Deserialize<DiscordCollaborationRefs>(File.ReadAllText(path), JsonOptions);
                if (refs is not null)
                {
                    // Tolerate older ref files (per-item Items / per-goal Threads maps) by ignoring them —
                    // the per-goal message model rebuilds its own refs on the next reconcile.
                    return new DiscordCollaborationRefs(
                        refs.GoalMessages ?? new Dictionary<string, GoalMessageRef>(StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
                // fall through to an empty ref set
            }
        }

        return new DiscordCollaborationRefs(new Dictionary<string, GoalMessageRef>(StringComparer.OrdinalIgnoreCase));
    }

    private void SaveRefs(DiscordCollaborationRefs refs)
    {
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(Path.Combine(_stateDirectory, RefsFileName), JsonSerializer.Serialize(refs, JsonOptions));
    }

    private sealed record DiscordCollaborationRefs(Dictionary<string, GoalMessageRef> GoalMessages);

    private sealed record GoalMessageRef(ulong ThreadId, ulong MessageId);
}
