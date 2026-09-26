using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly HashSet<string> _reportedStaleMergeTrainReceiptIds = new(StringComparer.Ordinal);

    private (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates)
        LandPassedMergeTrainReceiptsBeforeAdmission(
            ConductorDriver driver,
            ConductorAutonomyPolicy policy,
            Goal[] eligible,
            ConductorSpeculativeAcceptanceCandidate[] candidates,
            Dictionary<string, ParallelLandingOutcome> results,
            int tick,
            List<string> changedGoalLines)
    {
        if (eligible.Length < ConductorMergeTrainSelector.MinimumCompositionMembers)
        {
            return (eligible, candidates);
        }

        var ineligibleGoalIds = TrainIneligibleCriterionEvidenceGoalIds(eligible);
        foreach (var (selection, _) in driver.FindLandablePassedMergeTrainSelections(
                     candidates, ineligibleGoalIds, (receipt, moved) =>
                     {
                         if (_reportedStaleMergeTrainReceiptIds.Add(receipt.ReceiptId))
                         {
                             EmitProgress($"TRAIN_RECEIPT_STALE tick={tick} train={receipt.Identity.Value} " +
                                 $"receipt={receipt.ReceiptId} moved={moved}");
                         }
                     }))
        {
            if (selection.Members.Any(member => results.ContainsKey(member.GoalId.Value)))
            {
                continue;
            }
            var run = driver.RunMergeTrain(selection, eligible, policy, landFromReceiptOnly: true);
            foreach (var member in run.MemberResults)
            {
                results[member.Key] = new ParallelLandingOutcome(member.Value, SlotIndex: 0);
            }
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_TRAIN tick={tick} members={string.Join(',', selection.Members.Select(member => member.GoalId.Value[..8]))} " +
                $"ejected={string.Join(',', run.Ejections.Select(ejection => ejection.GoalId.Value[..8]))} {run.Detail}",
                changedGoalLines);
            if (run.Detail.StartsWith("outcome=passed", StringComparison.Ordinal))
            {
                candidates = candidates
                    .Where(candidate => !results.ContainsKey(candidate.GoalId.Value))
                    .Select(candidate => new ConductorSpeculativeAcceptanceCandidate(
                        candidate.GoalId,
                        driver.ProjectGateReadyCandidate(
                            eligible.Single(goal => goal.Id == candidate.GoalId), policy)))
                    .ToArray();
                break;
            }
        }
        return (eligible.Where(goal => !results.ContainsKey(goal.Id.Value)).ToArray(),
            candidates.Where(candidate => !results.ContainsKey(candidate.GoalId.Value)).ToArray());
    }
}
