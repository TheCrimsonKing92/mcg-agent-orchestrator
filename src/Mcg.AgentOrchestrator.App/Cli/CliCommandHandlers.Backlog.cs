using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecuteBacklogCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "backlog-list":
        {
            var all = parts.Any(p => p.Equals("--all", StringComparison.OrdinalIgnoreCase));
            var limit = ParseOptionalLimit(parts);
            var status = GetFlagValue(parts, "--status");
            var text = GetFlagValue(parts, "--text");
            var hasConstraints = limit is not null || !string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(text);
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var items = ApplyBacklogListFilters(store.ListAsync(all).GetAwaiter().GetResult(), status, text, limit);
            if (items.Count == 0)
            {
                Console.WriteLine(all
                    ? hasConstraints ? "No matching backlog items." : "No backlog items."
                    : hasConstraints ? "No matching open backlog items. Use --all to include closed items." : "No open backlog items. Use --all to include closed items.");
                return false;
            }
            foreach (var item in items)
                Console.WriteLine($"[{item.Status}] {item.Id} - {item.Title}");
            return false;
        }

        case "backlog-triage":
        {
            var limit = ParseOptionalLimit(parts) ?? 5;
            var staleDays = ParseOptionalNonNegativeInt(parts, "--stale-days") ?? 30;
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var items = store.ListAsync(includeAll: true).GetAwaiter().GetResult();
            Console.Write(RenderBacklogTriage(items, context.Kernel.Goals, limit, staleDays, DateTimeOffset.UtcNow));
            return false;
        }

        case "backlog-add":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-add <title> [body] | backlog-add <title> --body-file <path> | backlog-add <title> --text-file <path>");
            var title = parts[1];
            var body = ResolveTextArgumentOrDefault(parts, inlineIndex: 2, defaultValue: "", "--body-file", "--text-file") ?? "";
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.AddAsync(title, body).GetAwaiter().GetResult();
            Console.WriteLine($"Added: [{item.Id}] {item.Title}");
            return false;
        }

        case "backlog-show":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-show <id-prefix>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            Console.WriteLine($"Id:      {item.Id}");
            Console.WriteLine($"Title:   {item.Title}");
            Console.WriteLine($"Status:  {item.Status}");
            Console.WriteLine($"Created: {item.CreatedAt:O}");
            Console.WriteLine($"Updated: {item.UpdatedAt:O}");
            if (item.SourceGoalId is not null)
                Console.WriteLine($"Goal:    {item.SourceGoalId}");
            var linkedGoals = context.Kernel.Goals
                .Where(goal => string.Equals(goal.SourceBacklogItemId, item.Id, StringComparison.Ordinal))
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
                .ToList();
            if (linkedGoals.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Linked goals:");
                foreach (var goal in linkedGoals)
                {
                    Console.WriteLine($"- {goal.Id.Value[..8]} status={goal.Status} landing={ResolveBacklogShowGoalLandingState(context, goal)}");
                }
            }
            if (!string.IsNullOrWhiteSpace(item.Body))
            {
                Console.WriteLine();
                Console.WriteLine(item.Body);
            }
            if (item.Notes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Notes:");
                foreach (var note in item.Notes)
                {
                    Console.WriteLine($"- {note.CreatedAt:O}");
                    Console.WriteLine(note.Text);
                }
            }
            return false;
        }

        case "backlog-annotate":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-annotate <id-prefix> <note> | backlog-annotate <id-prefix> --text-file <path>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var note = ResolveTextArgumentOrDefault(parts, inlineIndex: 2, defaultValue: null, "--text-file")
                ?? throw new ArgumentException("Usage: backlog-annotate <id-prefix> <note> | backlog-annotate <id-prefix> --text-file <path>");
            var annotated = store.AppendNoteAsync(item.Id, note).GetAwaiter().GetResult();
            Console.WriteLine($"Annotated: [{annotated.Id}] {annotated.Title}");
            return false;
        }

        case "backlog-close":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var reason = ResolveTextArgumentOrDefault(parts, inlineIndex: 2, defaultValue: null, "--reason-file", "--text-file");
            var closed = store.CloseAsync(item.Id, reason).GetAwaiter().GetResult();
            Console.WriteLine($"Closed: [{closed.Id}] {closed.Title}");
            return false;
        }

        case "backlog-reopen":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-reopen <id-prefix> [reason]");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var reason = parts.Count > 2 ? parts[2] : null;
            var reopened = store.ReopenAsync(item.Id, reason).GetAwaiter().GetResult();
            Console.WriteLine($"Reopened: [{reopened.Id}] {reopened.Title}");
            return false;
        }

        case "backlog-view":
        {
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var all = store.ListAsync(includeAll: true).GetAwaiter().GetResult();
            Console.Write(RenderBacklogMarkdown(all));
            return false;
        }

        default:
            return null;
    }
}

private static string ResolveBacklogShowGoalLandingState(CliExecutionContext context, Goal goal)
{
    var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
    var workspaceExists = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null;
    var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
    var isRecorded = journal.LatestByOperation.Any(entry =>
        entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) &&
        entry.Status == GoalOperationStatus.Completed);
    var isCleanedUp = journal.LatestByOperation.Any(entry =>
        entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) &&
        entry.Status == GoalOperationStatus.Completed);
    return GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(workspaceExists, IsMerged: isMerged, IsRecorded: isRecorded, IsCleanedUp: isCleanedUp)).ToString();
}

internal static IReadOnlyList<BacklogItem> ApplyBacklogListFilters(
    IReadOnlyList<BacklogItem> items,
    string? status,
    string? text,
    int? limit)
{
    IEnumerable<BacklogItem> query = items;
    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(item => item.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(text))
    {
        query = query.Where(item =>
            item.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            item.Body.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    if (limit is { } cap)
    {
        query = query.Take(cap);
    }

    return query.ToArray();
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
        .Where(goal => goal.SourceBacklogItemId is not null && !IsTerminalGoalStatus(goal.Status))
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

private static int? ParseOptionalLimit(IReadOnlyList<string> parts)
{
    return ParseOptionalNonNegativeInt(parts, "--limit");
}

private static int? ParseOptionalNonNegativeInt(IReadOnlyList<string> parts, string flag)
{
    var value = GetFlagValue(parts, flag);
    if (value is null)
    {
        return null;
    }

    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) ||
        limit < 0)
    {
        throw new ArgumentException($"{flag} requires a non-negative integer value.");
    }

    return limit;
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
        sb.AppendLine($"- {ShortBacklogId(item)} age={ageDays}d{goalText} {item.Title}");
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
        var sample = string.Join(" | ", group.Take(3).Select(item => $"{ShortBacklogId(item)} {item.Title}"));
        sb.AppendLine($"- {group.Count()}x {group.Key}: {sample}");
    }
}

private static string ShortBacklogId(BacklogItem item) => item.Id[..Math.Min(8, item.Id.Length)];

private static bool IsTerminalGoalStatus(GoalStatus status) =>
    status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

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
