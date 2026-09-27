using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WithinAttemptRerunApparatusClassifier
{
    internal const string BoundExhaustedToken = "apparatus-rerun-pass-regate-bound-exhausted";

    internal static ApparatusRedDisposition? Classify(
        AcceptanceVerificationSummary acceptance,
        int regateCount,
        int regateCap)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        var unmet = acceptance.RequiredUnmetCriteria;
        if (acceptance.Passed || unmet.Count == 0 || regateCount < 0 || regateCap < 0)
        {
            return null;
        }

        var partitions = unmet.Where(WithinAttemptRerunApparatusEvidence.IsExplainedIdentitylessCheck).ToArray();
        if (partitions.Length == 0)
        {
            return null;
        }

        var partitionNames = partitions.Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
        if (partitionNames.Count != partitions.Length ||
            unmet.Any(check => !partitionNames.Contains(check.Name) &&
                (check.Passed ||
                 check.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) == true ||
                 check.CoveredBy is not { Count: > 0 } coveredBy ||
                 !coveredBy.All(partitionNames.Contains))))
        {
            return null;
        }

        var names = partitions.Select(check => check.Name).ToArray();
        return regateCount >= regateCap
            ? new ApparatusRedDisposition.BoundExhausted(
                AcceptanceWithinAttemptRerunEvidence.Reason, names, regateCount, regateCap)
            : new ApparatusRedDisposition.Regate(
                AcceptanceWithinAttemptRerunEvidence.Reason, names, regateCount + 1, regateCap);
    }
}
