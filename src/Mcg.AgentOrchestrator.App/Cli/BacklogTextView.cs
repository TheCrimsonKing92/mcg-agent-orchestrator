using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogTextView
{
internal static string RenderBacklogListTag(BacklogItem item)
{
    if (item.SupersededBy is not null)
        return $"[Superseded→{CliIdentifierText.ShortId(item.SupersededBy)}]";

    var duplicate = item.Links.FirstOrDefault(link =>
        link.Kind == BacklogLinkKind.Duplicate &&
        string.Equals(link.DuplicateId, item.Id, StringComparison.Ordinal));
    if (duplicate?.CanonicalId is not null)
        return $"[Dup→{CliIdentifierText.ShortId(duplicate.CanonicalId)}]";

    return $"[{item.Status}]";
}

internal static string RenderBacklogTimestampSuffix(BacklogItem item) =>
    FormattableString.Invariant($" [created={item.CreatedAt.UtcDateTime:yyyy-MM-dd}] [updated={item.UpdatedAt.UtcDateTime:yyyy-MM-dd}]");

internal static string RenderBacklogListSuffix(BacklogItem item)
{
    var related = item.Links
        .Where(link => link.Kind == BacklogLinkKind.Related)
        .Select(link => $"[Related→{CliIdentifierText.ShortId(link.OtherId(item.Id))}]")
        .ToArray();
    return related.Length == 0 ? "" : " " + string.Join(' ', related);
}

internal static string RenderBacklogTriage(
    IReadOnlyList<BacklogItem> items,
    IReadOnlyCollection<Goal> goals,
    int limit,
    int staleDays,
    DateTimeOffset now)
{
    if (limit < 0)
        throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be non-negative.");
    if (staleDays < 0)
        throw new ArgumentOutOfRangeException(nameof(staleDays), "Stale days must be non-negative.");

    var openItems = items.Where(item => item.Status == BacklogItemStatus.Open).ToArray();
    var activeGoalByBacklogId = goals
        .Where(goal => goal.SourceBacklogItemId is not null && !ConductorWorkingSet.IsTerminalGoal(goal))
        .GroupBy(goal => goal.SourceBacklogItemId!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    var linkedOpenItems = openItems
        .Where(item => activeGoalByBacklogId.ContainsKey(item.Id))
        .ToArray();
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Backlog triage: open={openItems.Length} done={items.Count - openItems.Length} active-linked={linkedOpenItems.Length} limit={limit}");
    AppendTriageBucket(
        sb,
        $"Stale open (>{staleDays}d)",
        openItems
            .Where(item => (now - item.UpdatedAt).TotalDays > staleDays)
            .OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendTriageBucket(
        sb,
        "Active-linked open",
        linkedOpenItems.OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendTriageBucket(
        sb,
        "Blocked-looking open",
        openItems
            .Where(IsBlockedLooking)
            .OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendTriageBucket(
        sb,
        "High-priority open",
        openItems
            .Where(IsHighPriorityLooking)
            .OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendDuplicateBucket(sb, openItems, limit);
    return sb.ToString();
}

private static void AppendTriageBucket(
    System.Text.StringBuilder sb,
    string heading,
    IEnumerable<BacklogItem> items,
    int limit,
    IReadOnlyDictionary<string, Goal> activeGoalByBacklogId,
    DateTimeOffset now)
{
    var selected = items.Take(limit).ToArray();
    sb.AppendLine(heading + ":");
    if (selected.Length == 0)
    {
        sb.AppendLine("- none");
        return;
    }

    foreach (var item in selected)
    {
        var ageDays = Math.Max(0, (int)Math.Floor((now - item.UpdatedAt).TotalDays));
        var goalText = activeGoalByBacklogId.TryGetValue(item.Id, out var goal)
            ? $" goal={goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]}:{goal.Status}"
            : "";
        sb.AppendLine($"- {CliIdentifierText.ShortId(item.Id)} age={ageDays}d{goalText} {item.Title}");
    }
}

private static void AppendDuplicateBucket(System.Text.StringBuilder sb, IReadOnlyList<BacklogItem> openItems, int limit)
{
    var duplicateGroups = openItems
        .GroupBy(item => DuplicateKey(item.Title), StringComparer.Ordinal)
        .Where(group => group.Key.Length > 0 && group.Count() > 1)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .Take(limit)
        .ToArray();

    sb.AppendLine("Duplicate-looking open:");
    if (duplicateGroups.Length == 0)
    {
        sb.AppendLine("- none");
        return;
    }

    foreach (var group in duplicateGroups)
    {
        var sample = string.Join(" | ", group.Take(3).Select(item => $"{CliIdentifierText.ShortId(item.Id)} {item.Title}"));
        sb.AppendLine($"- {group.Count()}x {group.Key}: {sample}");
    }
}

private static bool IsBlockedLooking(BacklogItem item) =>
    ContainsAny(item.Title, "blocked", "blocker", "stuck", "waiting", "human input") ||
    ContainsAny(item.Body, "blocked", "blocker", "stuck", "waiting", "human input");

private static bool IsHighPriorityLooking(BacklogItem item) =>
    ContainsAny(item.Title, "p0", "p1", "urgent", "critical", "high-priority", "high priority") ||
    ContainsAny(item.Body, "p0", "p1", "urgent", "critical", "high-priority", "high priority");

private static bool ContainsAny(string text, params string[] needles) =>
    needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

private static string DuplicateKey(string title)
{
    var normalized = new string(title
        .ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
        .ToArray());
    return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

internal static string RenderBacklogMarkdown(IReadOnlyList<BacklogItem> items)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# Backlog");
    sb.AppendLine();

    var openItems = items.Where(i => i.Status == BacklogItemStatus.Open).ToList();
    var doneItems = items.Where(i => i.Status == BacklogItemStatus.Done).ToList();

    foreach (var item in openItems)
    {
        sb.AppendLine($"## {item.Title}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            sb.AppendLine(item.Body);
            sb.AppendLine();
        }
    }

    if (doneItems.Count > 0)
    {
        sb.AppendLine("## Shipped (closed)");
        sb.AppendLine();
        foreach (var item in doneItems)
            sb.AppendLine($"- **{item.Title}** — {item.UpdatedAt:yyyy-MM-dd}");
    }

    return sb.ToString();
}
}
