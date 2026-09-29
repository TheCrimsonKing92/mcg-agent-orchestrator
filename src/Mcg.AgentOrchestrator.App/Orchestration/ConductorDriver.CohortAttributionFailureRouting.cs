using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Action<string> CohortAttributionVerdictSink { get; set; } = Console.WriteLine;

    private ConductorAcceptanceCohortRunResult ApplyCohortAttributionFailureVerdicts(
        ConductorAcceptanceCohortRunResult held,
        IReadOnlyList<Goal> goals,
        AcceptanceCohortReceipt receipt)
    {
        if (receipt.Outcome != AcceptanceCohortGateOutcome.Failed ||
            receipt.Attribution == AcceptanceCohortAttributionOutcome.NotApplicable)
        {
            return held;
        }

        var partitions = _cohortAcceptanceStore!.ReadPartitionReceipts(receipt.Identity.Value)
            .ToDictionary(partition => partition.GoalId);
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
            ConductorAcceptanceCohortAttributionVerdicts.Apply(
                receipt, goal, partition, candidate, main, alreadyRecorded,
                (member, checks, branch, mainHead, attributions) =>
                    _recordAcceptanceFailure(member, checks, branch, mainHead, attributions, null),
                CohortAttributionVerdictSink);
        }
        return held;
    }

    private ConductorAdvanceResult? RouteRecordedCohortAttributionFailure(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        var failure = goal.LatestAcceptanceFailure;
        if (failure?.CheckAttributions?.Any(attribution =>
                attribution.Evidence.StartsWith(ConductorAcceptanceCohortAttributionVerdicts.EvidencePrefix,
                    StringComparison.Ordinal)) != true)
        {
            return null;
        }

        var path = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
        if (path is null)
        {
            return null;
        }
        GateReadyCandidateRevisionPair current;
        try { current = ConductorGitRevisionReader.ReadRequiredPair(path); }
        catch (InvalidOperationException) { return null; }
        if (current.BranchRevision != failure.BranchHeadSha ||
            current.MainRevision != failure.MainHeadSha)
        {
            return null;
        }
        var reason = $"Acceptance verification failed; review and fix before landing. " +
            failure.CheckAttributions![0].Evidence;
        if (_cohortKernel is not null && !_cohortKernel.RouteRecordedAcceptanceFailure(goal.Id, reason))
        {
            throw new InvalidOperationException("Recorded cohort acceptance failure could not route its Verified member.");
        }
        return Escalate(goal, goalPrefix, policy, GoalLifecycleState.AcceptanceFailed, reason,
            ConductorEscalationKind.AcceptanceVerificationFailed);
    }
}
