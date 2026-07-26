using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Surfaces the operator attention queue in Discord as ONE persistent message PER GOAL that needs
// attention — a per-goal priority list, edited in place as items are raised/resolved. A goal with no
// open attention items gets NO message (silence is the default). This replaces the old per-item delivery
// (a new message per CollaborationItem) which spammed a message for every escalation.
public sealed class DiscordCollaborationViewService
{
    private const string RefsFileName = "discord-collaboration-refs.json";
    private const int MaxActionButtons = 5;
    private readonly ICollaborationItemStore _store;
    private readonly IDiscordForumApi _api;
    private readonly ulong _forumChannelId;
    private readonly string _stateDirectory;
    private readonly IReadOnlyList<string> _allowedUserIds;
    private readonly Func<string, string, CancellationToken, Task<bool>>? _resolveClarificationAnswer;
    private readonly Func<string, CancellationToken, Task<long?>>? _currentGoalStateVersion;
    private readonly Func<string, CancellationToken, Task> _dispatchAction;
    private readonly Action<string>? _acknowledge;
    private readonly Func<DateTimeOffset> _clock;

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
        IReadOnlyList<string> allowedUserIds,
        Func<string, string, CancellationToken, Task<bool>>? resolveClarificationAnswer = null,
        Func<string, CancellationToken, Task<long?>>? currentGoalStateVersion = null,
        Func<string, CancellationToken, Task>? dispatchAction = null,
        Action<string>? acknowledge = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _api = api;
        _forumChannelId = forumChannelId;
        _stateDirectory = stateDirectory;
        _allowedUserIds = allowedUserIds;
        _resolveClarificationAnswer = resolveClarificationAnswer;
        _currentGoalStateVersion = currentGoalStateVersion;
        _dispatchAction = dispatchAction ?? ((_, _) => Task.CompletedTask);
        _acknowledge = acknowledge;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DiscordClarificationAnswerModalRequest? TryBuildAnswerModalRequest(
        string customId,
        string userId,
        out string? errorMessage) =>
        DiscordInteractionHandler.TryBuildAnswerModalRequest(customId, userId, _allowedUserIds, out errorMessage);

    public DiscordActionInputModalRequest? TryBuildActionInputModalRequest(
        string customId,
        string userId,
        out string? errorMessage) =>
        DiscordInteractionHandler.TryBuildActionInputModalRequest(customId, userId, _allowedUserIds, out errorMessage);

    public async Task RecordRejectedActionReferenceAsync(
        string customId,
        string userId,
        string interactionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordInteractionHandler.TryReadActionReference(customId, out var rejectedKey, out var rejectedIndex))
            return;

