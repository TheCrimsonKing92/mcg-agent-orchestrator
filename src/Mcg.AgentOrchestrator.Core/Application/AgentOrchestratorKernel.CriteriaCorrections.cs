namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const string CriteriaCorrectionActor = "operator";

    public EffectiveAcceptanceCriteriaCorrection WaiveAcceptanceCriterion(
        GoalId goalId,
        string criterionReference,
        string reason,
        string actor = CriteriaCorrectionActor,
        IReadOnlyList<CriterionDispositionRequest>? dispositions = null)
    {
        var goal = GetGoal(goalId);
        if (goal.Status == GoalStatus.Draft || goal.IsTerminal)
        {
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value[..8]}' is {goal.Status}; acceptance criteria can only be waived on an in-flight goal.");
        }

        var spec = goal.RefinedSpec
            ?? throw new InvalidOperationException($"Goal '{goal.Id.Value[..8]}' has no refined acceptance criteria to waive.");
        var criterion = ResolveAcceptanceCriterion(spec.AcceptanceCriteria, criterionReference).Trim();
        var normalizedReason = NormalizeWaiverLine(reason, nameof(reason));
        var normalizedActor = NormalizeWaiverLine(actor, nameof(actor));
        var affectedCriteria = SelectAffectedCriteria(goal, criterion);
        var resolvedDispositions = ResolveCriterionDispositions(
            spec.AcceptanceCriteria,
            affectedCriteria,
            dispositions);
        var pendingWaiver = EffectiveAcceptanceCriteriaCorrection.Waiver(
            criterion,
            normalizedReason,
            normalizedActor,
            _clock.UtcNow,
            resolvedDispositions);
        var capturedHash = EffectiveAcceptanceCriteriaVersion.ComputeHash(
            spec,
            goal.EffectiveAcceptanceCriteriaCorrections.Append(pendingWaiver));
        var waiver = pendingWaiver with { CapturedAcceptanceCriteriaHash = capturedHash };

        if (!goal.AddEffectiveAcceptanceCriteriaCorrection(waiver))
        {
            throw new InvalidOperationException(
                $"Acceptance criterion '{criterion}' already has this waiver recorded by {normalizedActor} at {waiver.RecordedAt:u}.");
        }
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
            normalizedReason,
            capturedHash);
        return waiver;
    }

    private static IReadOnlyList<string> SelectAffectedCriteria(Goal goal, string waivedCriterion)
    {
        var waivedCriteria = goal.EffectiveAcceptanceCriteriaCorrections
            .Where(correction => correction.IsWaiver)
            .Select(correction => correction.SupersededCriterion)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return goal.OutstandingCriterionEvidenceObligations
            .Where(obligation => !string.Equals(
                obligation.Criterion.Trim(),
                waivedCriterion,
                StringComparison.OrdinalIgnoreCase))
            .Where(obligation => !waivedCriteria.Contains(obligation.Criterion.Trim()))
            .OrderBy(obligation => obligation.CriterionIndex)
            .Select(obligation => obligation.Criterion.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<CriterionDisposition>? ResolveCriterionDispositions(
        IReadOnlyList<string> criteria,
        IReadOnlyList<string> affectedCriteria,
        IReadOnlyList<CriterionDispositionRequest>? requests)
    {
        var supplied = requests ?? [];
        var affected = affectedCriteria.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<CriterionDisposition>(supplied.Count);
        foreach (var request in supplied)
        {
            var criterion = ResolveAcceptanceCriterion(criteria, request.CriterionReference).Trim();
            if (!affected.Contains(criterion))
            {
                throw new InvalidOperationException(
                    $"Acceptance criterion '{criterion}' is not an affected outstanding criterion for this waiver.");
            }

            if (resolved.Any(item => string.Equals(item.Criterion, criterion, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Provide exactly one disposition for affected acceptance criterion '{criterion}'.");
            }

            resolved.Add(new CriterionDisposition(
                criterion,
                NormalizeWaiverLine(request.Disposition, nameof(request.Disposition))));
        }

        var suppliedCriteria = resolved.Select(item => item.Criterion).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = affectedCriteria.Where(criterion => !suppliedCriteria.Contains(criterion)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Waiving this criterion requires a disposition for each affected outstanding criterion: " +
                string.Join("; ", missing.Select(criterion => $"'{criterion}'")) +
                ". Supply --disposition or --disposition-file for each criterion.");
        }

        return resolved.Count == 0 ? null : resolved;
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

        return criteria.FirstOrDefault(criterion => string.Equals(criterion.Trim(), reference, StringComparison.OrdinalIgnoreCase))
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

    private static string NormalizeWaiverLine(string value, string parameterName) =>
        string.Join(' ', RequireWaiverText(value, parameterName)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
