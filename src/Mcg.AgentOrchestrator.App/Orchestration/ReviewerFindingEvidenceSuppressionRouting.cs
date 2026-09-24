using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ReviewerFindingEvidenceSuppressionRoute(
    string[] WritableBlockerIds,
    AgentRole? ChosenOwner,
    bool DeferToReviewRetryRoute);

internal static class ReviewerFindingEvidenceSuppressionRouting
{
    internal static ReviewFinding[] SelectAndSuppress(
        IReadOnlyList<string> writableBlockerIds,
        ReviewFinding[] requestingFindings,
        TaskSpec requestingTask,
        string? candidateSha,
        Func<FindingEvidenceRequest, string> requestIdentity,
        Action<string> recordSuppression)
    {
        var selfExempt = SelectSelfExemptRequestingFindings(
            writableBlockerIds, requestingFindings, requestingTask, candidateSha);
        foreach (var identity in requestingFindings.Except(selfExempt)
                     .Select(finding => requestIdentity(finding.EvidenceRequest!))
                     .Distinct(StringComparer.Ordinal))
        {
            recordSuppression(identity);
        }

        return selfExempt;
    }

    internal static ReviewFinding[] SelectSelfExemptRequestingFindings(
        IReadOnlyList<string> writableBlockerIds,
        IReadOnlyList<ReviewFinding> requestingFindings,
        TaskSpec requestingTask,
        string? candidateSha) =>
        requestingFindings.Where(finding =>
            finding.State == ReviewFindingState.Open &&
            finding.EvidenceRequest is { Selections.Count: > 0 } &&
            !string.IsNullOrWhiteSpace(candidateSha) &&
            string.Equals(requestingTask.LastVerification?.ReviewedCommit, candidateSha,
                StringComparison.OrdinalIgnoreCase) &&
            FindingEvidenceExecutionClassifier.Classify(requestingTask, finding, candidateSha) ==
                FindingEvidenceExecutionState.PendingExecution &&
            writableBlockerIds.All(id => string.Equals(id, finding.StableId, StringComparison.Ordinal)))
            .ToArray();

    internal static ReviewerFindingEvidenceSuppressionRoute Resolve(
        TaskVerificationRecord? verification,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        IReadOnlyList<ReviewFinding> openBlockingFindings)
    {
        WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
            verification,
            criteriaCorrections,
            out var blockerProse,
            out _);
        if (string.IsNullOrWhiteSpace(blockerProse))
        {
            blockerProse = string.Join("; ", openBlockingFindings.Select(finding => finding.Description));
        }

        var route = ReviewFindingRouting.Resolve(openBlockingFindings, blockerProse);
        var allUseLegacyOwnership = openBlockingFindings.Count > 0 &&
            openBlockingFindings.All(finding => finding.Category == FindingCategory.Unspecified);
        if (allUseLegacyOwnership &&
            (route.EscalateToOperator || route.TargetRole != AgentRole.Developer))
        {
            return new ReviewerFindingEvidenceSuppressionRoute([], route.TargetRole, true);
        }

        var writableBlockerIds = route.TargetRole == AgentRole.Developer
            ? ReviewFindingRouting.Project(openBlockingFindings, blockerProse)
                .Where(projection => projection.TargetRole == route.TargetRole)
                .Select(projection => projection.Finding.StableId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray()
            : [];
        return new ReviewerFindingEvidenceSuppressionRoute(
            writableBlockerIds,
            route.TargetRole,
            false);
    }
}
