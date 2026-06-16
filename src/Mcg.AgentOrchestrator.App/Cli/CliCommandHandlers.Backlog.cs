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
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var items = store.ListAsync(all).GetAwaiter().GetResult();
            if (items.Count == 0)
            {
                Console.WriteLine(all
                    ? "No backlog items."
                    : "No open backlog items. Use --all to include closed items.");
                return false;
            }
            foreach (var item in items)
                Console.WriteLine($"[{item.Status}] {item.Id} — {item.Title}");
            return false;
        }

        case "backlog-add":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-add <title> [body]");
            var title = parts[1];
            var body = parts.Count > 2 ? parts[2] : "";
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
            if (!string.IsNullOrWhiteSpace(item.Body))
            {
                Console.WriteLine();
                Console.WriteLine(item.Body);
            }
            return false;
        }

        case "backlog-close":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-close <id-prefix> [reason]");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var reason = parts.Count > 2 ? parts[2] : null;
            var closed = store.CloseAsync(item.Id, reason).GetAwaiter().GetResult();
            Console.WriteLine($"Closed: [{closed.Id}] {closed.Title}");
            return false;
        }

        case "backlog-import":
        {
            var backlogMdPath = Path.Combine(context.Workspace.RootDirectory, "BACKLOG.md");
            if (!File.Exists(backlogMdPath))
            {
                Console.WriteLine($"No BACKLOG.md found at {backlogMdPath}");
                return false;
            }
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var (added, skipped) = ImportBacklogMd(backlogMdPath, store);
            Console.WriteLine($"Imported: {added} added, {skipped} already existed.");
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

private static (int added, int skipped) ImportBacklogMd(string path, BacklogStore store)
{
    var lines = File.ReadAllLines(path);
    var sections = ParseBacklogMdSections(lines);
    var added = 0;
    var skipped = 0;
    var now = DateTimeOffset.UtcNow;
    foreach (var (title, body, isDone) in sections)
    {
        var id = BacklogStore.SlugId(title);
        if (string.IsNullOrEmpty(id))
            continue;
        var status = isDone ? BacklogItemStatus.Done : BacklogItemStatus.Open;
        var item = new BacklogItem(id, title, body, status, now, now, null);
        var wasAdded = store.UpsertAsync(item).GetAwaiter().GetResult();
        if (wasAdded) added++;
        else skipped++;
    }
    return (added, skipped);
}

internal static IReadOnlyList<(string Title, string Body, bool IsDone)> ParseBacklogMdSections(string[] lines)
{
    var results = new List<(string, string, bool)>();
    string? currentTitle = null;
    var currentBody = new System.Text.StringBuilder();
    var currentIsDone = false;

    foreach (var line in lines)
    {
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            if (currentTitle is not null)
                results.Add((currentTitle, currentBody.ToString().Trim(), currentIsDone));

            currentTitle = line[3..].Trim();
            currentBody.Clear();
            var titleUpper = currentTitle.ToUpperInvariant();
            currentIsDone = titleUpper.Contains("DONE", StringComparison.Ordinal)
                || titleUpper.Contains("CLOSED", StringComparison.Ordinal)
                || titleUpper.Contains("SHIPPED", StringComparison.Ordinal);
        }
        else if (currentTitle is not null && !line.StartsWith("# ", StringComparison.Ordinal))
        {
            if (currentBody.Length > 0 || !string.IsNullOrWhiteSpace(line))
                currentBody.AppendLine(line);
        }
    }

    if (currentTitle is not null)
        results.Add((currentTitle, currentBody.ToString().Trim(), currentIsDone));

    return results;
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
