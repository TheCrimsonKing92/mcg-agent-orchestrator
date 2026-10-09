using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillReadyItemSelector
{
    internal const int MaxFailedRoundsPerChange = 2;

    private static readonly string[] OwnerMarkers =
        ["owner decision", "owner approval", "owner ruling", "approve-policy-change", "decision for you"];

    internal static DateTimeOffset ChangeStamp(BacklogItem item) =>
        item.Notes.Select(note => note.CreatedAt).Append(item.UpdatedAt).Max();

    internal static bool IsOwnerGated(BacklogItem item) =>
        item.Tags.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("owner-gated", StringComparer.OrdinalIgnoreCase) ||
        OwnerMarkers.Any(marker => item.Body.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            item.Notes.Any(note => note.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    internal static BacklogItem? Select(IReadOnlyList<BacklogItem> items, IReadOnlyList<Goal> goals,
        IReadOnlySet<string> alreadyDrafted, Func<BacklogItem, BacklogReadiness> readiness,
        IReadOnlyList<BoardFillDraftRound>? rounds = null) =>
        items.Where(item => item.Status == BacklogItemStatus.Open &&
            !alreadyDrafted.Contains(item.Id) &&
            (rounds is null || rounds.Count(round => round.BacklogItemId == item.Id &&
                BoardFillFailureKind.CountsTowardItem(round) && round.ChangeStamp >= ChangeStamp(item)) < MaxFailedRoundsPerChange) &&
            !goals.Any(goal => goal.SourceBacklogItemId == item.Id && !goal.IsTerminal) &&
            !IsOwnerGated(item))
        .Select(item => (Item: item, Ready: readiness(item)))
        .Where(candidate => candidate.Ready.Eligible)
        .OrderByDescending(candidate => !string.IsNullOrWhiteSpace(candidate.Item.Priority))
        .ThenByDescending(candidate => candidate.Ready.HasLandedDependency)
        .ThenByDescending(candidate => candidate.Item.UpdatedAt)
        .ThenBy(candidate => candidate.Item.Id, StringComparer.Ordinal)
        .Select(candidate => candidate.Item).FirstOrDefault();
}
