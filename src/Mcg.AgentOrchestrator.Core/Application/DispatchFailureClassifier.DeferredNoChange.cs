namespace Mcg.AgentOrchestrator.Core;

public static partial class DispatchFailureClassifier
{
    private static bool IsDeveloperDeferredNoChangeRound(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges)
    {
        if (!verification.Succeeded || !workerResultPresent || hasCommittedChanges ||
            task.RequiredRole != AgentRole.Developer ||
            (task.LatestRetryAt is null && task.CriterionRetryCount == 0 && task.CriterionRetryFeedback.Count == 0) ||
            task.LastDispatch is not { BaseCommit: { Length: > 0 } candidate } ||
            !DeferredNoChangeOutcome.TryParse(verification.StandardError, out var marker) ||
            !string.Equals(marker.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase) ||
            !WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockers) ||
            blockers != WorkerResultBlockers.BlockersStatus.None ||
            !WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Deferred ||
            !WorkerResultBlockers.TryFindTests(verification, out var testsField))
            return false;

        var classes = DeveloperDeferredTestClassNames.Parse(testsField);
        return classes.Count > 0 &&
            classes.Count == marker.TestClasses.Count &&
            classes.All(name => marker.TestClasses.Contains(name, StringComparer.OrdinalIgnoreCase)) &&
            (verification.StandardOutput.Contains(marker.Rationale, StringComparison.OrdinalIgnoreCase) ||
             verification.StandardError.Contains(marker.Rationale, StringComparison.OrdinalIgnoreCase));
    }
}
