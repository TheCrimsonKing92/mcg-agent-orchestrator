using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the pre-admission receipt pass; hold and stale-report state belong to the batch loop.
internal static class PassedMergeTrainReceiptAdmission
{
    internal delegate IReadOnlyList<(ConductorMergeTrainSelection Selection, MergeTrainReceipt Receipt)> FindReceipts(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IReadOnlySet<string> ineligibleGoalIds,
        Action<MergeTrainReceipt, string> onStale,
        Action<MergeTrainReceipt, PassedMergeTrainReceiptHolds.Observation> onBlocked,
        IReadOnlyList<Goal> scopedGoals,
        IReadOnlySet<GoalId> observationGoalIds);

    // The admission policy owns holds; the driver owns external observation and landing.
    internal sealed record Operations(
        FindReceipts Find,
        Func<Goal, ConductorAutonomyPolicy, GateReadyCandidateProjectionResult> Project,
        Func<ConductorMergeTrainSelection, Goal[], ConductorAutonomyPolicy, ConductorMergeTrainRunResult> Land);

    internal static (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates) Apply(
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        Goal[] eligible,
        ConductorSpeculativeAcceptanceCandidate[] candidates,
        IReadOnlyList<Goal> scopedGoals,
        Dictionary<string, ParallelLandingOutcome> results,
        int tick,
        List<string> changedGoalLines,
        PassedMergeTrainReceiptHolds holds,
        HashSet<string> reportedStaleReceiptIds,
        Action<string> emitProgress,
        Action<string, List<string>> recordProgress,
        Func<Goal, string, ConductorAdvanceResult> heldResult)
        => Apply(new Operations(
                (ready, ineligible, stale, blocked, scopedGoals, observed) =>
                    driver.FindLandablePassedMergeTrainSelections(ready, ineligible, stale, blocked, scopedGoals, observed),
                driver.ProjectGateReadyCandidate,
                (selection, goals, autonomy) => driver.RunMergeTrain(selection, goals, autonomy, landFromReceiptOnly: true)),
            policy, eligible, candidates, scopedGoals, results, tick, changedGoalLines, holds,
            reportedStaleReceiptIds, emitProgress, recordProgress, heldResult);

    internal static (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates) Apply(
        Operations operations,
        ConductorAutonomyPolicy policy,
        Goal[] eligible,
        ConductorSpeculativeAcceptanceCandidate[] candidates,
        IReadOnlyList<Goal> scopedGoals,
        Dictionary<string, ParallelLandingOutcome> results,
        int tick,
        List<string> changedGoalLines,
        PassedMergeTrainReceiptHolds holds,
        HashSet<string> reportedStaleReceiptIds,
        Action<string> emitProgress,
        Action<string, List<string>> recordProgress,
        Func<Goal, string, ConductorAdvanceResult> heldResult)
    {
        holds.BeginPass();
        var receiptObservationGoalIds = eligible.Select(goal => goal.Id)
            .Concat(holds.HeldGoalIds).ToHashSet();
        var ineligibleGoalIds = ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds(eligible);
        var heldMembers = new Dictionary<GoalId, MergeTrainReceipt>();
        void ReportStale(MergeTrainReceipt receipt, string moved)
        {
            if (reportedStaleReceiptIds.Add(receipt.ReceiptId))
            {
                emitProgress($"TRAIN_RECEIPT_STALE tick={tick} train={receipt.Identity.Value} " +
                    $"receipt={receipt.ReceiptId} moved={moved}");
            }
        }
        foreach (var (selection, _) in operations.Find(
                     candidates, ineligibleGoalIds, ReportStale, (receipt, observation) =>
                     {
                         var decision = holds.Observe(receipt, observation);
                         if (decision.Moved is not null)
                         {
                             ReportStale(receipt, decision.Moved);
                         }
                         else if (decision.Released)
                         {
                             emitProgress($"TRAIN_RECEIPT_RELEASED train={receipt.Identity.Value} " +
                                 $"receipt={receipt.ReceiptId} reason=hold-limit");
                         }
                         else if (decision.BlockedMember is { } blocked)
                         {
                             var blockedGoal = scopedGoals.SingleOrDefault(goal => goal.Id == blocked);
                             var projection = candidates.SingleOrDefault(candidate => candidate.GoalId == blocked)
                                 ?.ProjectionResult ?? (blockedGoal is null ? null :
                                     operations.Project(blockedGoal, policy));
                             var exclusion = projection as GateReadyCandidateProjectionResult.Excluded;
                             var hasCriterionGap = ineligibleGoalIds.Contains(blocked.Value) ||
                                 (blockedGoal is not null &&
                                  ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds([blockedGoal]).Contains(blocked.Value));
                             var reason = hasCriterionGap
                                 ? "ineligible-criterion-evidence"
                                 : (exclusion?.Reason ?? GateReadyCandidateExclusionReason.GateNotReady).ToString();
                             recordProgress(
                                 $"TRAIN_RECEIPT_HELD tick={tick} train={receipt.Identity.Value} " +
                                 $"receipt={receipt.ReceiptId} member={blocked.Value[..8]} " +
                                 $"reason={reason} heldTicks={decision.HeldTicks}", changedGoalLines);
                             foreach (var member in decision.HeldGoalIds!)
                             {
                                 heldMembers.TryAdd(member, receipt);
                             }
                         }
                     }, scopedGoals, receiptObservationGoalIds))
        {
            if (selection.Members.Any(member => results.ContainsKey(member.GoalId.Value)))
            {
                continue;
            }
            var run = operations.Land(selection, eligible, policy);
            foreach (var member in run.MemberResults)
            {
                results[member.Key] = new ParallelLandingOutcome(member.Value, SlotIndex: 0);
            }
            recordProgress(
                $"ACCEPTANCE_TRAIN tick={tick} members={string.Join(',', selection.Members.Select(member => member.GoalId.Value[..8]))} " +
                $"ejected={string.Join(',', run.Ejections.Select(ejection => ejection.GoalId.Value[..8]))} {run.Detail}",
                changedGoalLines);
            if (run.Detail.StartsWith("outcome=passed", StringComparison.Ordinal))
            {
                // Landing moved main, so the observations made before it no longer justify a hold.
                heldMembers.Clear();
                candidates = candidates
                    .Where(candidate => !results.ContainsKey(candidate.GoalId.Value))
                    .Select(candidate => new ConductorSpeculativeAcceptanceCandidate(
                        candidate.GoalId,
                        operations.Project(
                            eligible.Single(goal => goal.Id == candidate.GoalId), policy)))
                    .ToArray();
                break;
            }
        }
        holds.EndPass();
        foreach (var goal in eligible.Where(goal => heldMembers.ContainsKey(goal.Id) &&
                     !results.ContainsKey(goal.Id.Value)))
        {
            var receipt = heldMembers[goal.Id];
            var reason = $"reason={nameof(GateReadyCandidateExclusionReason.PassedTrainReceiptHeld)} " +
                $"train={receipt.Identity.Value} receipt={receipt.ReceiptId}";
            results[goal.Id.Value] = new ParallelLandingOutcome(
                heldResult(goal, reason), SlotIndex: null);
            recordProgress(
                $"ADMISSION tick={tick} result=held {reason} goal={goal.Id.Value[..8]}", changedGoalLines);
        }
        return (eligible.Where(goal => !results.ContainsKey(goal.Id.Value)).ToArray(),
            candidates.Where(candidate => !results.ContainsKey(candidate.GoalId.Value)).ToArray());
    }
}
