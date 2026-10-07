using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly HashSet<string> _followerStartedLeaderAttemptIds = new(StringComparer.Ordinal);

    private void AdmitFollowerGate(ParallelAcceptanceBatchState state, AgentOrchestratorKernel kernel,
        ConductorDriver driver, ConductorAutonomyPolicy policy, int tick, List<string> changedGoalLines)
    {
        if (!policy.FollowerGatesEnabled) return;
        foreach (var goal in state.OrderedEligible)
        {
            if (state.Results.ContainsKey(goal.Id.Value)) continue;
            var hold = driver.ReadFollowerGateHold(goal.Id);
            if (hold is null || !FollowerGateAdmissionRule.HoldForLeader(hold.Receipt.Outcome, hold.Leader, hold.LiveBase)) continue;
            HoldFollower(goal, hold.Receipt.Binding.LeaderGoalId.Value[..8], "follower-awaiting-leader");
        }
        var running = state.LiveAttempts.Where(attempt =>
            attempt.Kind == ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind &&
            attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running).ToArray();
        var leaderAttempt = running.Length == 1 ? running[0] : null;
        var leader = leaderAttempt is null ? null : kernel.Goals.SingleOrDefault(goal => goal.Id.Value == leaderAttempt.GoalId);
        Goal? follower = null;
        GateReadyCandidateProjection? followerProjection = null;
        foreach (var goal in state.OrderedEligible)
        {
            if (goal.Id == leader?.Id || state.Results.ContainsKey(goal.Id.Value) || state.LiveAttemptGoalIds.Contains(goal.Id.Value)) continue;
            if (driver.ProjectGateReadyCandidate(goal, policy) is not GateReadyCandidateProjectionResult.Ready ready) continue;
            follower = goal;
            followerProjection = ready.Projection;
            break;
        }
        var decision = FollowerGateAdmissionRule.Decide(true,
            DecideLiveAcceptanceAdmission(state.AcceptanceCensus, state.ConfiguredAcceptanceWidth).IsAdmitted,
            running.Length, leaderAttempt is not null &&
                (_followerStartedLeaderAttemptIds.Contains(leaderAttempt.AttemptId) ||
                 driver.HasFollowerGateForLeader(new(leaderAttempt.GoalId), leaderAttempt.BranchHeadSha!)), follower is not null);
        var leaderPrefix = leader?.Id.Value[..8] ?? "-";
        if (decision != FollowerGateAdmissionDecision.Start)
        {
            Report(follower, leaderPrefix, "skipped", decision.ToString());
            return;
        }
        if (leader is null || driver.ProjectFollowerLeader(leader, policy) is not GateReadyCandidateProjectionResult.Ready leaderReady ||
            !string.Equals(leaderReady.Projection.BranchRevision, leaderAttempt!.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(leaderReady.Projection.MainRevision, leaderAttempt.MainHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            Report(follower, leaderPrefix, "skipped", "leader-projection-mismatch");
            return;
        }
        var outcome = driver.StartFollowerGateForAdmission(leaderReady.Projection, followerProjection!, policy);
        if (outcome is FollowerGateStartOutcome.Started)
        {
            _followerStartedLeaderAttemptIds.Add(leaderAttempt!.AttemptId);
            HoldFollower(follower!, leaderPrefix, "follower-gate-running");
            state.ActiveCohortCapacity = driver.GetActiveAcceptanceCohortCapacity();
            state.AcceptanceCensus = CaptureLiveAcceptanceCensus(state.LiveAttempts, state.ActiveAttemptIds,
                state.ActiveCohortCapacity, tick, changedGoalLines, blockAdmissionOnFailure: true);
        }
        else Report(follower, leaderPrefix, "skipped", outcome.GetType().Name);

        void HoldFollower(Goal goal, string leaderId, string reason)
        {
            state.Results[goal.Id.Value] = new(CohortMemberHeld(goal, policy, $"{reason} leader={leaderId}"), SlotIndex: null);
            Report(goal, leaderId, "held", reason);
        }
        void Report(Goal? goal, string leaderId, string result, string reason) => RecordParallelAcceptanceProgress(
            $"FOLLOWER_GATE tick={tick} follower={goal?.Id.Value[..8] ?? "-"} leader={leaderId} result={result} reason={reason}", changedGoalLines);
    }

    private void CarryFollowerGateReceipts(ParallelAcceptanceBatchState state, ConductorDriver driver,
        ConductorAutonomyPolicy policy, int tick, List<string> changedGoalLines)
    {
        if (!policy.FollowerGatesEnabled) return;
        foreach (var goal in state.CohortEligible)
        {
            if (state.Results.ContainsKey(goal.Id.Value) || goal.Status != GoalStatus.Verified) continue;
            var carry = driver.CarryFollowerGateReceipt(goal, policy);
            if (carry is null) continue;
            if (carry.Result is not null) state.Results[goal.Id.Value] = new(carry.Result, SlotIndex: 0);
            RecordParallelAcceptanceProgress($"FOLLOWER_GATE tick={tick} follower={goal.Id.Value[..8]} " +
                $"leader={carry.Leader} result={(carry.Result is null ? "discard" : "completed")} reason={carry.Reason}", changedGoalLines);
        }
        state.CohortEligible = state.CohortEligible.Where(goal => !state.Results.ContainsKey(goal.Id.Value)).ToArray();
        state.ProductionCandidates = state.ProductionCandidates.Where(candidate => !state.Results.ContainsKey(candidate.GoalId.Value))
            .Select(candidate => new ConductorSpeculativeAcceptanceCandidate(candidate.GoalId,
                driver.ProjectGateReadyCandidate(state.CohortEligible.Single(goal => goal.Id == candidate.GoalId), policy))).ToArray();
    }
}
