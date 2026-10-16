namespace Mcg.AgentOrchestrator.Core;

// A review binds the next gate's candidate; only gate receipts supply evidence.
internal static class ReviewedCandidateEvidenceBinding
{
    internal static CriterionEvidenceObligation Apply(
        CriterionEvidenceObligation obligation, string reviewedCommit, DateTimeOffset recordedAt)
    {
        if (obligation.Owner != CriterionEvidenceOwner.Acceptance ||
            obligation.RequiredScope != CriterionEvidenceScopes.FullAcceptanceGate ||
            obligation.State == CriterionEvidenceState.Repaired)
            return obligation;

        if (obligation.State == CriterionEvidenceState.Pending)
            return obligation with { ExpectedCandidateSha = reviewedCommit, RecordedAt = recordedAt };

        if (obligation.State is CriterionEvidenceState.Satisfied or CriterionEvidenceState.Failed &&
            string.Equals(obligation.CandidateSha, reviewedCommit, StringComparison.OrdinalIgnoreCase))
            return obligation;

        return obligation with
        {
            State = CriterionEvidenceState.Pending,
            CandidateSha = null,
            ReceiptId = null,
            Detail = null,
            PriorReceipts = obligation.ArchiveCurrentReceipt(),
            ExpectedCandidateSha = reviewedCommit,
            RecordedAt = recordedAt
        };
    }

    internal static void BindReviewerAttestations(
        Goal goal, TaskSpec task, TaskVerificationRecord verification, DateTimeOffset recordedAt)
    {
        if (task.RequiredRole != AgentRole.Reviewer || !verification.WorkerResultPresent ||
            !verification.Succeeded || verification.ProviderFailureKind != ProviderFailureKind.Unknown ||
            DispatchFailureClassifier.IsProviderInterruptionFailure(verification) ||
            verification.ReviewFindingContractViolation is not null ||
            string.IsNullOrWhiteSpace(verification.ReviewedCommit) ||
            goal.RefinedSpec is not { AcceptanceCriteria.Count: > 0 } spec ||
            goal.AuthoritativeRefinedSpecVersion is not { } version ||
            !WorkerResultBlockers.TryFindCriteriaVerdicts(verification, out var verdicts, out _))
            return;

        // Match the attestation path's coverage and waiver rules before changing authority.
        var registered = verdicts.Where(item => item.CriterionIndex < spec.AcceptanceCriteria.Count).ToArray();
        if (!registered.Select(item => item.CriterionIndex).OrderBy(index => index)
                .SequenceEqual(Enumerable.Range(0, spec.AcceptanceCriteria.Count)))
            return;

        foreach (var verdict in registered.Where(item => item.Verdict is "not-verifiable" or "met"))
        {
            if (goal.EffectiveAcceptanceCriteriaCorrections.Any(correction => correction.IsWaiver &&
                    string.Equals(correction.SupersededCriterion, spec.AcceptanceCriteria[verdict.CriterionIndex].Trim(),
                        StringComparison.OrdinalIgnoreCase)) ||
                VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(goal, verdict.CriterionIndex))
                continue;

            var matches = goal.CriterionEvidenceObligations.Where(item =>
                item.CriterionIndex == verdict.CriterionIndex && item.CriterionVersion == version.Version).ToArray();
            if (matches.Length == 1)
                goal.BindDeferredCriterionObligation(verdict.CriterionIndex, version.Version, verification.ReviewedCommit, recordedAt);
        }
    }
}