        await _store.RecordRejectedDecisionAsync(
            rejectedKey,
            rejectedIndex,
            $"discord:{userId}",
            interactionId,
            reason,
            _clock(),
            cancellationToken);
    }

    public async Task<DiscordInteractionResult> ApplyAnswerModalAsync(
        string modalCustomId,
        string answer,
        string userId,
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        var submit = DiscordInteractionHandler.ProcessAnswerModalSubmit(
            modalCustomId, answer, userId, interactionId, _allowedUserIds);
        if (submit.ErrorMessage is not null)
            return new DiscordInteractionResult(null, false, null, submit.ErrorMessage);

        var resolved = _resolveClarificationAnswer is null
            ? await _store.TryResolveAsync(submit.CorrelationKey, submit.Answer, cancellationToken)
            : await _resolveClarificationAnswer(submit.CorrelationKey, submit.Answer, cancellationToken);

        if (!resolved)
            return new DiscordInteractionResult(null, false, null, $"No open clarification for '{submit.CorrelationKey}'.");

        return await RefreshResolvedGoalMessageAsync(submit.CorrelationKey, submit.UserId, cancellationToken);
    }

    public async Task<DiscordInteractionResult> ApplyActionInputModalAsync(
        string modalCustomId,
        string modalText,
        string userId,
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        var submit = DiscordInteractionHandler.ProcessActionInputModalSubmit(
            modalCustomId, modalText, userId, interactionId, _allowedUserIds);
        if (submit.ErrorMessage is not null)
        {
            await RecordRejectedActionReferenceAsync(
                modalCustomId,
                userId,
                interactionId,
                submit.ErrorMessage,
                cancellationToken);

            return new DiscordInteractionResult(null, false, null, submit.ErrorMessage);
        }

        var decision = submit.Decision!;
        var apply = await ApplyDecisionAsync(decision, cancellationToken);
        if (apply.ErrorMessage is not null)
            return new DiscordInteractionResult(null, false, null, apply.ErrorMessage);
        if (!apply.Applied && !apply.Duplicate)
            return new DiscordInteractionResult(null, false, null, "Action rejected.");

        var refresh = await RefreshResolvedGoalMessageAsync(decision.InboxItemId, userId, cancellationToken);
        return refresh.ErrorMessage is null
            ? new DiscordInteractionResult(decision, false, null, null, $"Applied: {apply.Command}")
            : refresh;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (DiscordOperatorFaultClassifier.IsAuthDisabledForProcess)
            return;

        try
        {
            await ReconcileCoreAsync(cancellationToken);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            DiscordOperatorFaultClassifier.IsAuthError(ex))
        {
            DiscordOperatorFaultClassifier.DisableForProcess("collaboration view", ex);
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
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
            try
            {
                await UpsertGoalMessageAsync(refs, goalKey, items, cancellationToken);
                foreach (var item in items)
                    await _store.TryMarkDeliveredAsync(item.CorrelationKey!, cancellationToken);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                !DiscordOperatorFaultClassifier.IsAuthError(ex) &&
                !DiscordOperatorFaultClassifier.IsFatal(ex) &&
                !DiscordOperatorFaultClassifier.IsTransient(ex))
            {
                // A non-transient, non-fatal render failure for one goal (e.g. a Discord validation
                // error like 50035) must not crash the listener or block other goals' messages. Skip
                // it this cycle; it retries on the next reconcile. Transient (retry) and fatal (exit)
                // errors still propagate to the listener's fault handler.
                Console.Error.WriteLine(
                    $"operator-listen: skipped rendering attention for goal {GoalLabel(goalKey)}: {ex.Message}");
            }
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
        if (result.ErrorMessage is not null)
        {
            await RecordRejectedActionReferenceAsync(
                customId,
                userId,
                interactionId,
                result.ErrorMessage,
                cancellationToken);

            return result;
        }

        if (result.RequiresConfirmation || result.Decision is null)
            return result;

        var apply = await ApplyDecisionAsync(result.Decision, cancellationToken);
        if (!apply.Applied)
            return apply.Duplicate
                ? result
                : new DiscordInteractionResult(null, false, null, apply.ErrorMessage ?? "Action rejected.");

        var refresh = await RefreshResolvedGoalMessageAsync(result.Decision.InboxItemId, userId, cancellationToken);
        return refresh.ErrorMessage is null
            ? result with { AcknowledgementMessage = $"Applied: {apply.Command}" }
            : refresh;
    }

    private Task<DiscordDecisionApplicationResult> ApplyDecisionAsync(
        OperatorDecision decision,
        CancellationToken cancellationToken)
    {
        var applier = new DiscordDecisionApplier(
            _stateDirectory,
            _dispatchAction,
            _store,
            _allowedUserIds,
            acknowledge: _acknowledge,
            currentGoalStateVersion: _currentGoalStateVersion,
            clock: _clock);
        return applier.ApplyDetailedAsync(decision, cancellationToken);
    }

    private async Task<DiscordInteractionResult> RefreshResolvedGoalMessageAsync(
        string correlationKey,
        string userId,
        CancellationToken cancellationToken)
    {
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
                var actionSurface = await BuildGoalActionSurfaceAsync(remaining, cancellationToken);
                var content = BuildGoalContent(
                    goalKey,
                    remaining,
                    actionSurface.OverflowNotice,
                    $"✅ resolved one by {userId}");
                await _api.EditMessageAsync(messageRef.ThreadId, messageRef.MessageId,
                    content,
                    actionSurface.Buttons, cancellationToken);
                await _store.UpdateRenderedContentHashAsync(
                    remaining.Select(item => item.CorrelationKey!),
                    ComputeContentHash(content),
                    cancellationToken);
            }
        }

        return new DiscordInteractionResult(
            new OperatorDecision(correlationKey, 0, null, $"discord:{userId}", correlationKey),
            false,
            null,
            null);
    }

    private async Task UpsertGoalMessageAsync(
        DiscordCollaborationRefs refs, string goalKey, IReadOnlyList<CollaborationItem> items, CancellationToken cancellationToken)
    {
        var actionSurface = await BuildGoalActionSurfaceAsync(items, cancellationToken);
        var content = BuildGoalContent(goalKey, items, actionSurface.OverflowNotice, footer: null);
        var correlationKeys = items
            .Where(item => !string.IsNullOrWhiteSpace(item.CorrelationKey))
            .Select(item => item.CorrelationKey!)
            .ToList();
        var contentHash = ComputeContentHash(content);

        if (refs.GoalMessages.TryGetValue(goalKey, out var messageRef))
        {
            await _api.EditMessageAsync(
                messageRef.ThreadId,
                messageRef.MessageId,
                content,
                actionSurface.Buttons,
                cancellationToken);
            await _store.UpdateRenderedContentHashAsync(correlationKeys, contentHash, cancellationToken);
            return;
        }

        var threadId = await _api.CreateThreadAsync(_forumChannelId, BuildGoalThreadTitle(goalKey), content, cancellationToken);
        var messageId = await _api.SendMessageAsync(
            threadId,
            content,
            actionSurface.Buttons,
            cancellationToken);
        await _store.UpdateRenderedContentHashAsync(correlationKeys, contentHash, cancellationToken);
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
            .ThenByDescending(item => item.RaisedAt)
            // Collapse duplicate escalations: a conductor that re-raises the same goal+reason each tick
            // produces many items sharing one correlation key — which would render as duplicate Discord
            // button customIds (Discord rejects with 50035) and a wall of repeated text. Show one per
            // key; key-less items stay distinct by id.
            .DistinctBy(item => string.IsNullOrWhiteSpace(item.CorrelationKey) ? item.Id : item.CorrelationKey)
            .ToList();

    private static string GoalLabel(string goalKey) =>
        goalKey == "orchestrator" ? "orchestrator" : goalKey[..Math.Min(8, goalKey.Length)];

    private static string BuildGoalThreadTitle(string goalKey) => $"[{GoalLabel(goalKey)}] needs attention";

    // Discord rejects message content longer than 2000 characters with error 50035. A goal that
    // accumulates several long items (e.g. multiple verbose clarifications) would otherwise blow past
    // this and crash the listener, so the aggregated per-goal message is capped.
    internal const int DiscordMessageLimit = 2000;

    private static string BuildGoalContent(
        string goalKey,
        IReadOnlyList<CollaborationItem> items,
        string overflow,
        string? footer)
    {
        // Reserve headroom for the footer, truncation notice, and button-overflow inventory so none
        // of the open items hidden from the five-button surface become invisible.
        var budget = DiscordMessageLimit - 220 - overflow.Length;
        var sb = new StringBuilder();
        sb.AppendLine($"**Goal `{GoalLabel(goalKey)}` — {items.Count} item(s) need attention:**");
        sb.AppendLine();
        var rendered = 0;
        for (var i = 0; i < Math.Min(items.Count, MaxActionButtons); i++)
        {
            var item = items[i];
            var line = $"{i + 1}. **[{item.Type}]** {Truncate(item.Subject, 180)}";
            var body = string.IsNullOrWhiteSpace(item.Body) ? null : $"> {Truncate(item.Body, 220)}";
            if (rendered > 0 && sb.Length + line.Length + (body?.Length ?? 0) + 4 > budget)
            {
                sb.AppendLine($"_…and {items.Count - rendered} more item(s) — truncated to fit Discord's {DiscordMessageLimit}-char limit. Resolve some to see the rest._");
                break;
            }

            sb.AppendLine(line);
            if (body is not null)
                sb.AppendLine(body);
            rendered++;
        }

        if (overflow.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(overflow);
        }

        if (!string.IsNullOrWhiteSpace(footer))
        {
            sb.AppendLine();
            sb.AppendLine(footer);
        }

        var result = sb.ToString().Trim();
        // Hard safety net so a single oversized item or footer can never exceed the limit.
        return result.Length <= DiscordMessageLimit ? result : result[..(DiscordMessageLimit - 1)] + "…";
    }

    private static string BuildAllResolvedContent(string goalKey, string? resolvedBy) =>
        $"✅ Goal `{GoalLabel(goalKey)}` — all attention items resolved{(resolvedBy is null ? "" : $" (last by {resolvedBy})")}.";

    // A goal card exposes every valid action binding until Discord's five-button surface is full.
    // Multiple actions on one item are distinct operator choices and must not be silently collapsed.
    private async Task<GoalActionSurface> BuildGoalActionSurfaceAsync(
        IReadOnlyList<CollaborationItem> items,
        CancellationToken cancellationToken)
    {
        var buttons = new List<DiscordButtonDefinition>();
        var overflow = new List<OverflowAction>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.CorrelationKey))
                continue;

            var verb = item.Type switch
            {
                CollaborationItemType.Verify => "Verify",
                CollaborationItemType.Clarification => "Answer",
                _ => "Resolve"
            };
            if (item.Type == CollaborationItemType.Clarification)
            {
                AddAction(
                    buttons,
                    overflow,
                    item,
                    verb,
                    new DiscordButtonDefinition(
                        $"{verb} #{i + 1}",
                        DiscordInteractionHandler.BuildAnswerCustomId(item.CorrelationKey),
                        DiscordButtonStyle.Success));
                continue;
            }

            var actions = await _store.ListActionsAsync(item.CorrelationKey, cancellationToken);
            foreach (var action in actions.Where(action => action.ConsumedAt is null))
            {
                var customId = action.RequiresInput
                    ? DiscordInteractionHandler.BuildActionInputCustomId(item.CorrelationKey, action.ActionIndex)
                    : action.RequiresConfirmation
                    ? DiscordInteractionHandler.BuildConfirmCustomId(item.CorrelationKey, action.ActionIndex)
                    : DiscordInteractionHandler.BuildDirectCustomId(item.CorrelationKey, action.ActionIndex);
                AddAction(
                    buttons,
                    overflow,
                    item,
                    action.Label,
                    new DiscordButtonDefinition(
                        $"{action.Label} #{i + 1}",
                        customId,
                        action.RequiresConfirmation ? DiscordButtonStyle.Danger : DiscordButtonStyle.Success));
            }
        }

        return new GoalActionSurface(buttons, BuildButtonOverflowNotice(overflow));
    }

    private static void AddAction(
        ICollection<DiscordButtonDefinition> buttons,
        ICollection<OverflowAction> overflow,
        CollaborationItem item,
        string actionLabel,
        DiscordButtonDefinition button)
    {
        if (buttons.Count < MaxActionButtons)
        {
            buttons.Add(button);
            return;
        }

        overflow.Add(new OverflowAction(
            BuildOverflowIdentifier(item),
            item.Subject,
            actionLabel));
    }

    private static string BuildButtonOverflowNotice(IEnumerable<OverflowAction> overflowActions)
    {
        var named = overflowActions
            .Select(action =>
                $"`{Truncate(action.Identifier, 16)}` {Truncate(action.Subject, 48)} — {Truncate(action.Label, 28)}")
            .ToList();
        return named.Count == 0
            ? string.Empty
            : $"+{named.Count} more (no button): {string.Join(", ", named)} — answer via attention verbs or terminal.";
    }

    private static string BuildOverflowIdentifier(CollaborationItem item)
    {
        var key = string.IsNullOrWhiteSpace(item.CorrelationKey) ? item.Id : item.CorrelationKey!;
        if (item.Type != CollaborationItemType.Clarification)
            return key;

        var lastSegment = key[(key.LastIndexOf(':') + 1)..];
        return lastSegment.Length <= 8 ? lastSegment : lastSegment[..8];
    }

    internal static string ComputeContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private sealed record GoalActionSurface(
        IReadOnlyList<DiscordButtonDefinition> Buttons,
        string OverflowNotice);

    private sealed record OverflowAction(string Identifier, string Subject, string Label);

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
