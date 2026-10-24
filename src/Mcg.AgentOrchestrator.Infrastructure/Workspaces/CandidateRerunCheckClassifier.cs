using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class CandidateRerunCheckClassifier
{
    internal static CandidateFailureRerunResult Classify(
        string identity,
        FocusedEvidenceArmRunResult arm,
        AcceptanceCheckResult check,
        string? receipt)
    {
        if (!arm.Accepted ||
            arm.Disposition is FindingEvidenceArmDisposition.ApparatusFailure or
                FindingEvidenceArmDisposition.Inconclusive ||
            (!string.IsNullOrWhiteSpace(check.FailureClassification) &&
                !string.Equals(check.FailureClassification, AcceptanceShardCompletionPredicates.FailingTrx,
                    StringComparison.Ordinal)))
        {
            return new CandidateFailureRerunResult(null, receipt,
                check.FailureClassification ?? $"candidate rerun arm was not usable: {arm.Disposition}");
        }

        if (string.IsNullOrWhiteSpace(receipt))
        {
            return new CandidateFailureRerunResult(null, null, "candidate rerun receipt was missing");
        }

        if (check.Passed)
        {
            return new CandidateFailureRerunResult(true, receipt);
        }

        return check.FailingTestIdentities?.Contains(identity, StringComparer.Ordinal) == true
            ? new CandidateFailureRerunResult(false, receipt)
            : new CandidateFailureRerunResult(null, receipt,
                "candidate rerun did not report this identity as failing");
    }
}
