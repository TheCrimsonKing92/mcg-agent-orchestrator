using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

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
        var items = await _store.GetAttentionQueueAsync(cancellationToken);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.CorrelationKey))
                continue;

            var refs = LoadRefs();
            if (!refs.Items.TryGetValue(item.Id, out var messageRef))
            {
                await DeliverAsync(item, cancellationToken);
                continue;
            }

            await _api.EditMessageAsync(
                messageRef.ThreadId,
                messageRef.MessageId,
                BuildContent(item),
                BuildButtons(item, disabled: false),
                cancellationToken);
        }
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
        var changed = await _store.TryResolveAsync(correlationKey, resolution, cancellationToken);
        var item = (await _store.ListAsync(null, cancellationToken))
            .FirstOrDefault(candidate => candidate.CorrelationKey == correlationKey);
        if (item is null)
            return new DiscordInteractionResult(null, false, null, $"No collaboration item for '{correlationKey}'.");

        var refs = LoadRefs();
        if (refs.Items.TryGetValue(item.Id, out var messageRef))
        {
            var resolvedItem = item with
            {
                Status = CollaborationItemStatus.Resolved,
                Resolution = item.Resolution ?? resolution,
                ResolvedAt = item.ResolvedAt ?? DateTimeOffset.UtcNow
            };
            await _api.EditMessageAsync(
                messageRef.ThreadId,
                messageRef.MessageId,
                BuildContent(resolvedItem, $" by {userId}"),
                BuildButtons(resolvedItem, disabled: true),
                cancellationToken);
            if (changed)
            {
                await _api.SendMessageAsync(
                    messageRef.ThreadId,
                    $"Applied: {resolution}",
                    [],
                    cancellationToken);
            }
        }

        return result;
    }

    private async Task DeliverAsync(CollaborationItem item, CancellationToken cancellationToken)
    {
        var refs = LoadRefs();
        var threadKey = ThreadKey(item);

        if (!refs.Threads.TryGetValue(threadKey, out var threadId))
        {
            // First escalation for this id/goal: open one thread for it.
            var title = BuildThreadTitle(item);
            threadId = await _api.CreateThreadAsync(_forumChannelId, title, BuildContent(item), cancellationToken);
            refs.Threads[threadKey] = threadId;
        }

        // Every escalation for the same id posts as a message in that one thread rather than
        // spawning a new thread, so a goal's escalations stay grouped and trackable.
        var messageId = await _api.SendMessageAsync(
            threadId,
            BuildContent(item),
            BuildButtons(item, disabled: false),
            cancellationToken);

        refs.Items[item.Id] = new DiscordItemMessageRef(threadId, messageId);
        SaveRefs(refs);
        await _store.TryMarkDeliveredAsync(item.CorrelationKey!, cancellationToken);
    }

    // One Discord thread per goal id (the thread title is already goal-scoped); orchestrator-level
    // items without a goal share a single "orchestrator" thread.
    private static string ThreadKey(CollaborationItem item) =>
        string.IsNullOrWhiteSpace(item.GoalId) ? "orchestrator" : item.GoalId;

    private static string BuildThreadTitle(CollaborationItem item)
    {
        var goal = string.IsNullOrWhiteSpace(item.GoalId)
            ? "orchestrator"
            : item.GoalId[..Math.Min(8, item.GoalId.Length)];
        return $"[{goal}] {item.Type}: {item.Subject}";
    }

    private static string BuildContent(CollaborationItem item, string resolvedBy = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**[{item.Type}]** {item.Subject}");
        sb.AppendLine();
        sb.AppendLine(item.Body);
        sb.AppendLine();
        sb.AppendLine(item.Status == CollaborationItemStatus.Resolved
            ? $"✅ {item.Resolution}{resolvedBy}"
            : "Status: unresolved");
        return sb.ToString().Trim();
    }

    private static IReadOnlyList<DiscordButtonDefinition> BuildButtons(CollaborationItem item, bool disabled)
    {
        if (string.IsNullOrWhiteSpace(item.CorrelationKey))
            return [];

        var label = item.Type switch
        {
            CollaborationItemType.Verify => "Verified",
            CollaborationItemType.Clarification => "Answered",
            _ => "Resolve"
        };
        var resolution = item.Type switch
        {
            CollaborationItemType.Verify => "verified",
            CollaborationItemType.Clarification => "answered",
            _ => "resolved"
        };
        var customId = DiscordInteractionHandler.BuildDirectCustomId(item.CorrelationKey, resolution);
        return [new DiscordButtonDefinition(label, customId, disabled ? DiscordButtonStyle.Secondary : DiscordButtonStyle.Success, disabled)];
    }

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
                    // Tolerate older ref files written before per-goal thread grouping (no Threads map).
                    return new DiscordCollaborationRefs(
                        refs.Items ?? new Dictionary<string, DiscordItemMessageRef>(StringComparer.OrdinalIgnoreCase),
                        refs.Threads ?? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
                // fall through to an empty ref set
            }
        }

        return new DiscordCollaborationRefs(
            new Dictionary<string, DiscordItemMessageRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase));
    }

    private void SaveRefs(DiscordCollaborationRefs refs)
    {
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(Path.Combine(_stateDirectory, RefsFileName), JsonSerializer.Serialize(refs, JsonOptions));
    }

    private sealed record DiscordCollaborationRefs(
        Dictionary<string, DiscordItemMessageRef> Items,
        Dictionary<string, ulong> Threads);

    private sealed record DiscordItemMessageRef(ulong ThreadId, ulong MessageId);
}
