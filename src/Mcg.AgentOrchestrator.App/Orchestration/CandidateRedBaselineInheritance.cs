using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CandidateRedBaselineInheritance
{
    internal static CandidateRedBaselineComparison Compare(
        FindingEvidenceArmReceipt candidate, FindingEvidenceArmReceipt baseline)
    {
        var candidateFailures = Normalize(candidate.FailingTestIdentities);
        var baselineFailures = Normalize(baseline.FailingTestIdentities);
        var baselineSet = baselineFailures.ToHashSet(StringComparer.Ordinal);
        var candidateOnly = candidateFailures.Where(identity => !baselineSet.Contains(identity)).ToArray();
        var verdict = baselineFailures.Count == 0
            ? CandidateRedBaselineVerdict.BaselineIdentitiesUnavailable
            : candidateOnly.Length == 0
                ? CandidateRedBaselineVerdict.Inherited
                : CandidateRedBaselineVerdict.CandidateOwn;
        return new(verdict, candidateFailures, baselineFailures, candidateOnly);
    }

    internal static string FormatFailureSets(CandidateRedBaselineComparison comparison) =>
        $"candidate_failures={FormatIdentities(comparison.CandidateFailures)}; " +
        $"baseline_failures={FormatIdentities(comparison.BaselineFailures)}; " +
        $"candidate_only_failures={FormatIdentities(comparison.CandidateOnlyFailures)}";

    internal static string FormatIdentities(IReadOnlyList<string> identities) =>
        identities.Count == 0 ? "none" : string.Join(',', identities);

    internal static (IReadOnlyList<string>? Failures, IReadOnlyList<string>? BaselineFailures)
        AttributableFailures(FindingEvidenceArmReceipt candidate, FindingEvidenceArmReceipt? baseline)
    {
        if (baseline is null || baseline.Disposition == FindingEvidenceArmDisposition.Green)
        {
            return (candidate.FailingTestIdentities, null);
        }
        if (baseline.Disposition == FindingEvidenceArmDisposition.Red)
        {
            var comparison = Compare(candidate, baseline);
            if (comparison.Verdict == CandidateRedBaselineVerdict.CandidateOwn)
            {
                return (comparison.CandidateOnlyFailures, comparison.BaselineFailures);
            }
        }
        return (null, null);
    }

    internal static bool TryBuildUnattributableRedEscalation(
        string candidateSha,
        IReadOnlyList<FindingEvidenceArmReceipt> arms,
        FindingEvidenceBatch batch,
        string receiptId,
        out FailedGoalFindingObservation decision)
    {
        decision = FailedGoalFindingObservation.None;
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        if (candidate is not { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red } ||
            !string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var findingIds = string.Join(',', batch.Findings.Select(finding => finding.StableId));
        if (candidate.FailingTestIdentities is not { Count: > 0 })
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"Candidate RED receipt {receiptId} at candidate {candidateSha} for finding(s) {findingIds} carried no failing test identities. No worker was dispatched.");
            return true;
        }

        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        if (baseline?.Disposition == FindingEvidenceArmDisposition.Red)
        {
            var comparison = Compare(candidate, baseline);
            var reason = comparison.Verdict switch
            {
                CandidateRedBaselineVerdict.Inherited => "is inherited from a RED Baseline arm",
                CandidateRedBaselineVerdict.BaselineIdentitiesUnavailable =>
                    "has a Baseline arm that is RED but carried no failing test identities, so inheritance cannot be decided",
                CandidateRedBaselineVerdict.CandidateOwn =>
                    "has failures absent from the RED Baseline arm and no Developer-owned finding matched them",
                _ => throw new InvalidOperationException($"Unknown candidate RED baseline verdict: {comparison.Verdict}")
            };
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"Candidate RED receipt {receiptId} at candidate {candidateSha} for finding(s) {findingIds} {reason}; " +
                $"{FormatFailureSets(comparison)}. No worker was dispatched.");
            return true;
        }
        if (baseline?.Disposition is
                FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive)
        {
            decision = ConductorDriver.BuildBaselineExecutionFailureEscalation(candidateSha, batch, receiptId);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? identities) =>
        (identities ?? []).Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Select(identity => identity.Trim()).Distinct(StringComparer.Ordinal).ToArray();
}
