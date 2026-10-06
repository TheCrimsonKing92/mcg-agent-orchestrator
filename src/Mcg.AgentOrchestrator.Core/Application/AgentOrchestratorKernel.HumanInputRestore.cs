namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static bool ShouldRequeueAnsweredTesterBlocker(
        Goal goal, TaskSpec task, HumanInputRequest request, string answer)
    {
        var verification = task.LastVerification;
        if (task.RequiredRole != AgentRole.Tester ||
            !(verification is { Succeeded: true } || task.Status == WorkTaskStatus.Failed) ||
            !WorkerResultBlockers.TryFindTesterWorkerResultBlocker(task, verification, out _) ||
            WorkerResultBlockers.TryFindFailingTests(verification, out _) ||
            string.IsNullOrWhiteSpace(answer) ||
            IsReopenProtectedGoalStatus(goal.Status) ||
            task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true } ||
            goal.Tasks.Any(candidate => IsDownstreamRole(task.RequiredRole, candidate.RequiredRole) &&
                                        candidate.LastProcess is { IsRunning: true }))
            return false;

        // A retry-cause clarification on the same task must not consume its worker blocker.
        var parsedQuestion = AgentOutputDirectives.ParseHumanInputRequest(verification!.StandardOutput, task.RequiredRole);
        var rawQuestion = verification.HumanInputQuestion ?? parsedQuestion.Directive?.Question;
        if (rawQuestion is null || !string.Equals(request.QuestionFingerprint,
                verification.HumanInputQuestionFingerprint ?? parsedQuestion.Directive?.QuestionFingerprint ??
                HumanInputRequest.BuildQuestionFingerprint(rawQuestion), StringComparison.Ordinal))
            return false;

        var timeline = goal.Timeline.ToList();
        var questionIndex = timeline.FindLastIndex(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputRequested && item.Message == request.Question);
        if (questionIndex < 0)
            questionIndex = timeline.FindLastIndex(item => item.TaskId == task.Id &&
                item.Kind == ProgressKind.HumanInputRequested);
        if (questionIndex < 0) return false;

        // Timeline order, rather than timestamps, distinguishes operator retries of this question.
        return !timeline.Skip(questionIndex + 1).Any(item => item.TaskId == task.Id &&
            item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskRetryFeedbackUpdated);
    }

    private void RequeueAnsweredTesterBlocker(Goal goal, TaskSpec task, string answer)
    {
        // The request is answered; remove its wait status before entering the normal retry policy.
        task.SetStatus(WorkTaskStatus.Assigned);
        RetryTask(goal.Id, task.Id, answer, RetryCause.ContractClarification);
    }

    private static RetryAdmissionReceipt? GetRefusedUnstartedDispatch(TaskSpec task)
    {
        var latestAdmission = task.RetryAdmissionHistory.LastOrDefault(receipt =>
            receipt.Decision is RetryAdmissionDecision.Allowed or
                RetryAdmissionDecision.ResumedReservation or RetryAdmissionDecision.Prevented);
        if (latestAdmission is not { Decision: RetryAdmissionDecision.Prevented } ||
            task.LastProcess is { IsRunning: true } ||
            task.LastProcess is { } process && process.StartedAt >= latestAdmission.LinkedDispatchAt ||
            task.LastDispatch is { } dispatch && dispatch.DispatchedAt > latestAdmission.LinkedDispatchAt)
            return null;

        return task.RetryAdmissionHistory.Any(receipt =>
            receipt.LinkedDispatchAt == latestAdmission.LinkedDispatchAt &&
            (receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation ||
             receipt.WorkerStartClaimedAt is not null || receipt.WorkerStartedAt is not null))
            ? null
            : latestAdmission;
    }

    private static void ClearStaleVerificationForAnsweredRestore(TaskSpec task)
    {
        // Keep the prior round in verification history while allowing its answered continuation to dispatch.
        if (task.LastVerification is { Succeeded: true }) task.ClearLatestVerification();
    }
}
