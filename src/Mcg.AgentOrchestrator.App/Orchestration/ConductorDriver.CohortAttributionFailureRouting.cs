using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Action<string> CohortAttributionVerdictSink { get; set; } =
        line => ConductorBatchLoop.EmitProgress(line);
    internal Func<Goal, GateReadyCandidateRevisionPair?>? CohortAttributionRevisionReader { get; set; }

    private ConductorAcceptanceCohortRunResult ApplyCohortAttributionFailureVerdicts(
        ConductorAcceptanceCohortRunResult held,
        IReadOnlyList<Goal> goals,
        AcceptanceCohortReceipt receipt,
        ConductorAutonomyPolicy policy)
    {
        if (receipt.Outcome != AcceptanceCohortGateOutcome.Failed ||
            receipt.Attribution == AcceptanceCohortAttributionOutcome.NotApplicable)
        {
            return held;
        }

        var partitions = _cohortAcceptanceStore!.ReadPartitionReceipts(receipt.Identity.Value)
            .ToDictionary(partition => partition.GoalId);
        var memberResults = held.MemberResults.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var suppliedGoal in goals)
        {
            var goal = GetCurrentGoal(suppliedGoal);
            partitions.TryGetValue(goal.Id, out var partition);
            string? candidate = null;
            string? main = null;
            if (partition is not null && receipt.AttributedMembers.Any(member => member.GoalId == goal.Id))
            {
                var path = GoalWorktrees.TryResolve(_cohortWorkspace!.ExecutionDirectory, goal.Id);
                if (path is not null)
                {
                    try { candidate = ConductorGitRevisionReader.ReadRequiredCommit(path, "HEAD"); }
                    catch (InvalidOperationException) { /* A missing current identity cannot authorize failure. */ }
                    try { main = ConductorGitRevisionReader.ReadRequiredCommit(path, "main^{commit}"); }
                    catch (InvalidOperationException) { /* A missing current identity cannot authorize failure. */ }
                }
            }

            var alreadyRecorded = partition is not null &&
                ConductorAcceptanceCohortAttributionVerdicts.IsRecordedFailure(
                    goal, receipt.Identity.Value, partition.ReceiptId);
            memberResults[goal.Id.Value] = ConductorAcceptanceCohortAttributionVerdicts.CompleteMember(
                receipt, goal, partition, candidate, main, alreadyRecorded,
                (member, checks, branch, mainHead, attributions) =>
                    _recordAcceptanceFailure(member, checks, branch, mainHead, attributions, null),
                held.MemberResults[goal.Id.Value], CohortAttributionVerdictSink);
        }
        return held with { MemberResults = memberResults };
    }

    private ConductorAdvanceResult? RouteRecordedCohortAttributionFailure(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        if (!HasRoutableRecordedCohortAttributionFailure(goal)) return null;
        var failure = goal.LatestAcceptanceFailure!;
        var reason = $"Acceptance verification failed; review and fix before landing. " +
            failure.CheckAttributions![0].Evidence;
        if (goal.Status == GoalStatus.AcceptanceFailed)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.AcceptanceFailed, reason,
                    ConductorEscalationKind.AcceptanceVerificationFailed));
        }
        if ((_cohortKernel ?? _conductorTickKernel)?.RouteRecordedAcceptanceFailure(goal.Id, reason) != true)
        {
            throw new InvalidOperationException("Recorded cohort acceptance failure could not route its Verified member.");
        }
        return Escalate(goal, goalPrefix, policy, GoalLifecycleState.AcceptanceFailed, reason,
            ConductorEscalationKind.AcceptanceVerificationFailed);
    }

    internal bool HasRoutableRecordedCohortAttributionFailure(Goal goal)
    {
        var failure = goal.LatestAcceptanceFailure;
        if (failure?.CheckAttributions?.Any(attribution =>
                attribution.Evidence.StartsWith(ConductorAcceptanceCohortAttributionVerdicts.EvidencePrefix,
                    StringComparison.Ordinal)) != true)
        {
            return false;
        }

        var current = ReadCurrentCohortAttributionRevisions(goal);
        return current is not null &&
            current.BranchRevision == failure.BranchHeadSha &&
            current.MainRevision == failure.MainHeadSha;
    }

    private GateReadyCandidateRevisionPair? ReadCurrentCohortAttributionRevisions(Goal goal)
    {
        if (CohortAttributionRevisionReader is not null)
        {
            return CohortAttributionRevisionReader(goal);
        }
        var path = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
        if (path is null) return null;
        try { return ConductorGitRevisionReader.ReadRequiredPair(path); }
        catch (InvalidOperationException) { return null; }
    }
}
