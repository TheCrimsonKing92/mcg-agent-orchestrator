using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGoalChanges(GoalChangesReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"goal-changes {report.GoalPrefix} ({report.GoalStatus})");
        Console.WriteLine($"  {TruncateText(report.GoalObjective, 80)}");
        Console.WriteLine();

        if (report.Entries.Count == 0)
        {
            Console.WriteLine("  No file changes recorded for this goal.");
            Console.WriteLine();
            return;
        }

        var byRole = report.Entries
            .GroupBy(e => e.Role)
            .OrderBy(g => RoleOrder(g.Key));

        foreach (var roleGroup in byRole)
        {
            Console.WriteLine($"== {roleGroup.Key} ==");
            foreach (var entry in roleGroup)
            {
                var commitRange = entry.Attribution switch
                {
                    GoalChangesAttribution.Uncommitted => "(uncommitted)",
                    GoalChangesAttribution.Approximate => "(approximate — legacy dispatch)",
                    _ => $"{ShortHash(entry.BaseCommit)}..{ShortHash(entry.ResultCommit)}"
                };
                Console.WriteLine($"  task {entry.TaskId[..Math.Min(8, entry.TaskId.Length)]} {commitRange}");
                Console.WriteLine($"    {TruncateText(entry.TaskDescription, 72)}");
                if (entry.Files.Count == 0)
                {
                    Console.WriteLine("    (no files changed)");
                }
                else
                {
                    foreach (var file in entry.Files)
                        Console.WriteLine($"    {file}");
                }
                Console.WriteLine();
            }
        }
    }

    public static void PrintGoalChangesFlat(GoalChangesReport report)
    {
        var allFiles = report.Entries
            .SelectMany(e => e.Files)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var file in allFiles)
            Console.WriteLine(file);
    }

    public static void PrintGoalChangesJson(GoalChangesReport report)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(report, options));
    }

    private static string ShortHash(string? hash) =>
        hash is { Length: >= 8 } ? hash[..8] : (hash ?? "?");

    private static string TruncateText(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static int RoleOrder(AgentRole role) => role switch
    {
        AgentRole.Planner => 0,
        AgentRole.Ideation => 1,
        AgentRole.Researcher => 2,
        AgentRole.Developer => 3,
        AgentRole.Tester => 4,
        AgentRole.Reviewer => 5,
        _ => 99
    };
}
