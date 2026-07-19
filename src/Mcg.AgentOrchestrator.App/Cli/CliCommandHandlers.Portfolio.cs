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
            CliArgumentParser.RequirePartCount(parts, 2, "epic-add <title> | epic-add --text-file <path>");
            var title = ResolveTextArgumentOrDefault(parts, inlineIndex: 1, defaultValue: null, "--text-file")
                ?? throw new ArgumentException("Usage: epic-add <title> | epic-add --text-file <path>");
            var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
            var epic = store.AddEpicAsync(title).GetAwaiter().GetResult();
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
