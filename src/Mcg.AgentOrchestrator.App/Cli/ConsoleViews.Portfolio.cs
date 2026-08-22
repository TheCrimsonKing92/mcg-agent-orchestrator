using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintEpicRollups(IReadOnlyList<PortfolioEpicRollup> rollups)
    {
        if (rollups.Count == 0)
        {
            Console.WriteLine("No epics.");
            return;
        }

        foreach (var row in rollups)
        {
            var project = row.Project is null ? "unassigned" : $"{row.Project.Title} ({ShortId(row.Project.Id)})";
            Console.WriteLine($"{row.Epic.Title} ({ShortId(row.Epic.Id)}) project={project} goals={row.GoalCount} backlog={row.BacklogItemCount} active={row.ActiveCount} verified={row.VerifiedCount} parked={row.ParkedCount} landed={row.LandedCount} newest={FormatTimestamp(row.NewestTransitionAt)}");
        }
    }

    public static void PrintEpicMembers(PortfolioEpic epic, IReadOnlyList<PortfolioEpicMember> members)
    {
        if (members.Count == 0)
        {
            Console.WriteLine($"Epic {epic.Title} ({ShortId(epic.Id)}) has no members.");
            return;
        }

        Console.WriteLine($"Epic {epic.Title} ({ShortId(epic.Id)}) members:");
        foreach (var member in members)
            Console.WriteLine($"  {member.Kind}: {member.MemberId}");
    }

    public static void PrintPortfolio(IReadOnlyList<PortfolioEpicRollup> rollups, IReadOnlyList<PortfolioGoalRow> rows)
    {
        if (rollups.Count == 0)
        {
            Console.WriteLine("Portfolio: no epics.");
            return;
        }

        foreach (var projectGroup in rollups.GroupBy(row => row.Project?.Id ?? "", StringComparer.Ordinal))
        {
            var project = projectGroup.First().Project;
            Console.WriteLine(project is null ? "Project: unassigned" : $"Project: {project.Title} ({ShortId(project.Id)})");
            foreach (var epic in projectGroup.OrderBy(row => row.Epic.Title, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  Epic: {epic.Epic.Title} ({ShortId(epic.Epic.Id)}) goals={epic.GoalCount} active={epic.ActiveCount} verified={epic.VerifiedCount} parked={epic.ParkedCount} landed={epic.LandedCount} newest={FormatTimestamp(epic.NewestTransitionAt)}");
                foreach (var goalRow in rows.Where(row => row.Epic.Id == epic.Epic.Id))
                {
                    Console.WriteLine($"    - {ShortId(goalRow.Goal.Id.Value)} [{goalRow.Goal.Status}] newest={FormatTimestamp(goalRow.NewestTransitionAt)} {OutputTextPreview.CreateSummary(goalRow.Goal.Objective).Text}");
                }
            }
        }
    }

    public static void PrintClusterSuggestions(IReadOnlyList<PortfolioClusterSuggestion> suggestions)
    {
        if (suggestions.Count == 0)
        {
            Console.WriteLine("No cluster suggestions.");
            return;
        }

        foreach (var suggestion in suggestions)
        {
            var goals = suggestion.GoalIds.Count == 0 ? "none" : string.Join(",", suggestion.GoalIds.Select(ShortId));
            var backlog = suggestion.BacklogItemIds.Count == 0 ? "none" : string.Join(",", suggestion.BacklogItemIds.Select(ShortId));
            Console.WriteLine($"{ShortId(suggestion.Id)} signal={suggestion.Signal} goals={goals} backlog={backlog} title={suggestion.Title}");
            Console.WriteLine($"  evidence: {suggestion.Evidence}");
        }
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value is null ? "none" : value.Value.ToString("O");
}
