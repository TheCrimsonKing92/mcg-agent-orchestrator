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

    public static void PrintGoalScopeCollisionReport(
        GoalScopeCollisionReport report,
        string? intakeItemId = null,
        string? heading = null)
    {
        var subject = string.IsNullOrWhiteSpace(intakeItemId) && string.IsNullOrWhiteSpace(heading)
            ? string.Empty
            : $" item={intakeItemId ?? "unknown"} heading={JsonSerializer.Serialize(heading ?? string.Empty)}";
        Console.WriteLine(
            $"Scope collision advisory:{subject} {report.VerdictToken} conflicts={report.Collisions.Count} " +
            $"inputGoals={report.InputGoalCount} eligibleGoals={report.EligibleGoalCount} " +
            $"comparedGoals={report.ComparedGoalCount} uncheckableGoals={report.UncheckableGoalCount} " +
            $"uncomparedGoals={report.UncomparedGoalCount} explicitConflicts={report.ExplicitCollisionCount}");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            intakeItemId,
            heading,
            verdict = report.VerdictToken,
            report.InputGoalCount,
            report.EligibleGoalCount,
            report.ComparedGoalCount,
            report.UncheckableGoalCount,
            report.UncomparedGoalCount,
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
