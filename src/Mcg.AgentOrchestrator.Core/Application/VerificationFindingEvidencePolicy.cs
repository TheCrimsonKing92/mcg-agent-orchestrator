namespace Mcg.AgentOrchestrator.Core;

// Finding deferral requires authoritative ownership; worker prose cannot grant it.
// Candidate context comes from the dispatch that owns the verification record.
internal static class VerificationFindingEvidencePolicy
{
    internal static string FormatCriterionIndex(int criterionIndex) =>
        $"criterion_index={criterionIndex} (criterion {criterionIndex + 1})";

    internal static bool IsWorkerOwnedCriterionObligation(Goal goal, int criterionIndex)
    {
        var version = goal.AuthoritativeRefinedSpecVersion?.Version;
        var matches = version is null
            ? []
            : goal.CriterionEvidenceObligations.Where(item =>
                item.CriterionIndex == criterionIndex && item.CriterionVersion == version.Value).ToArray();
        var obligation = matches.Length == 1 ? matches[0] : null;
        // A legacy/malformed record has no authoritative ownership; do not
        // convert it into a deferred obligation based on worker prose.
        return matches.Length != 1 || obligation is null || obligation.Owner is CriterionEvidenceOwner.Worker or CriterionEvidenceOwner.Unknown;
    }

    internal static CriterionEvidenceObligation? FindAuthoritativelyDeferredAcceptanceObligation(Goal goal, ReviewFinding finding)
    {
        if (finding.Category != FindingCategory.AcceptanceOwned ||
            string.IsNullOrWhiteSpace(finding.StableId))
        {
            return null;
        }

        // A worker's category is only a proposal. Deferral requires one live,
        // explicit operator/refinement binding to the exact stable finding id.
        var currentVersion = goal.AuthoritativeRefinedSpecVersion?.Version;
        var matches = goal.CriterionEvidenceObligations.Where(item =>
            item.Owner == CriterionEvidenceOwner.Acceptance &&
            item.HasValidEvidenceState &&
            item.CriterionVersion == currentVersion &&
            string.Equals(item.FindingStableId, finding.StableId, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool HasOnlyAuthoritativelyDeferredAcceptanceFindings(
        Goal goal,
        TaskVerificationRecord verification)
    {
        if (WorkerResultBlockers.TryFindCriteriaVerdicts(verification, out var verdicts, out _) &&
            verdicts.Any(item => item.Verdict == "not-met" && goal.RefinedSpec is { } spec &&
                item.CriterionIndex >= 0 && item.CriterionIndex < spec.AcceptanceCriteria.Count &&
                !goal.EffectiveAcceptanceCriteriaCorrections.Any(correction => correction.IsWaiver &&
                    string.Equals(correction.SupersededCriterion, spec.AcceptanceCriteria[item.CriterionIndex].Trim(), StringComparison.OrdinalIgnoreCase))))
            return false;
        var openFindings = ReviewFindings.GetOpenBlockingFindings(
            verification.MergedReviewFindings ?? [],
            goal.EffectiveAcceptanceCriteriaCorrections);
        return openFindings.Count > 0 && openFindings.All(finding =>
            FindAuthoritativelyDeferredAcceptanceObligation(goal, finding) is not null);
    }
}
