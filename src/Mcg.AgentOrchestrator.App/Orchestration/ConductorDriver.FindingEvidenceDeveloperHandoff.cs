using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TryRouteDeliveredFindingEvidenceToDeveloper(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy,
        GoalLifecycleState state, out ConductorAdvanceResult result)
    {
        result = default!;
        if (state != GoalLifecycleState.WorkspaceReady ||
            goal.Tasks.Any(task => task.LastProcess is { IsRunning: true })) return false;
        var retryCap = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound);
        if (retryCap.IsAtCap) return false;

        var candidateSha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(candidateSha)) return false;

        foreach (var requester in goal.Tasks.Where(HasUnconsumedRetry))
        {
            var predecessors = TasksBefore(goal, requester);
            var developer = predecessors.LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
            if (developer is null || predecessors.Any(task => task.Status != WorkTaskStatus.Completed)) continue;

            var delivery = goal.Timeline.LastOrDefault(evt =>
                evt.Kind == ProgressKind.TaskRetried && evt.TaskId == requester.Id &&
                evt.OccurredAt >= requester.LatestRetryAt);
            if (delivery is null ||
                !FindingEvidenceExecutionClassifier.TryReadEvidenceDeliveryRetry(
                    delivery, out var deliveredCandidate, out var deliveredIds) ||
                !string.Equals(candidateSha, deliveredCandidate, StringComparison.OrdinalIgnoreCase)) continue;

            var verification = requester.VerificationHistory.LastOrDefault(record =>
                WorkerResultBlockers.TryFindReviewFindingRound(record, out _, out _));
            if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out _)) continue;
            var blockers = ReviewFindings.GetOpenBlockingFindings(
                verification?.MergedReviewFindings is { Count: > 0 } merged ? merged : round.Findings,
                goal.EffectiveAcceptanceCriteriaCorrections);
            var route = ReviewerFindingEvidenceSuppressionRouting.Resolve(
                verification, goal.EffectiveAcceptanceCriteriaCorrections, blockers);
            if (route.ChosenOwner != AgentRole.Developer || route.DeferToReviewRetryRoute) continue;

            var executed = blockers.Where(finding =>
                deliveredIds.Contains(finding.StableId, StringComparer.Ordinal) &&
                route.WritableBlockerIds.Contains(finding.StableId, StringComparer.Ordinal) &&
                FindingEvidenceExecutionClassifier.Classify(requester, finding, candidateSha) ==
                    FindingEvidenceExecutionState.ExecutedOnCandidate).ToArray();
            if (executed.Length == 0) continue;

            var receiptIds = requester.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                .Where(receipt => string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
                .Select(receipt => receipt.ReceiptId).Distinct(StringComparer.Ordinal);
            var message = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
                retryCap.Round, requester.RequiredRole, requester.Id, "delivered finding evidence",
                AgentRole.Developer, verification?.StandardOutputPath ?? "requesting task verification history",
                executed.Select(finding => $"stable_id: {finding.StableId}; {finding.Description}"),
                executed.Select(finding => finding.Location.File)) + Environment.NewLine +
                $"candidate_sha={candidateSha}; receipt_ids={string.Join(',', receiptIds)}";
            _retryTask(goal.Id, developer.Id, message, null, RetryCause.NewSourceFinding);

            var refreshed = GetCurrentGoal(goal);
            var target = refreshed.Tasks.FirstOrDefault(task => task.Id == developer.Id);
            result = target is { Status: WorkTaskStatus.Assigned or WorkTaskStatus.Pending } &&
                !refreshed.Tasks.Any(task => task.LastProcess is { IsRunning: true })
                ? ExecuteDispatchAndStart(refreshed, goalPrefix, policy, GoalLifecycleState.WorkspaceReady)
                : MakeResult(refreshed.Id.Value, goalPrefix, policy,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycle.ResolveState(refreshed, GetFacts(refreshed)),
                        "Finding evidence Developer retry was re-observed; the target attempt changed before dispatch."));
            return true;
        }

        return false;
    }
}
