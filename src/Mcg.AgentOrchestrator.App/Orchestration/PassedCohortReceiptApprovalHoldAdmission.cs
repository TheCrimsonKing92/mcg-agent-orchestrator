using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Observes excluded members too, so a pre-walk approval intent cannot strand its partner.
internal static class PassedCohortReceiptApprovalHoldAdmission
{
    internal static (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates) Apply(
        ConductorDriver driver, ConductorAutonomyPolicy policy, AgentOrchestratorKernel kernel,
        Goal[] eligible, ConductorSpeculativeAcceptanceCandidate[] candidates,
        IReadOnlyList<Goal> scopedGoals, IReadOnlySet<GoalId> intentChangedGoalIds,
        Func<Goal, bool> otherwiseEligible, IReadOnlySet<string> activeGoalIds,
        Dictionary<string, ParallelLandingOutcome> results, int tick, Action<string> recordProgress)
    {
        if (!driver.AcceptanceCohortsEnabled) return (eligible, candidates);
        var candidateIds = candidates.Select(candidate => candidate.GoalId).ToHashSet();
        var observable = scopedGoals.Where(goal =>
            !results.ContainsKey(goal.Id.Value) && !activeGoalIds.Contains(goal.Id.Value) &&
            !ConductorWorkingSet.IsPreWalkExcludedGoal(goal) && otherwiseEligible(goal) &&
            !goal.OutstandingCriterionEvidenceObligations.Any(item => item.Owner != CriterionEvidenceOwner.Acceptance) &&
            (candidateIds.Contains(goal.Id) || intentChangedGoalIds.Contains(goal.Id))).ToArray();
        var groupedIds = GroupedAdmissionCandidateFilter.FilterGoals(
            observable.Select(goal => goal.Id.Value).ToArray(), kernel.Goals, activeGoalIds);
        observable = observable.Where(goal => groupedIds.Contains(goal.Id.Value)).ToArray();
        var projections = observable.ToDictionary(goal => goal.Id,
            goal => driver.ProjectGroupedAdmissionCandidate(goal, policy, kernel));
        var observations = observable.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, projections[goal.Id])).ToArray();
        var goals = observable.ToDictionary(goal => goal.Id);
        var eligibleIds = goals.Keys.ToHashSet();
        var restored = new HashSet<GoalId>();
        foreach (var selection in driver.FindLandablePassedCohortSelections(observations))
        {
            var receipt = driver.ReadCurrentPassedCohortReceipt(selection);
            if (receipt is null || receipt.Identity.Members.Any(member => driver.HasFinalizedCohortLanding(member.GoalId)))
                continue;
            PassedCohortReceiptApprovalHold.Decision? hold;
            try
            {
                var denylist = LandingExecutor.LoadLandingDenylist(driver.ExecutionDirectory, receipt.Identity.ObservedMainRevision).Denylist;
                var decisions = CollaborationItemStore.ForDirectory(OrchestratorWorkspace.ForDirectory(driver.ExecutionDirectory).OrchestratorDirectory);
                hold = PassedCohortReceiptApprovalHold.Evaluate(receipt, goals, projections, eligibleIds,
                    driver.HasFinalizedCohortLanding, member => LandingDenylistOwnerGate.Evaluate(
                        driver.ExecutionDirectory, denylist, decisions, member.GoalId.Value,
                        receipt.Identity.ObservedMainRevision, member.CandidateRevision, member.LandingPaths));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Failed observation is no evidence of an approval-only blocker. Let ordinary
                // admission diagnose it rather than reserving the receipt on an assumption.
                recordProgress($"COHORT_RECEIPT_APPROVAL_OBSERVATION_FAILED tick={tick} receipt={receipt.ReceiptId} error={exception.GetType().Name}");
                continue;
            }
            if (hold is null)
            {
                // Approval may itself have arrived before this walk. Restore only otherwise-ready
                // receipt members to the existing pre-land pass, which still owns all mutation.
                restored.UnionWith(selection.Members.Select(member => member.GoalId));
                continue;
            }
            foreach (var member in receipt.Identity.Members)
            {
                results[member.GoalId.Value] = new ParallelLandingOutcome(
                    PassedCohortReceiptApprovalHold.Held(goals[member.GoalId], policy, hold), null);
                recordProgress($"ADMISSION tick={tick} result=held {hold.Reason} goal={member.GoalId.Value[..8]}");
            }
        }
        return (eligible.Concat(observable.Where(goal => restored.Contains(goal.Id)))
                .DistinctBy(goal => goal.Id).Where(goal => !results.ContainsKey(goal.Id.Value)).ToArray(),
            candidates.Concat(observations.Where(candidate => restored.Contains(candidate.GoalId)))
                .DistinctBy(candidate => candidate.GoalId).Where(candidate => !results.ContainsKey(candidate.GoalId.Value)).ToArray());
    }
}
