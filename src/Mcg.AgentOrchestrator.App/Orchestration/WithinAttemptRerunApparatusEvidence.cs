using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WithinAttemptRerunApparatusEvidence
{
    internal const string EvidenceKind = AcceptanceWithinAttemptRerunEvidence.Reason;

    internal static bool IsExplainedIdentitylessCheck(AcceptanceCheckResult check) =>
        !check.Passed &&
        check.WithinAttemptRerun is { ExecutedTestCount: > 0, RerunTestResultPaths.Count: > 0 } evidence &&
        evidence.PartitionId.Length > 0 &&
        (evidence.FailedPredicate is AcceptanceShardCompletionPredicates.MissingTrx or
            AcceptanceShardCompletionPredicates.MalformedTrx) &&
        check.CompletionDecision?.FailedPredicate == evidence.FailedPredicate &&
        check.FailingTestIdentities?.Any(identity => !string.IsNullOrWhiteSpace(identity)) != true;
}
