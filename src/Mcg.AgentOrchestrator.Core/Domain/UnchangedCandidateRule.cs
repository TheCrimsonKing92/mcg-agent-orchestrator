using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public static class UnchangedCandidateRule
{
    public static UnchangedCandidateHoldReason? Evaluate(
        Goal goal, TaskSpec candidateTask, CandidateIdentity? current)
    {
        if (current is null) return null;
        var prior = FindPriorVerdict(goal, candidateTask);
        if (prior.verification is null ||
            !CandidateIdentity.AreSameCandidate(prior.verification.CandidateIdentity, current) ||
            HasNewInput(goal, candidateTask, prior.verification.CompletedAt))
            return null;
        var verdict = UnchangedCandidateVerdict.Derive(goal, prior.task, prior.verification);
        if (verdict == UnchangedCandidateVerdict.Rejected) return null;
        return new UnchangedCandidateHoldReason(
            candidateTask.RequiredRole, prior.task.Id, prior.verification.CompletedAt, verdict, current);
    }

    public static UnchangedCandidateReinstatement? EvaluateReinstatement(
        Goal goal, TaskSpec candidateTask, CandidateIdentity? current)
    {
        if (candidateTask.RequiredRole is not (AgentRole.Tester or AgentRole.Reviewer) ||
            !candidateTask.LatestRetryInherited ||
            candidateTask.Status is not (WorkTaskStatus.Pending or WorkTaskStatus.Assigned) ||
            candidateTask.LastVerification is not null || candidateTask.LastDispatch is not null ||
            candidateTask.LatestRetryAt is not { } resetAt)
            return null;

        // Reuse the hold predicate, including its latest role verdict and all new-input signals.
        var hold = Evaluate(goal, candidateTask, current);
        if (hold is null || hold.PriorVerdict != "passed" || hold.PriorVerdictTaskId != candidateTask.Id)
            return null;
        var prior = FindPriorVerdict(goal, candidateTask).verification!;
        if (prior.CompletedAt > resetAt || string.IsNullOrWhiteSpace(prior.AcceptanceCriteriaVersionHash) ||
            !string.Equals(prior.AcceptanceCriteriaVersionHash,
                EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal), StringComparison.Ordinal))
            return null;

        return new UnchangedCandidateReinstatement(candidateTask.RequiredRole, candidateTask.Id,
            hold.PriorVerdictTaskId, hold.PriorVerdictAt, hold.CandidateIdentity, prior);
    }

    private static (TaskSpec task, TaskVerificationRecord verification) FindPriorVerdict(
        Goal goal, TaskSpec candidateTask) => goal.Tasks
        .Where(task => task.RequiredRole == candidateTask.RequiredRole)
        .SelectMany(task => task.VerificationHistory.Select(verification => (task, verification)))
        .Where(pair => pair.verification.WorkerResultPresent &&
            pair.verification.ProviderFailureKind == ProviderFailureKind.Unknown &&
            pair.verification.OrchestratorFailureReason is null)
        .OrderByDescending(pair => pair.verification.CompletedAt)
        .FirstOrDefault();

    private static bool HasNewInput(Goal goal, TaskSpec task, DateTimeOffset verdictAt)
    {
        if (task.AcceptedRetryFeedback?.AcceptedAt > verdictAt ||
            goal.Timeline.Any(evt => evt.Kind == ProgressKind.HumanInputReceived && evt.OccurredAt > verdictAt))
            return true;
        if (task.LatestRoleInputRetryAt > verdictAt)
            return true;
        if (task.RequiredRole == AgentRole.Reviewer &&
            task.PreReviewEvidenceHistory.Any(receipt => receipt.RecordedAt > verdictAt))
            return true;
        foreach (var peer in goal.Tasks.Where(peer => peer.RequiredRole != task.RequiredRole))
        {
            foreach (var later in peer.VerificationHistory.Where(verification => verification.CompletedAt > verdictAt))
            {
                if (later.FindingEvidenceReceipts is { Count: > 0 }) return true;
                if (task.RequiredRole == AgentRole.Developer &&
                    peer.RequiredRole is AgentRole.Tester or AgentRole.Reviewer &&
                    (!later.Succeeded || later.MergedReviewFindings is { Count: > 0 })) return true;
                if (task.RequiredRole == AgentRole.Tester &&
                    peer.RequiredRole == AgentRole.Reviewer &&
                    later.MergedReviewFindings?.Any(finding => finding.EvidenceRequest is not null) == true)
                    return true;
            }
        }
        return false;
    }
}
