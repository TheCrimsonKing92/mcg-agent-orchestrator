using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Replays current passed cohort receipts through the existing landing and completion path.
internal static class PassedCohortReceiptReplay
{
    internal static IReadOnlyList<ConductorAcceptanceCohortSelection> FindLandableSelections(
        CohortAcceptanceStore? store, IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        Func<IReadOnlySet<string>> readSuppressedPairs,
        Func<ConductorAcceptanceCohortSelection, AcceptanceCohortReceipt, bool> hasCurrentIdentity)
    {
        if (store is null)
        {
            return [];
        }

        var ready = candidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready)
            .ToDictionary(candidate => candidate.GoalId,
                candidate => ((GateReadyCandidateProjectionResult.Ready)candidate.ProjectionResult).Projection);
        var claimed = new HashSet<GoalId>();
        var selections = new List<ConductorAcceptanceCohortSelection>();
        foreach (var candidate in candidates)
        {
            if (!ready.TryGetValue(candidate.GoalId, out var first) || claimed.Contains(candidate.GoalId))
            {
                continue;
            }

            foreach (var receipt in store.ReadPassedReceiptsForGoal(candidate.GoalId))
            {
                if (receipt.Identity.Members.Count != ConductorAcceptanceCohortSelector.CohortSize ||
                    !string.Equals(receipt.Identity.ObservedMainRevision, first.MainRevision, StringComparison.Ordinal) ||
                    receipt.Identity.Members.Any(member =>
                        claimed.Contains(member.GoalId) ||
                        !ready.TryGetValue(member.GoalId, out var live) ||
                        !string.Equals(member.CandidateRevision, live.CandidateRevision, StringComparison.Ordinal)))
                {
                    continue;
                }

                var pair = receipt.Identity.Members
                    .Select(member => candidates.Single(candidate => candidate.GoalId == member.GoalId))
                    .ToArray();
                var selection = ConductorAcceptanceCohortSelector.Select(
                    pair, suppressedPairFingerprints: readSuppressedPairs()).Selection;
                if (selection is null)
                {
                    continue;
                }
                if (!hasCurrentIdentity(selection, receipt))
                {
                    continue;
                }

                selections.Add(selection);
                foreach (var member in selection.Members)
                {
                    claimed.Add(member.GoalId);
                }
                break;
            }
        }
        return selections;
    }

    internal static (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates)
        LandBeforeTrainSelection(
            ConductorDriver driver,
            ConductorAutonomyPolicy policy,
            Goal[] eligible,
            ConductorSpeculativeAcceptanceCandidate[] candidates,
            Dictionary<string, ParallelLandingOutcome> results,
            int tick,
            Action<string> recordProgress, AgentOrchestratorKernel kernel)
    {
        var landed = false;
        foreach (var selection in driver.FindLandablePassedCohortSelections(candidates))
        {
            if (selection.Members.Any(member => results.ContainsKey(member.GoalId.Value)))
            {
                continue;
            }

            var run = driver.RunAcceptanceCohort(selection, eligible, policy, runGateInBackground: true);
            foreach (var member in run.MemberResults)
            {
                var result = member.Value;
                if (run.Detail.StartsWith("outcome=passed", StringComparison.Ordinal))
                {
                    var goal = eligible.Single(goal => goal.Id.Value == member.Key);
                    var recorded = driver.AdvanceOnce(goal, policy);
                    if (recorded.Outcome is not ConductorAdvanceOutcome.Executed
                        { FromState: GoalLifecycleState.Merged })
                    {
                        throw new InvalidOperationException(
                            $"Pre-landed cohort goal {member.Key} did not record its landing.");
                    }
                    result = driver.AdvanceOnce(goal, policy);
                    if (result.Outcome is not ConductorAdvanceOutcome.Executed
                        { FromState: GoalLifecycleState.Recorded } || goal.Status != GoalStatus.Completed)
                    {
                        throw new InvalidOperationException(
                            $"Pre-landed cohort goal {member.Key} did not complete after cleanup.");
                    }
                }
                results[member.Key] = new ParallelLandingOutcome(result, SlotIndex: 0);
            }
            recordProgress(
                $"ACCEPTANCE_COHORT tick={tick} members={string.Join(',', selection.Members.Select(member => member.GoalId.Value[..8]))} prelanded=true {run.Detail}");
            // A successful landing advances main, so other receipts probed against the old main
            // must be reconsidered on a later tick.
            if (run.Detail.StartsWith("outcome=passed", StringComparison.Ordinal))
            {
                landed = true;
                break;
            }
        }

        if (landed)
        {
            candidates = candidates
                .Where(candidate => !results.ContainsKey(candidate.GoalId.Value))
                .Select(candidate => new ConductorSpeculativeAcceptanceCandidate(
                    candidate.GoalId,
                    driver.ProjectGroupedAdmissionCandidate(
                        eligible.Single(goal => goal.Id == candidate.GoalId), policy, kernel)))
                .ToArray();
        }
        return (eligible.Where(goal => !results.ContainsKey(goal.Id.Value)).ToArray(),
            candidates.Where(candidate => !results.ContainsKey(candidate.GoalId.Value)).ToArray());
    }
}
