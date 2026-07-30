using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    private static readonly JsonSerializerOptions GoalScopeCollisionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void PrintGoalScopeCollisionReport(GoalScopeCollisionReport report)
    {
        Console.WriteLine(
            $"Scope collision advisory: {report.VerdictToken} conflicts={report.Collisions.Count} " +
            $"comparedGoals={report.ComparedGoalCount} explicitConflicts={report.ExplicitCollisionCount}");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            verdict = report.VerdictToken,
            report.ComparedGoalCount,
            report.ExplicitCollisionCount,
            report.ConflictingGoalIds,
            collisions = report.Collisions.Select(collision => new
            {
                collision.GoalId,
                collision.GoalPrefix,
                collision.GoalStatus,
                kind = collision.Kind.ToString(),
                collision.ProposedPath,
                proposedProvenance = collision.ProposedProvenance.ToString(),
                collision.ConflictingPath,
                conflictingProvenance = collision.ConflictingProvenance.ToString()
            }),
            evidenceGaps = report.EvidenceGaps.Select(gap => new
            {
                gap.GoalId,
                gap = gap.Gap.ToString(),
                gap.Message
            }),
            proposedScopes = report.ProposedScopes.Select(scope => new
            {
                scope.Path,
                provenance = scope.Provenance.ToString()
            })
        }, GoalScopeCollisionJsonOptions));
    }
}
