using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class VerifyingFindingCurrency
{
    internal static bool HasCommittedOutput(TaskSpec task)
    {
        if (task.LastVerification?.HasCommittedChanges is true)
        {
            return true;
        }

        var dispatch = task.LastDispatch;
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCurrent(
        Goal goal,
        TaskSpec verifyingTask,
        TaskVerificationRecord findingRecord)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(verifyingTask);
        ArgumentNullException.ThrowIfNull(findingRecord);

        if (verifyingTask.LastVerification is null)
        {
            return false;
        }

        var supersededByLaterResult = verifyingTask.VerificationHistory.Any(verification =>
            verification.CompletedAt > findingRecord.CompletedAt &&
            (verification.MergedReviewFindings is not null ||
             IsLatestCompletedResult(verifyingTask, verification)));
        if (supersededByLaterResult)
        {
            return false;
        }

        return !goal.Tasks.Any(task =>
            task.RequiredRole == AgentRole.Developer &&
            HasCommittedOutput(task) &&
            task.LastVerification is { } developerVerification &&
            developerVerification.CompletedAt > findingRecord.CompletedAt);
    }

    private static bool IsLatestCompletedResult(TaskSpec task, TaskVerificationRecord verification) =>
        task.Status == WorkTaskStatus.Completed &&
        verification.Succeeded &&
        task.LastVerification?.HasSameRoundIdentity(verification) is true;

    internal static bool HasCurrentOpenBlockingFinding(
        Goal goal,
        AgentRole role,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(goal);

        var state = goal.Tasks
            .Where(task => task.RequiredRole == role)
            .SelectMany(task => task.VerificationHistory
                .Where(verification =>
                    verification.CompletedAt <= completedAt &&
                    verification.MergedReviewFindings is not null &&
                    IsCurrent(goal, task, verification)))
            .OrderByDescending(verification => verification.CompletedAt)
            .Select(verification => verification.MergedReviewFindings!)
            .FirstOrDefault() ?? [];

        return ReviewFindings.GetOpenBlockingFindings(
            state,
            goal.EffectiveAcceptanceCriteriaCorrections).Count > 0;
    }
}
