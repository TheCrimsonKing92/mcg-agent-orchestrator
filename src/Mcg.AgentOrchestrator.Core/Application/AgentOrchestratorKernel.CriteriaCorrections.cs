namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const string CriteriaCorrectionActor = "operator";

    private void RecordEffectiveAcceptanceCriteriaCorrections(
        Goal goal,
        TaskId? taskId,
        ProgressKind sourceKind,
        string message)
    {
        var corrections = EffectiveAcceptanceCriteriaCorrectionParser.Parse(
            message,
            CriteriaCorrectionActor,
            _clock.UtcNow,
            taskId,
            sourceKind);

        foreach (var correction in corrections)
        {
            goal.AddEffectiveAcceptanceCriteriaCorrection(correction);
        }
    }

    private bool TrySuppressSupersededReviewerBlocker(
        Goal goal,
        TaskSpec task,
        string blocker,
        out string effectiveBlocker)
    {
        effectiveBlocker = blocker;
        if (!TryFilterSupersededReviewerBlocker(goal, blocker, out effectiveBlocker, out var suppressed))
        {
            return false;
        }

        foreach (var item in suppressed)
        {
            Append(
                goal,
                task.Id,
                ProgressKind.TaskNote,
                $"Suppressed Reviewer blocker matching operator criteria correction: {item.Finding}; superseded criterion: {item.Correction.SupersededCriterion}; correction recorded {item.Correction.RecordedAt:u} by {item.Correction.Actor}.");
        }

        return true;
    }

    private static bool TryGetUnsuppressedReviewerBlocker(Goal goal, string blocker, out string effectiveBlocker)
    {
        if (!TryFilterSupersededReviewerBlocker(goal, blocker, out effectiveBlocker, out _))
        {
            effectiveBlocker = blocker;
        }

        return !string.IsNullOrWhiteSpace(effectiveBlocker);
    }

    private static bool TryFilterSupersededReviewerBlocker(
        Goal goal,
        string blocker,
        out string effectiveBlocker,
        out IReadOnlyList<(string Finding, EffectiveAcceptanceCriteriaCorrection Correction)> suppressed)
    {
        effectiveBlocker = blocker;
        suppressed = [];
        if (goal.EffectiveAcceptanceCriteriaCorrections.Count == 0)
        {
            return false;
        }

        var findings = SplitReviewerBlockerFindings(blocker);
        if (!ReviewFindings.TryFilterWaivedDescriptions(
            findings,
            goal.EffectiveAcceptanceCriteriaCorrections,
            out var kept,
            out var suppressedItems))
        {
            return false;
        }

        suppressed = suppressedItems;
        effectiveBlocker = string.Join("; ", kept);
        return true;
    }

    private static IReadOnlyList<string> SplitReviewerBlockerFindings(string blocker)
    {
        var findings = blocker
            .Replace("\r\n", "\n")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.TrimStart('-', '*', ' '))
            .Where(item => item.Length > 0)
            .ToList();

        return findings.Count == 0 ? [blocker.Trim()] : findings;
    }
}
