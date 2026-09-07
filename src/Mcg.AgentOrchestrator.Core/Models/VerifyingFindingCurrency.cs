namespace Mcg.AgentOrchestrator.Core;

public enum VerifyingFindingDisposition
{
    Current,
    InvalidatedByTaskRetry,
    SupersededByLaterVerification,
    SupersededByCompletedRepair
}

public sealed record VerifyingFindingCurrencyDecision(
    VerifyingFindingDisposition Disposition,
    TaskId? RepairTaskId = null,
    string? RepairedCandidate = null);

public static class VerifyingFindingCurrency
{
    public static bool HasCommittedOutput(TaskSpec task)
    {
        ArgumentNullException.ThrowIfNull(task);
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
            !SameCandidate(dispatch.BaseCommit, dispatch.ResultCommit);
    }

    public static VerifyingFindingDisposition Classify(
        Goal goal,
        TaskSpec verifyingTask,
        TaskVerificationRecord findingRecord) =>
        Evaluate(goal, verifyingTask, findingRecord).Disposition;

    public static VerifyingFindingCurrencyDecision Evaluate(
        Goal goal,
        TaskSpec verifyingTask,
        TaskVerificationRecord findingRecord)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(verifyingTask);
        ArgumentNullException.ThrowIfNull(findingRecord);

        if (verifyingTask.LastVerification is null)
        {
            return new(VerifyingFindingDisposition.InvalidatedByTaskRetry);
        }

        var supersededByLaterResult = verifyingTask.VerificationHistory.Any(verification =>
            verification.CompletedAt > findingRecord.CompletedAt &&
            (verification.MergedReviewFindings is not null ||
             IsLatestCompletedResult(verifyingTask, verification)));
        if (supersededByLaterResult)
        {
            return new(VerifyingFindingDisposition.SupersededByLaterVerification);
        }

        var completedRepair = goal.Tasks
            .Where(task => IsCompletedRepairOf(task, findingRecord))
            .OrderByDescending(task => task.LastVerification!.CompletedAt)
            .ThenBy(task => task.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return completedRepair is null
            ? new(VerifyingFindingDisposition.Current)
            : new(
                VerifyingFindingDisposition.SupersededByCompletedRepair,
                completedRepair.Id,
                completedRepair.LastDispatch!.ResultCommit!.Trim());
    }

    public static bool IsCurrent(
        Goal goal,
        TaskSpec verifyingTask,
        TaskVerificationRecord findingRecord) =>
        Classify(goal, verifyingTask, findingRecord) == VerifyingFindingDisposition.Current;

    public static bool HasCurrentOpenBlockingFinding(
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

    private static bool IsCompletedRepairOf(TaskSpec task, TaskVerificationRecord findingRecord)
    {
        if (task.RequiredRole != AgentRole.Developer ||
            task.Status != WorkTaskStatus.Completed ||
            task.LatestRetryAt is null ||
            task.LastVerification is not { } developerVerification ||
            !developerVerification.Succeeded ||
            developerVerification.CompletedAt <= findingRecord.CompletedAt ||
            task.LastDispatch is not { } dispatch ||
            dispatch.DispatchedAt < task.LatestRetryAt ||
            string.IsNullOrWhiteSpace(findingRecord.ReviewedCommit) ||
            string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            string.IsNullOrWhiteSpace(dispatch.ResultCommit) ||
            SameCandidate(findingRecord.ReviewedCommit, dispatch.ResultCommit) ||
            SameCandidate(dispatch.BaseCommit, dispatch.ResultCommit))
        {
            return false;
        }

        return HasCommittedOutput(task);
    }

    private static bool IsLatestCompletedResult(TaskSpec task, TaskVerificationRecord verification) =>
        task.Status == WorkTaskStatus.Completed &&
        verification.Succeeded &&
        task.LastVerification?.HasSameRoundIdentity(verification) is true;

    private static bool SameCandidate(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
