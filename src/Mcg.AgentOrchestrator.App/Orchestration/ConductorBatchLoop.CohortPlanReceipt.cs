using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private void EmitSpeculativeCohortPlanReceipt(
        IReadOnlyList<Goal> scopedGoals,
        IReadOnlySet<GoalId> verifiedGoalIdsAtTickStart,
        IReadOnlySet<GoalId> preWalkIntentChangedGoalIds,
        IReadOnlyList<Goal> eligible,
        IReadOnlyList<Goal> orderedEligible,
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> speculativeCandidates,
        IReadOnlyList<ConductorParallelAcceptanceAttempt> liveAttempts,
        ConductorAcceptanceCapacitySnapshot activeCohorts,
        LiveAcceptanceCensus acceptanceCensus,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel,
        int tick)
    {
        var liveAttemptGoalIds = liveAttempts.Select(attempt => attempt.GoalId)
            .ToHashSet(StringComparer.Ordinal);
        var cohortMemberIds = activeCohorts.ActiveRoots
            .SelectMany(root => root.MemberGoalIds)
            .ToHashSet(StringComparer.Ordinal);
        var eligibleIds = eligible.Select(goal => goal.Id).ToHashSet();
        var orderedIds = orderedEligible.Select(goal => goal.Id).ToHashSet();
        var upstream = new List<ConductorSpeculativeUpstreamExclusion>();

        foreach (var goal in scopedGoals.Where(goal => verifiedGoalIdsAtTickStart.Contains(goal.Id)))
        {
            ConductorSpeculativeUpstreamExclusionReason? reason = null;
            if (!eligibleIds.Contains(goal.Id))
            {
                reason = preWalkIntentChangedGoalIds.Contains(goal.Id) || IsPreWalkExcludedGoal(goal) ?
                    ConductorSpeculativeUpstreamExclusionReason.OtherUpstreamFilter :
                    ConductorSpeculativeUpstreamExclusionReason.LifecycleRetired;
            }
            else if (!orderedIds.Contains(goal.Id))
            {
                reason = ClassifyBatchPlannerInputExclusion(
                    goal, driver, completedGoals, escalatedGoals, kernel);
            }
            else if (liveAttemptGoalIds.Contains(goal.Id.Value))
            {
                reason = ConductorSpeculativeUpstreamExclusionReason.LiveAcceptanceAttempt;
            }
            else if (cohortMemberIds.Contains(goal.Id.Value))
            {
                reason = ConductorSpeculativeUpstreamExclusionReason.CohortGateMember;
            }
            else if (goal.OutstandingCriterionEvidenceObligations.Any(obligation =>
                obligation.Owner != CriterionEvidenceOwner.Acceptance))
            {
                reason = ConductorSpeculativeUpstreamExclusionReason.OtherUpstreamFilter;
            }

            if (reason is { } excluded)
            {
                upstream.Add(new ConductorSpeculativeUpstreamExclusion(goal.Id, excluded));
            }
        }

        var excludedIds = upstream.Select(item => item.GoalId).ToHashSet();
        var receiptCandidates = speculativeCandidates
            .Where(candidate => !excludedIds.Contains(candidate.GoalId)).ToArray();
        var plan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(receiptCandidates);

        var widthOccupied = plan.ReadyCandidateCount > 0 &&
            acceptanceCensus.CaptureFailure is null &&
            acceptanceCensus.OccupiedCount >= policy.AcceptanceWidth;

        EmitProgress(plan.FormatReceipt(tick,
            new ConductorSpeculativeCohortReceiptContext(upstream,
                widthOccupied ? policy.AcceptanceWidth : null,
                widthOccupied ? acceptanceCensus.Occupants : null)));
    }

    private static ConductorSpeculativeUpstreamExclusionReason ClassifyBatchPlannerInputExclusion(
        Goal goal,
        ConductorDriver driver,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel)
    {
        if (!IsParallelAcceptanceLifecycleEligible(goal, driver))
        {
            return driver.ParallelAcceptanceEnabled
                ? ConductorSpeculativeUpstreamExclusionReason.LifecycleRetired
                : ConductorSpeculativeUpstreamExclusionReason.OtherUpstreamFilter;
        }
        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
        {
            return ConductorSpeculativeUpstreamExclusionReason.VerificationIncomplete;
        }
        if (GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel) is not null)
        {
            return ConductorSpeculativeUpstreamExclusionReason.DependencyHold;
        }
        if (VerifiedAcceptanceEscalationDecision.TryHasUnresolvedPersistedVerifiedAcceptanceEscalation(goal, driver) != false)
        {
            return ConductorSpeculativeUpstreamExclusionReason.PersistedAcceptanceEscalation;
        }
        return ConductorSpeculativeUpstreamExclusionReason.OtherUpstreamFilter;
    }
}
