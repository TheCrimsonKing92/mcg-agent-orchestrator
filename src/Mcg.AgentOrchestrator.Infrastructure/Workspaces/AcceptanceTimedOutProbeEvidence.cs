using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTimedOutProbeEvidence
{
    internal static AcceptanceWithinAttemptRerunEvidence? TryBuild(
        string partitionId,
        AcceptanceCheckResult firstRun,
        AcceptanceCheckResult probe,
        string originalInvocationId,
        string retryInvocationId)
    {
        if (firstRun.CompletionDecision?.FailedPredicate != AcceptanceShardCompletionPredicates.TimedOut ||
            probe.TestResultPaths is not { Count: > 0 } paths || !paths.All(File.Exists))
            return null;

        return new AcceptanceWithinAttemptRerunEvidence(
            partitionId,
            AcceptanceShardCompletionPredicates.TimedOut,
            originalInvocationId,
            retryInvocationId,
            probe.CompletionDecision?.ExecutedTestCount ?? probe.ExecutedTestCount ?? 0,
            paths,
            probe.Passed,
            probe.FailingTestIdentities?.Where(identity => !string.IsNullOrWhiteSpace(identity)).ToArray() ?? []);
    }
}
