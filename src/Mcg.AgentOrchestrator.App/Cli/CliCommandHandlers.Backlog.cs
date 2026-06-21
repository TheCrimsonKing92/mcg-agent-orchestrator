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
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-add <title> [body] | backlog-add <title> --body-file <path>");
            var title = parts[1];
            var body = ResolveBacklogTextFile(parts, "--body-file", inlineIndex: 2, defaultValue: "") ?? "";
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
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var reason = ResolveBacklogTextFile(parts, "--reason-file", inlineIndex: 2, defaultValue: null);
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

private static string? ResolveBacklogTextFile(IReadOnlyList<string> parts, string flag, int inlineIndex, string? defaultValue)
{
    if (parts.Any(part => part.Equals(flag, StringComparison.OrdinalIgnoreCase)))
    {
        var path = GetFlagValue(parts, flag)
            ?? throw new ArgumentException($"{flag} requires <path>.");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{flag} not found: {path}");
        }

        return File.ReadAllText(path, System.Text.Encoding.UTF8);
    }

    return parts.Count > inlineIndex ? parts[inlineIndex] : defaultValue;
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
