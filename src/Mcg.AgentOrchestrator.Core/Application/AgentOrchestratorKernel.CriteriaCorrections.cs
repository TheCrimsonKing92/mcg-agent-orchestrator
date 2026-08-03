namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const string CriteriaCorrectionActor = "operator";

    public EffectiveAcceptanceCriteriaCorrection WaiveAcceptanceCriterion(
        GoalId goalId,
        string criterionReference,
        string reason,
        string actor = CriteriaCorrectionActor)
    {
        var goal = GetGoal(goalId);
        if (goal.Status == GoalStatus.Draft || goal.IsTerminal)
        {
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value[..8]}' is {goal.Status}; acceptance criteria can only be waived on an in-flight goal.");
        }

        var spec = goal.RefinedSpec
            ?? throw new InvalidOperationException($"Goal '{goal.Id.Value[..8]}' has no refined acceptance criteria to waive.");
        var criterion = ResolveAcceptanceCriterion(spec.AcceptanceCriteria, criterionReference);
        var normalizedReason = RequireWaiverText(reason, nameof(reason));
        var normalizedActor = RequireWaiverText(actor, nameof(actor)).ReplaceLineEndings(" ");
        var waiver = EffectiveAcceptanceCriteriaCorrection.Waiver(
            criterion,
            normalizedReason,
            normalizedActor,
            _clock.UtcNow);

        goal.AddEffectiveAcceptanceCriteriaCorrection(waiver);
        Append(
            goal,
            null,
            ProgressKind.GoalPolicyDecision,
            $"Acceptance criterion waived by {normalizedActor}: {criterion}; reason: {normalizedReason}");
        _eventWriter.AppendAcceptanceCriterionWaived(
            goal.Id,
            criterion,
            normalizedActor,
            waiver.RecordedAt,
            normalizedReason);
        return waiver;
    }

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

    private static string ResolveAcceptanceCriterion(
        IReadOnlyList<string> criteria,
        string criterionReference)
    {
        var reference = RequireWaiverText(criterionReference, nameof(criterionReference));
        if (criteria.Count == 0)
        {
            throw new InvalidOperationException("The refined specification has no acceptance criteria to waive.");
        }

        var numericReference = reference.StartsWith("criterion ", StringComparison.OrdinalIgnoreCase)
            ? reference["criterion ".Length..].Trim()
            : reference.TrimStart('#');
        if (int.TryParse(
                numericReference,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ordinal))
        {
            if (ordinal < 1 || ordinal > criteria.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(criterionReference),
                    $"Acceptance criterion number must be between 1 and {criteria.Count}.");
            }

            return criteria[ordinal - 1];
        }

        return criteria.FirstOrDefault(criterion => string.Equals(criterion, reference, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Acceptance criterion '{reference}' was not found. Use its 1-based number or exact text.");
    }

    private static string RequireWaiverText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim();
    }
}
