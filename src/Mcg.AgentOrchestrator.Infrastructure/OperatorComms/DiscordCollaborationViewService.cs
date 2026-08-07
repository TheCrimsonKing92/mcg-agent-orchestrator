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

    // Discord rejects message content longer than 2000 characters with error 50035. A goal that
    // accumulates several long items (e.g. multiple verbose clarifications) would otherwise blow past
    // this and crash the listener, so the aggregated per-goal message is capped.
    internal const int DiscordMessageLimit = 2000;
    internal const int MinimumItemSlice = 120;
    private const int ObjectivePreviewLimit = 160;
    private const int SubjectDisplayLimit = 40;

    private static string BuildGoalContent(
        string goalKey,
        IReadOnlyList<CollaborationItem> items,
        string overflow,
        string? footer)
    {
        var goalLabel = GoalLabel(goalKey);
        var header = new StringBuilder($"**Goal `{goalLabel}` — {items.Count} item(s) need attention:**");
        var objective = ExtractGoalObjective(items);
        if (!string.IsNullOrWhiteSpace(objective))
            header.Append($"\nObjective: {TruncateToLength(objective, ObjectivePreviewLimit)}");

        var naturalItems = items
            .Select((item, index) => RenderItem(item, index + 1, int.MaxValue, goalLabel))
            .ToArray();
        var renderCount = items.Count;
        string? omittedLine = null;
        while (renderCount > 0)
        {
            var omitted = items.Count - renderCount;
            omittedLine = omitted == 0
                ? null
                : $"+{omitted} more clarifications not shown — run `attention show {goalLabel}`";
            var available = AvailableItemBudget(
                header.Length,
                renderCount,
                omittedLine,
                overflow,
                footer);
            var required = naturalItems
                .Take(renderCount)
                .Sum(item => Math.Min(item.Length, MinimumItemSlice));
            if (available >= required)
                break;

            renderCount--;
        }

        if (renderCount == 0 && items.Count > 0)
        {
            renderCount = 1;
            omittedLine = items.Count == 1
                ? null
                : $"+{items.Count - 1} more clarifications not shown — run `attention show {goalLabel}`";
        }

        var itemBudget = AvailableItemBudget(
            header.Length,
            renderCount,
            omittedLine,
            overflow,
            footer);
        var allocations = AllocateFairShares(
            naturalItems.Take(renderCount).Select(item => item.Length).ToArray(),
            itemBudget);
        var renderedItems = items
            .Take(renderCount)
            .Select((item, index) => RenderItem(item, index + 1, allocations[index], goalLabel))
            .ToArray();

        var blocks = new List<string> { header.ToString() };
        if (renderedItems.Length > 0)
            blocks.Add(string.Join('\n', renderedItems));
        if (!string.IsNullOrWhiteSpace(overflow))
            blocks.Add(overflow);
        if (!string.IsNullOrWhiteSpace(footer))
            blocks.Add(footer);
        if (!string.IsNullOrWhiteSpace(omittedLine))
            blocks.Add(omittedLine);

        var result = string.Join("\n\n", blocks);
        return result.Length <= DiscordMessageLimit
            ? result
            : TruncateToLength(result, DiscordMessageLimit);
    }

    private static int AvailableItemBudget(
        int headerLength,
        int itemCount,
        string? omittedLine,
        string overflow,
        string? footer)
    {
        var budget = DiscordMessageLimit - headerLength;
        if (itemCount > 0)
            budget -= 2 + itemCount - 1;
        foreach (var block in new[] { omittedLine, overflow, footer })
        {
            if (!string.IsNullOrWhiteSpace(block))
                budget -= 2 + block.Length;
        }

        return Math.Max(0, budget);
    }

    private static int[] AllocateFairShares(IReadOnlyList<int> naturalLengths, int totalBudget)
    {
        var allocations = new int[naturalLengths.Count];
        var remaining = Enumerable.Range(0, naturalLengths.Count).ToList();
        var remainingBudget = totalBudget;
        while (remaining.Count > 0)
        {
            var share = remainingBudget / remaining.Count;
            var completed = remaining.Where(index => naturalLengths[index] <= share).ToArray();
            if (completed.Length == 0)
            {
                foreach (var index in remaining)
                {
                    allocations[index] = share;
                    if (remainingBudget % remaining.Count > 0)
                    {
                        allocations[index]++;
                        remainingBudget--;
                    }
                }

                break;
            }

            foreach (var index in completed)
            {
                allocations[index] = naturalLengths[index];
                remainingBudget -= allocations[index];
                remaining.Remove(index);
            }
        }

        return allocations;
    }

    private static string RenderItem(CollaborationItem item, int index, int maxLength, string goalLabel)
    {
        var subject = TruncateToLength(item.Subject.Trim(), SubjectDisplayLimit);
        var label = $"{index}. **[{item.Type}]** {subject}";
        var displayBody = item.Type == CollaborationItemType.Clarification
            ? ExtractClarificationDisplayBody(item.Body)
            : RemoveGoalObjective(item.Body);
        if (string.IsNullOrWhiteSpace(displayBody) || label.Length >= maxLength)
            return label;

        var quotedBody = Quote(displayBody);
        var natural = $"{label}\n{quotedBody}";
        if (natural.Length <= maxLength)
            return natural;

        var bodyBudget = maxLength - label.Length - 1;
        var pointer = Quote($"… truncated to fit: `attention show {goalLabel}`");
        var mainBudget = Math.Max(0, bodyBudget - pointer.Length - 1);
        var (question, options) = SplitAnswerOptions(displayBody);
        string renderedMain;
        if (!string.IsNullOrWhiteSpace(options))
        {
            var quotedOptions = Quote(options);
            if (quotedOptions.Length < mainBudget)
            {
                var questionBudget = mainBudget - quotedOptions.Length - 1;
                renderedMain = $"{Quote(TruncateToLength(Flatten(question), Math.Max(1, questionBudget - 2)))}\n{quotedOptions}";
            }
            else
            {
                var questionBudget = Math.Min(32, Math.Max(0, mainBudget / 4));
                var optionsBudget = Math.Max(1, mainBudget - questionBudget - 1);
                renderedMain = $"{Quote(TruncateToLength(Flatten(question), Math.Max(1, questionBudget - 2)))}\n" +
                    Quote(TruncateToLength(Flatten(options), Math.Max(1, optionsBudget - 2)));
            }
        }
        else
        {
            renderedMain = Quote(TruncateToLength(Flatten(question), Math.Max(1, mainBudget - 2)));
        }

        var rendered = $"{label}\n{renderedMain}\n{pointer}";
        return rendered.Length <= maxLength ? rendered : TruncateToLength(rendered, maxLength);
    }

    private static string? ExtractGoalObjective(IEnumerable<CollaborationItem> items)
    {
        foreach (var item in items)
        {
            var lines = item.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Goal objective:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var objective = new List<string> { lines[i]["Goal objective:".Length..].Trim() };
                for (i++; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]); i++)
                    objective.Add(lines[i].Trim());
                return string.Join(' ', objective).Trim();
            }
        }

        return null;
    }

    private static string ExtractClarificationDisplayBody(string body)
    {
        var lines = RemoveGoalObjective(body)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var questionStart = Array.FindIndex(lines, line => line.StartsWith("Question:", StringComparison.OrdinalIgnoreCase));
        if (questionStart < 0)
            return string.Join('\n', lines).Trim();

        var end = Array.FindIndex(lines, questionStart + 1, line =>
            line.StartsWith("Fork kind:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Topic key:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Blast radius:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Refiner confidence:", StringComparison.OrdinalIgnoreCase));
        if (end < 0)
            end = lines.Length;
        return string.Join('\n', lines[questionStart..end]).Trim();
    }

    private static string RemoveGoalObjective(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("Goal objective:", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(lines[i]);
                continue;
            }

            while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
                i++;
        }

        return string.Join('\n', output).Trim();
    }

    private static (string Question, string Options) SplitAnswerOptions(string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var optionStart = Array.FindIndex(lines, line =>
            line.StartsWith("Answer options:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Options:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Choose exactly one", StringComparison.OrdinalIgnoreCase));
        return optionStart < 0
            ? (body, string.Empty)
            : (string.Join('\n', lines[..optionStart]).Trim(), string.Join('\n', lines[optionStart..]).Trim());
    }

    private static string Quote(string value) =>
        string.Join('\n', value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => $"> {line}"));

    private static string Flatten(string value) =>
        string.Join(' ', value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
                // Keep the complete 20-action overflow inventory below the space left after the
                // goal header and its first rendered item. The identifier, subject, and action
                // remain recognizable without letting the final Discord safety cap erase tail entries.
                $"`{Truncate(action.Identifier, 16)}` {Truncate(action.Subject, 24)} — {Truncate(action.Label, 16)}")
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

    private static string TruncateToLength(string value, int max) =>
        value.Length <= max
            ? value
            : max <= 1
                ? "…"[..Math.Max(0, max)]
                : value[..(max - 1)] + "…";

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
