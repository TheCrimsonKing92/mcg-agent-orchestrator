using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Attention only changes order within the existing body/footer partitions.
internal sealed class OwnerConsoleBoardSectionOrder(ImmutableArray<OwnerConsoleDecision> decisions)
{
    private readonly ILookup<string, OwnerConsoleDecision> _goalDecisions = decisions
        .Where(decision => decision.Kind != OwnerQuestionKind.Onboarding)
        .ToLookup(decision => decision.GoalId, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _questionProjects = decisions
        .Where(decision => decision.Kind == OwnerQuestionKind.Onboarding)
        .Select(decision => OnboardingQuestionSource.TryIdentify(decision.Id, out var project, out _, out var stale)
            && !stale ? project : null)
        .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal int Tier(IEnumerable<OwnerConsoleBoardRow> rows) =>
        rows.Any(row => _questionProjects.Contains(row.Project)) || DecisionCount(rows) > 0 ? 0 :
        rows.Any(row => row.IsDiagnostic && row.State == "onboarding") ? 1 : 2;

    internal int DecisionCount(IEnumerable<OwnerConsoleBoardRow> rows) => rows
        .Where(row => !row.IsForeign && !row.IsDiagnostic && !row.Dimmed)
        .Sum(row => _goalDecisions[row.GoalId].Count());
}
