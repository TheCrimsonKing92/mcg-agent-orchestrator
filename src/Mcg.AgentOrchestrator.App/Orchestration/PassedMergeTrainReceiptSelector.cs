using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns receipt observation and selection; storage, git and identity validation stay with the driver.
internal static class PassedMergeTrainReceiptSelector
{
    internal static IReadOnlyList<(ConductorMergeTrainSelection Selection, MergeTrainReceipt Receipt)> Find(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IReadOnlySet<string> ineligibleGoalIds,
        Action<MergeTrainReceipt, string> onStale,
        Action<MergeTrainReceipt, PassedMergeTrainReceiptHolds.Observation>? onBlocked,
        IReadOnlyList<Goal>? observationGoals,
        IReadOnlySet<GoalId>? receiptObservationGoalIds,
        Func<Goal, (string? BranchHeadSha, string? MainHeadSha)> resolveReceiptHeads,
        IReadOnlyCollection<Goal>? kernelGoals,
        Func<GoalId, IReadOnlyList<MergeTrainReceipt>> readPassedReceipts,
        Func<IReadOnlySet<string>> readSuppressedPairs,
        Func<ConductorMergeTrainSelection, MergeTrainReceipt, bool> hasCurrentIdentity)
    {
        var ready = candidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready)
            .ToDictionary(candidate => candidate.GoalId,
                candidate => ((GateReadyCandidateProjectionResult.Ready)candidate.ProjectionResult).Projection);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var suppressed = readSuppressedPairs();
        var selections = new List<(ConductorMergeTrainSelection, MergeTrainReceipt)>();
        var resolveHeads = resolveReceiptHeads;
        var observationGoalIds = onBlocked is not null && observationGoals is not null
            ? candidates.Select(candidate => candidate.GoalId).Concat(observationGoals
                .Where(goal => receiptObservationGoalIds?.Contains(goal.Id) == true)
                .Select(goal => goal.Id)).Distinct()
            : candidates.Select(candidate => candidate.GoalId);
        foreach (var goalId in observationGoalIds)
        {
            ready.TryGetValue(goalId, out var first);
            if (first is null && onBlocked is null)
            {
                continue;
            }
            // Most eligible goals have no receipt. Do not resolve git heads until one exists.
            var receipts = readPassedReceipts(goalId);
            if (receipts.Count == 0)
            {
                continue;
            }
            var observationGoal = first is null ? observationGoals?.SingleOrDefault(goal => goal.Id == goalId) : null;
            var observedMain = first?.MainRevision ?? (observationGoal is null ? null :
                resolveHeads(observationGoal).MainHeadSha);
            if (observedMain is null)
            {
                continue;
            }
            foreach (var receipt in receipts)
            {
                if (!seen.Add(receipt.ReceiptId) ||
                    receipt.Identity.Members.Count < ConductorMergeTrainSelector.MinimumCompositionMembers)
                {
                    continue;
                }
                var notReady = receipt.Identity.Members.Where(member =>
                        ineligibleGoalIds.Contains(member.GoalId.Value) || !ready.ContainsKey(member.GoalId))
                    .Select(member => member.GoalId).ToHashSet();
                if (notReady.Count > 0)
                {
                    if (onBlocked is not null)
                    {
                        var revisions = receipt.Identity.Members.ToDictionary(member => member.GoalId, member =>
                        {
                            if (ready.TryGetValue(member.GoalId, out var projection))
                            {
                                return projection.CandidateRevision;
                            }
                            var goal = kernelGoals
                                .SingleOrDefault(goal => goal.Id == member.GoalId);
                            return goal is null ? null : resolveHeads(goal).BranchHeadSha;
                        });
                        onBlocked(receipt, new(observedMain, revisions, notReady));
                    }
                    continue;
                }
                if (selections.Count > 0)
                {
                    // Keep observing blocked receipts, but preserve the first landable selection.
                    continue;
                }
                if (!string.Equals(receipt.Identity.ObservedMainRevision, observedMain,
                        StringComparison.Ordinal))
                {
                    onStale(receipt, "main");
                    continue;
                }
                var movedMember = receipt.Identity.Members.FirstOrDefault(member =>
                    !string.Equals(member.CandidateRevision, ready[member.GoalId].CandidateRevision,
                        StringComparison.Ordinal));
                if (movedMember is not null)
                {
                    onStale(receipt, $"member:{movedMember.GoalId.Value[..8]}");
                    continue;
                }
                var members = receipt.Identity.Members.Select(member => ready[member.GoalId]).ToArray();
                if (members.SelectMany((member, index) => members.Skip(index + 1).Select(peer =>
                        ConductorAcceptanceCohortSelector.PairFingerprint(member, peer)))
                    .Any(suppressed.Contains))
                {
                    continue;
                }
                var selection = new ConductorMergeTrainSelection(members);
                if (!hasCurrentIdentity(selection, receipt))
                {
                    continue;
                }
                selections.Add((selection, receipt));
                // Landing this train moves main; every other receipt must be checked next tick.
                if (onBlocked is null)
                {
                    return selections;
                }
            }
        }
        return selections;
    }
}
