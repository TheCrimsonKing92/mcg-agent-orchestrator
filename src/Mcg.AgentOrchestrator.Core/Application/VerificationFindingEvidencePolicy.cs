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

    internal static CriterionEvidenceObligation? FindAuthoritativelyDeferredOffWorkerObligation(
        Goal goal, ReviewFinding finding, string? reviewedCommit)
    {
        var owner = finding.Category switch
        {
            FindingCategory.AcceptanceOwned => CriterionEvidenceOwner.Acceptance,
            FindingCategory.OperatorOwned => CriterionEvidenceOwner.Operator,
            _ => CriterionEvidenceOwner.Unknown
        };
        if (owner == CriterionEvidenceOwner.Unknown ||
            string.IsNullOrWhiteSpace(finding.StableId) || string.IsNullOrWhiteSpace(reviewedCommit))
        {
            return null;
        }

        // A worker's category is only a proposal. Deferral requires one live,
        // explicit operator/refinement binding to the exact stable finding id.
        var currentVersion = goal.AuthoritativeRefinedSpecVersion?.Version;
        var matches = goal.CriterionEvidenceObligations.Where(item =>
            item.CriterionVersion == currentVersion &&
            item.State != CriterionEvidenceState.Repaired &&
            string.Equals(item.FindingStableId, finding.StableId, StringComparison.Ordinal)).ToArray();
        // Check ambiguity before filtering owners or validity. Restore retains
        // duplicate/malformed claims as Unknown records for the same criterion.
        if (matches.Length != 1) return null;
        var obligation = matches[0];
        return obligation.Owner == owner && obligation.HasValidEvidenceState &&
            goal.CriterionEvidenceObligations.Count(item =>
                item.CriterionVersion == currentVersion && item.CriterionIndex == obligation.CriterionIndex &&
                item.State != CriterionEvidenceState.Repaired) == 1 &&
            !string.IsNullOrWhiteSpace(obligation.ExpectedCandidateSha) &&
            string.Equals(obligation.ExpectedCandidateSha, reviewedCommit, StringComparison.OrdinalIgnoreCase)
                ? obligation : null;
    }

    internal static bool HasOnlyAuthoritativelyDeferredOffWorkerFindings(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (task.RequiredRole is not (AgentRole.Tester or AgentRole.Reviewer) ||
            !verification.WorkerResultPresent || !verification.Succeeded ||
            verification.ProviderFailureKind != ProviderFailureKind.Unknown ||
            DispatchFailureClassifier.IsProviderInterruptionFailure(verification)) return false;
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
            FindAuthoritativelyDeferredOffWorkerObligation(goal, finding, verification.ReviewedCommit) is not null);
    }
}
