using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Observes excluded members too, so a pre-walk approval intent cannot strand its partner.
internal static class PassedCohortReceiptApprovalHoldAdmission
{
    internal static (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates) ApplyBeforeGating(
        ConductorDriver driver, ConductorAutonomyPolicy policy, AgentOrchestratorKernel kernel,
        Goal[] eligible, ConductorSpeculativeAcceptanceCandidate[] candidates,
        IReadOnlyList<Goal> scopedGoals, IReadOnlySet<GoalId> intentChangedGoalIds,
        Func<Goal, bool> otherwiseEligible, IReadOnlySet<string> activeGoalIds,
        Dictionary<string, ParallelLandingOutcome> results, int tick, Action<string> recordProgress,
        Func<bool> suppressNewAdmission, Func<Goal, bool> requiresCircuitHold, ISet<GoalId> changedGoalIds)
    {
        if (!suppressNewAdmission())
            (eligible, candidates) = Apply(driver, policy, kernel, eligible, candidates,
                scopedGoals, intentChangedGoalIds, otherwiseEligible, activeGoalIds, results, tick, recordProgress);
        if (!suppressNewAdmission() && driver.AcceptanceCohortsEnabled &&
            eligible.Length >= ConductorAcceptanceCohortSelector.CohortSize && !eligible.Any(requiresCircuitHold))
        {
            var beforePrelanding = eligible.ToDictionary(goal => goal.Id, GoalKernelChange.Capture);
            (eligible, candidates) = PassedCohortReceiptReplay.LandBeforeTrainSelection(
                driver, policy, eligible, candidates, results, tick, recordProgress, kernel);
            // A restored approval-intent target is outside the ordinary walk. Persist its landing
            // here, together with any other state changed by this pre-land pass.
            foreach (var (id, before) in beforePrelanding)
                if (GoalKernelChange.Changed(before, kernel, id)) changedGoalIds.Add(id);
        }
        return (eligible, candidates);
    }

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
                recordProgress($"ADMISSION tick={tick} result=held reason=PassedCohortReceiptApprovalHeld {hold.Reason} goal={member.GoalId.Value[..8]}");
            }
            foreach (var (goalId, holdReason) in hold.MissingApprovals)
            {
                try
                {
                    OperatorInbox.RecordLandingEscalation(OrchestratorWorkspace.ForDirectory(driver.ExecutionDirectory),
                        goals[goalId], holdReason, LandingExecutor.IntegrationBranchName);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    recordProgress($"ADMISSION tick={tick} result=held reason=cohort-approval-attention-write-failed " +
                        $"cohort={receipt.Identity.Value} goal={goalId.Value[..8]} error={exception.GetType().Name}");
                }
            }
        }
        return (eligible.Concat(observable.Where(goal => restored.Contains(goal.Id)))
                .DistinctBy(goal => goal.Id).Where(goal => !results.ContainsKey(goal.Id.Value)).ToArray(),
            candidates.Concat(observations.Where(candidate => restored.Contains(candidate.GoalId)))
                .DistinctBy(candidate => candidate.GoalId).Where(candidate => !results.ContainsKey(candidate.GoalId.Value)).ToArray());
    }
}
