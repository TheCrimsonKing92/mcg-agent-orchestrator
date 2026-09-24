using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static ConductorSpeculativeAcceptanceCandidate[] ExcludeGroupedAcceptanceCandidatesWithNonAcceptanceObligations(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IReadOnlyList<Goal> eligibleGoals,
        IReadOnlySet<string> liveAttemptGoalIds,
        IReadOnlySet<string> activeCohortMemberGoalIds)
    {
        var excludedGoalIds = eligibleGoals
            .Where(goal => goal.OutstandingCriterionEvidenceObligations.Any(obligation =>
                obligation.Owner != CriterionEvidenceOwner.Acceptance))
            .Select(goal => goal.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(candidate => !liveAttemptGoalIds.Contains(candidate.GoalId.Value) &&
                                !activeCohortMemberGoalIds.Contains(candidate.GoalId.Value) &&
                                !excludedGoalIds.Contains(candidate.GoalId.Value))
            .ToArray();
    }

    internal static IReadOnlySet<string> TrainIneligibleCriterionEvidenceGoalIds(IReadOnlyList<Goal> eligibleGoals) =>
        eligibleGoals
            .Where(goal => AcceptanceCriterionEvidence.DescribeTrainOperatorEvidenceGap(goal) is not null ||
                goal.OutstandingCriterionEvidenceObligations.Any(obligation =>
                    obligation.Owner != CriterionEvidenceOwner.Acceptance))
            .Select(goal => goal.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
}
