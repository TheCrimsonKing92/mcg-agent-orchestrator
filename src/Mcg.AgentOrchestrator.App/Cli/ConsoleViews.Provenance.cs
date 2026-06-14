using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintProvenanceReport(ProvenanceReportSnapshot snapshot)
    {
        Console.WriteLine();
        Console.WriteLine($"Provenance audit ({snapshot.CompletedGoalCount} completed goal(s)):");
        Console.WriteLine($"  Backed:   {snapshot.BackedGoalCount}");
        Console.WriteLine($"  Unbacked: {snapshot.UnbackedGoalCount}");
        Console.WriteLine();

        if (snapshot.Goals.Count == 0)
        {
            Console.WriteLine("  No completed goals found.");
        }
        else
        {
            foreach (var record in snapshot.Goals)
            {
                var label = record.ProvenanceStatus == ProvenanceStatus.Backed ? "BACKED  " : "UNBACKED";
                var prefix = record.GoalId.Value[..8];
                var detail = record.ProvenanceStatus == ProvenanceStatus.Unbacked
                    ? $" ({record.UnbackedTaskCount}/{record.CompletedTaskCount} tasks lack receipts)"
                    : string.Empty;
                Console.WriteLine($"  [{label}] {prefix}: {TruncateObjective(record.Objective)}{detail}");
            }
        }

        Console.WriteLine();
        if (snapshot.UnbackedDogfoodReferences.Count > 0)
        {
            Console.WriteLine($"  Unbacked dogfood references ({snapshot.UnbackedDogfoodReferences.Count}):");
            foreach (var reference in snapshot.UnbackedDogfoodReferences)
            {
                var reason = reference.IsAbsent ? "absent from state" : "goal is unbacked";
                Console.WriteLine($"    {reference.GoalIdPrefix}: {reason} (from \"{reference.ReferenceContext}\")");
            }
        }
        else
        {
            Console.WriteLine("  No unbacked dogfood references found.");
        }

        Console.WriteLine();
        if (snapshot.UnbackedCommitShas.Count > 0)
        {
            Console.WriteLine($"  Commit SHAs absent from git history ({snapshot.UnbackedCommitShas.Count}):");
            foreach (var sha in snapshot.UnbackedCommitShas)
            {
                Console.WriteLine($"    {sha}: not found in git history");
            }
        }
        else
        {
            Console.WriteLine("  No unbacked commit SHAs found.");
        }

        Console.WriteLine();
    }

    private static string TruncateObjective(string objective)
    {
        const int MaxLength = 60;
        return objective.Length <= MaxLength
            ? objective
            : objective[..MaxLength] + "...";
    }
}
