using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public static class UnchangedCandidateRule
{
    public static UnchangedCandidateHoldReason? Evaluate(
        Goal goal, TaskSpec candidateTask, CandidateIdentity? current)
    {
        if (current is null) return null;
        var prior = goal.Tasks
            .Where(task => task.RequiredRole == candidateTask.RequiredRole)
            .SelectMany(task => task.VerificationHistory.Select(verification => (task, verification)))
            .Where(pair => pair.verification.WorkerResultPresent &&
                pair.verification.ProviderFailureKind == ProviderFailureKind.Unknown &&
                pair.verification.OrchestratorFailureReason is null)
            .OrderByDescending(pair => pair.verification.CompletedAt)
            .FirstOrDefault();
        if (prior.verification is null ||
            !CandidateIdentity.AreSameCandidate(prior.verification.CandidateIdentity, current) ||
            HasNewInput(goal, candidateTask, prior.verification.CompletedAt))
            return null;
        var verdict = WorkerResultBlockers.TryFindNeedsWorkVerdict(prior.verification, out _)
            ? "needs-work"
            : WorkerResultBlockers.TryFindBlockedAtCapVerdict(prior.verification, out _)
                ? "blocked-at-cap"
                : prior.verification.AssignedScopeComplete is false
                    ? "revision-requested"
                    : prior.verification.Succeeded ? "passed" : "failed";
        return new UnchangedCandidateHoldReason(
            candidateTask.RequiredRole, prior.task.Id, prior.verification.CompletedAt, verdict, current);
    }

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
