using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecutePortfolioCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "project-add":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "project-add <title> | project-add --text-file <path>");
            var title = ResolveTextArgumentOrDefault(parts, inlineIndex: 1, defaultValue: null, "--text-file")
                ?? throw new ArgumentException("Usage: project-add <title> | project-add --text-file <path>");
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var project = store.AddProjectAsync(title).GetAwaiter().GetResult();
            Console.WriteLine($"Added project: [{project.Id}] {project.Title}");
            return false;
        }

        case "epic-add":
        {
            var titleFlagCount = parts.Count(part => part.Equals("--title", StringComparison.OrdinalIgnoreCase));
            var hasPositionalTitle = parts.Count > 1 && !parts[1].StartsWith("--", StringComparison.Ordinal);
            if (titleFlagCount > 1)
                throw new ArgumentException("Provide --title only once.");
            if (titleFlagCount == 1 && hasPositionalTitle)
                throw new ArgumentException("Provide the epic title either positionally or with --title, not both.");

            var flaggedTitle = titleFlagCount == 1 ? GetFlagValue(parts, "--title") : null;
            if (titleFlagCount == 1 &&
                (string.IsNullOrWhiteSpace(flaggedTitle) || flaggedTitle.StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException("--title requires a non-empty title.");

            string title;
            string? description = null;
            if (titleFlagCount == 0 && !hasPositionalTitle)
            {
                title = ResolveTextArgumentOrDefault(parts, 1, null, "--text-file")?.Trim()
                    ?? throw new ArgumentException(CliCommandHelp.EpicAddUsage);
                if (title.Length > 200 || title.Contains('\r') || title.Contains('\n'))
                    throw new ArgumentException("For a multi-line or long epic description, provide --title <title> with --text-file <path>.");
            }
            else
            {
                var bodyParts = titleFlagCount == 1 ? RemoveFlagWithValue(parts, "--title") : parts;
                if (titleFlagCount == 1 && bodyParts.Count > 1 && !bodyParts[1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("When using --title, provide the description with --text-file or --body-file.");
                title = flaggedTitle ?? parts[1];
                description = ResolveTextArgumentOrDefault(bodyParts, titleFlagCount == 1 ? 1 : 2, null, "--body-file", "--text-file");
            }
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.AddEpicAsync(title, description: description).GetAwaiter().GetResult();
            Console.WriteLine($"Added epic: [{epic.Id}] {epic.Title}");
            return false;
        }

        case "project-assign":
        {
            CliArgumentParser.RequirePartCount(parts, 3, "project-assign <epic> <project>");
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.ResolveEpicAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{parts[1]}' was not found.");
            var project = store.ResolveProjectAsync(parts[2]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Project '{parts[2]}' was not found.");
            store.AssignEpicToProjectAsync(epic.Id, project.Id).GetAwaiter().GetResult();
            Console.WriteLine($"Assigned epic {epic.Id[..8]} to project {project.Id[..8]}");
            return false;
        }

        case "epic-assign":
        {
            CliArgumentParser.RequirePartCount(parts, 3, "epic-assign <goal-or-backlog-id> <epic>");
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.ResolveEpicAsync(parts[2]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{parts[2]}' was not found.");
            var assignment = ResolvePortfolioAssignmentTarget(context, parts[1]);
            if (assignment.Goal is not null)
            {
                store.AssignGoalToEpicAsync(assignment.Goal.Id.Value, epic.Id).GetAwaiter().GetResult();
                Console.WriteLine($"Assigned goal {assignment.Goal.Id.Value[..8]} to epic {epic.Id[..8]}");
            }
            else
            {
                store.AssignBacklogItemToEpicAsync(assignment.BacklogItem!.Id, epic.Id).GetAwaiter().GetResult();
                Console.WriteLine($"Assigned backlog {assignment.BacklogItem!.Id[..8]} to epic {epic.Id[..8]}");
            }
            return false;
        }

        case "epic-list":
        {
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            ConsoleViews.PrintEpicRollups(store.BuildEpicRollupsAsync(context.Kernel.Goals).GetAwaiter().GetResult());
            return false;
        }

        case "epic-members":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "epic-members <epic>");
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.ResolveEpicAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{parts[1]}' was not found.");
            var members = store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult();
            ConsoleViews.PrintEpicMembers(epic, members);
            return false;
        }

        case "epic-show":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "epic-show <epic>");
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.ResolveEpicAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{parts[1]}' was not found.");
            var members = store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult();
            var rollup = store.BuildEpicRollupsAsync(context.Kernel.Goals).GetAwaiter().GetResult()
                .Single(row => row.Epic.Id == epic.Id);
            ConsoleViews.PrintEpicShow(rollup, members);
            return false;
        }

        case "epic-rename":
        case "epic-describe":
        {
            var rename = command == "epic-rename";
            CliArgumentParser.RequirePartCount(parts, 3, rename ? "epic-rename <epic> <new-title>" : "epic-describe <epic> <text> | epic-describe <epic> --text-file <path>");
            var text = ResolveTextArgumentOrDefault(parts, 2, null, rename ? [] : ["--text-file"])
                ?? throw new ArgumentException(rename ? CliCommandHelp.EpicRenameUsage : CliCommandHelp.EpicDescribeUsage);
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.ResolveEpicAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{parts[1]}' was not found.");
            var updated = store.UpdateEpicAsync(epic.Id, title: rename ? text : null, description: rename ? null : text)
                .GetAwaiter().GetResult();
            Console.WriteLine(rename ? $"Renamed epic: [{updated.Id}] {updated.Title}" : $"Updated epic description: [{updated.Id}] {updated.Title}");
            return false;
        }

        case "portfolio":
        case "portfolio-view":
        {
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var rollups = store.BuildEpicRollupsAsync(context.Kernel.Goals).GetAwaiter().GetResult();
            var rows = store.BuildPortfolioGoalRowsAsync(context.Kernel.Goals).GetAwaiter().GetResult();
            ConsoleViews.PrintPortfolio(rollups, rows);
            return false;
        }

        case "epic-suggest":
        {
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var backlog = new BacklogStore(context.Workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            var suggestions = PortfolioClusterer.BuildSuggestions(context.Kernel.Goals, backlog);
            store.ReplaceSuggestionsAsync(suggestions).GetAwaiter().GetResult();
            ConsoleViews.PrintClusterSuggestions(suggestions);
            return false;
        }

        case "epic-suggestions":
        {
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            ConsoleViews.PrintClusterSuggestions(store.ListSuggestionsAsync().GetAwaiter().GetResult());
            return false;
        }

        default:
            return null;
    }
}

private static PortfolioAssignmentTarget ResolvePortfolioAssignmentTarget(CliExecutionContext context, string idOrPrefix)
{
    var goalMatches = context.Kernel.Goals
        .Where(goal => goal.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var backlogStore = new BacklogStore(context.Workspace.BacklogStorePath);
    var backlog = backlogStore.GetByIdPrefixAsync(idOrPrefix).GetAwaiter().GetResult();

    if (goalMatches.Length > 1)
        throw new InvalidOperationException($"Goal prefix '{idOrPrefix}' is ambiguous.");
    if (goalMatches.Length == 1 && backlog is not null)
        throw new InvalidOperationException($"Portfolio assignment target '{idOrPrefix}' matches both a goal and a backlog item.");
    if (goalMatches.Length == 1)
        return new PortfolioAssignmentTarget(goalMatches[0], null);
    if (backlog is not null)
        return new PortfolioAssignmentTarget(null, backlog);
    throw new KeyNotFoundException($"Goal or backlog item '{idOrPrefix}' was not found.");
}

private sealed record PortfolioAssignmentTarget(Goal? Goal, BacklogItem? BacklogItem);
}
