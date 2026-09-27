namespace Mcg.AgentOrchestrator.Core;

public static partial class DispatchFailureClassifier
{
    private static bool IsDeveloperDeferredNoChangeRound(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges)
    {
        var output = verification.AuthoritativeStandardOutput;
        if (!verification.Succeeded || !workerResultPresent || hasCommittedChanges ||
            output is null ||
            task.RequiredRole != AgentRole.Developer ||
            (task.LatestRetryAt is null && task.CriterionRetryCount == 0 && task.CriterionRetryFeedback.Count == 0) ||
            task.LastDispatch is not { BaseCommit: { Length: > 0 } candidate } ||
            !DeferredNoChangeOutcome.TryParse(verification.StandardError, out var marker) ||
            !string.Equals(marker.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase) ||
            !WorkerResultBlockers.TryGetBlockersStatus(output, out var blockers) ||
            blockers != WorkerResultBlockers.BlockersStatus.None ||
            !WorkerResultBlockers.TryGetTestsStatus(output, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Deferred ||
            !WorkerResultBlockers.TryFindTests(output, out var testsField))
            return false;

        var classes = DeveloperDeferredTestClassNames.Parse(testsField);
        return classes.Count > 0 &&
            classes.Count == marker.TestClasses.Count &&
            classes.All(name => marker.TestClasses.Contains(name, StringComparer.OrdinalIgnoreCase)) &&
            output.Contains(marker.Rationale, StringComparison.OrdinalIgnoreCase);
    }
}
