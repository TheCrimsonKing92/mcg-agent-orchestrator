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
            partitions.TryGetValue(suppliedGoal.Id, out var partition);
            ApplyCohortAttributionVerdict(receipt, suppliedGoal, partition);
        }
        return held with { MemberResults = memberResults };
    }

    private ConductorAcceptanceCohortAttributionVerdict ApplyCohortAttributionVerdict(
        AcceptanceCohortReceipt receipt,
        Goal suppliedGoal,
        AcceptanceCohortPartitionReceipt? partition)
    {
        var goal = GetCurrentGoal(suppliedGoal);
        string? candidate = null;
        string? main = null;
        if (partition is not null && receipt.AttributedMembers.Any(member => member.GoalId == goal.Id))
        {
            var path = GoalWorktrees.TryResolve(_cohortWorkspace!.ExecutionDirectory, goal.Id);
            if (path is not null)
            {
                try { candidate = ConductorGitRevisionReader.ReadRequiredCommit(path, "HEAD"); }
                catch (InvalidOperationException) { /* A missing current identity cannot authorize failure. */ }
                try { main = ConductorGitRevisionReader.ReadRequiredCommit(path, $"{_integrationBranch}^{{commit}}"); }
                catch (InvalidOperationException) { /* A missing current identity cannot authorize failure. */ }
            }
        }

        var alreadyRecorded = partition is not null &&
            ConductorAcceptanceCohortAttributionVerdicts.IsRecordedFailure(
                goal, receipt.Identity.Value, partition.ReceiptId);
        return ConductorAcceptanceCohortAttributionVerdicts.Apply(
            receipt, goal, partition, candidate, main, alreadyRecorded,
            (member, checks, branch, mainHead, attributions) =>
                _recordAcceptanceFailure(member, checks, branch, mainHead, attributions, null),
            CohortAttributionVerdictSink);
    }

    private ConductorAdvanceResult RouteRecordedCohortAttributionFailure(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        VerifiedAdmissionDecision decision)
    {
        var reason = decision.Reason;
        if (goal.Status == GoalStatus.AcceptanceFailed)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.AcceptanceFailed, reason,
                    ConductorEscalationKind.AcceptanceVerificationFailed) { Decision = decision.ToRecord() });
        }
        if ((_cohortKernel ?? _conductorTickKernel)?.RouteRecordedAcceptanceFailure(goal.Id, reason) != true)
        {
            throw new InvalidOperationException("Recorded cohort acceptance failure could not route its Verified member.");
        }
        var result = Escalate(goal, goalPrefix, policy, GoalLifecycleState.AcceptanceFailed, reason,
            ConductorEscalationKind.AcceptanceVerificationFailed);
        return result with
            { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = decision.ToRecord() } };
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
        try { return ConductorGitRevisionReader.ReadRequiredPair(path, _integrationBranch); }
        catch (InvalidOperationException) { return null; }
    }
}
