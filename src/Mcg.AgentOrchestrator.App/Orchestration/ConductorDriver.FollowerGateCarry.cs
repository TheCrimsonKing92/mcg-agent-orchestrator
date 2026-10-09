using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record FollowerGateHoldObservation(FollowerGateRunReceipt Receipt,
    FollowerLeaderGateStatus Leader, FollowerLiveBaseState LiveBase);
internal sealed record FollowerGateCarryResult(ConductorAdvanceResult? Result, string Leader, string Reason);

internal sealed partial class ConductorDriver
{
    private const string FollowerCarryOperation = "conductor:acceptance-follower-carry";
    private const string FollowerDiscardOperation = "conductor:acceptance-follower-discard";
    private Func<GateReadyCandidateProjection, GateReadyCandidateProjection, ConductorAutonomyPolicy,
        FollowerGateStartOutcome>? _startFollowerGateOverride;
    internal Func<GateReadyCandidateProjection, GateReadyCandidateProjection, ConductorAutonomyPolicy,
        FollowerGateStartOutcome>? FollowerGateStartOverride { set => _startFollowerGateOverride = value; }
    internal Func<GoalId, FollowerGateHoldObservation?>? FollowerGateHoldOverride { private get; set; }

    internal FollowerGateStartOutcome StartFollowerGateForAdmission(GateReadyCandidateProjection leader,
        GateReadyCandidateProjection follower, ConductorAutonomyPolicy policy) =>
        _startFollowerGateOverride?.Invoke(leader, follower, policy) ?? StartFollowerGate(leader, follower, policy);

    internal bool HasFollowerGateForLeader(GoalId leader, string candidate) =>
        _groupedGateAttempts?.ReadAll().Any(attempt => attempt.Kind == "follower" &&
            attempt.Members[0].GoalId == leader.Value && attempt.Members[0].CandidateRevision == candidate) == true;

    internal GateReadyCandidateProjectionResult ProjectFollowerLeader(Goal goal, ConductorAutonomyPolicy policy)
    {
        if (goal.Status != GoalStatus.Verifying) return ProjectGateReadyCandidate(goal, policy);
        if (HasActiveOwnerReviewHold(goal, out _, out _) || HasActiveApparatusHold(goal, out _))
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.LifecycleNotReady);
        var risk = _classifyChangeRisk(goal);
        return _gateReadyCandidateProjector?.Project(new(goal.Id, GoalLifecycleState.Verified,
            _isVerificationGateSatisfied(goal), risk,
            risk.HasValue ? policy.GetTransitionDecision(GoalLifecycleState.Merged, risk.Value) : null)) ??
            ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown);
    }

    internal FollowerGateHoldObservation? ReadFollowerGateHold(GoalId follower)
    {
        if (FollowerGateHoldOverride is { } hook) return hook(follower);
        if (_cohortWorkspace is null || _executionDirectory is null) return null;
        var receipt = FollowerGateStore.ReadReceiptsForFollower(follower).FirstOrDefault();
        if (receipt is null || FollowerReceiptConsumed(follower, receipt.ReceiptId)) return null;
        var main = ReadFollowerRevision(_executionDirectory, $"refs/heads/{_integrationBranch}");
        return new(receipt, ReadFollowerReceiptLeaderStatus(receipt.Binding),
            FollowerGateBindingRule.ClassifyLiveBase(receipt.Binding.BaseMainRevision,
                receipt.Binding.LeaderCandidateTree, main,
                main is null ? null : ReadFollowerRevision(_executionDirectory, $"{main}^1"),
                main is null ? null : ReadFollowerRevision(_executionDirectory, $"{main}^{{tree}}")));
    }

    private bool FollowerReceiptConsumed(GoalId follower, string receiptId) =>
        GoalOperationJournal.Read(_executionDirectory!, follower).Entries.Any(entry =>
            entry.Operation is FollowerCarryOperation or FollowerDiscardOperation &&
            (entry.Detail ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains($"receipt={receiptId}"));

    private FollowerLeaderGateStatus ReadFollowerReceiptLeaderStatus(FollowerGateReceipt binding)
    {
        var goal = _cohortKernel?.Goals.SingleOrDefault(goal => goal.Id == binding.LeaderGoalId);
        if (goal is null) return FollowerLeaderGateStatus.Unknown;
        return goal.Status switch
        {
            GoalStatus.Completed => FollowerLeaderGateStatus.Landed,
            GoalStatus.AcceptanceFailed or GoalStatus.Failed => FollowerLeaderGateStatus.Failed,
            GoalStatus.Verified or GoalStatus.Verifying =>
                ReadFollowerRevision(_executionDirectory!, $"refs/heads/{GoalWorktrees.BranchName(goal.Id)}") ==
                    binding.LeaderCandidateRevision ? FollowerLeaderGateStatus.Pending : FollowerLeaderGateStatus.Stale,
            _ => FollowerLeaderGateStatus.Cancelled
        };
    }

    internal FollowerGateCarryResult? CarryFollowerGateReceipt(Goal follower, ConductorAutonomyPolicy policy)
    {
        if (!policy.FollowerGatesEnabled) return null;
        if (_cohortWorkspace is null || _executionDirectory is null || follower.Status != GoalStatus.Verified) return null;
        var receipt = FollowerGateStore.ReadReceiptsForFollower(follower.Id).FirstOrDefault();
        if (receipt is null || FollowerReceiptConsumed(follower.Id, receipt.ReceiptId)) return null;
        var leader = ReadFollowerReceiptLeaderStatus(receipt.Binding);
        if (leader == FollowerLeaderGateStatus.Pending) return null;
        var prefix = follower.Id.Value[..8];
        var leaderPrefix = receipt.Binding.LeaderGoalId.Value[..8];
        if (ProjectGateReadyCandidate(follower, policy) is not GateReadyCandidateProjectionResult.Ready ready) return null;
        var headBeforeRebase = ready.Projection.BranchRevision;
        var rebase = RebaseBeforeMerge(follower, prefix, policy, out _);
        if (rebase is not null) return new(rebase, leaderPrefix, "rebase");
        var worktree = GoalWorktrees.TryResolve(_executionDirectory, follower.Id) ??
            throw new InvalidOperationException("Follower landing worktree is unavailable.");
        var main = ReadFollowerRevision(_executionDirectory, $"refs/heads/{_integrationBranch}");
        var head = ReadFollowerRevision(worktree, "HEAD");
        var observation = new FollowerGateLandingObservation(leader switch
        {
            FollowerLeaderGateStatus.Landed => FollowerLeaderOutcome.Landed,
            FollowerLeaderGateStatus.Failed => FollowerLeaderOutcome.Failed,
            FollowerLeaderGateStatus.Stale => FollowerLeaderOutcome.Stale,
            _ => FollowerLeaderOutcome.Cancelled
        }, main is null ? null : ReadFollowerRevision(_executionDirectory, $"{main}^1"),
            main is null ? null : ReadFollowerRevision(_executionDirectory, $"{main}^{{tree}}"),
            headBeforeRebase, head is null ? null : ReadFollowerRevision(worktree, $"{head}^{{tree}}"),
            (_cohortAcceptanceVerifier ?? throw new InvalidOperationException("Follower verifier is unavailable."))
                .ComputeEffectivePlanIdentity(worktree, ready.Projection.LandingPaths));
        var decision = FollowerGateBindingRule.Decide(receipt, observation);
        var detail = $"identity={receipt.IdentityValue} receipt={receipt.ReceiptId} leader={leaderPrefix}";
        if (decision.Disposition == FollowerGateDisposition.Discard)
        {
            var reason = decision.Reason.HasValue ? FollowerGateBindingRule.WireName(decision.Reason.Value) : "infrastructure";
            GoalOperationJournal.Completed(_executionDirectory, follower, FollowerDiscardOperation,
                $"{detail} reason={reason}", main);
            return new(null, leaderPrefix, reason);
        }
        var passed = decision.Disposition == FollowerGateDisposition.LandFollower;
        if (passed)
            GoalOperationJournal.AcceptancePassed(_executionDirectory, follower, FollowerCarryOperation, head, main, detail);
        var checks = passed ? Array.Empty<string>() : receipt.FailedChecks.Count > 0 ? receipt.FailedChecks : ["follower-gate"];
        if (!passed)
        {
            var kernel = _cohortKernel ?? _conductorTickKernel ??
                throw new InvalidOperationException("Follower failure reconciliation requires the authoritative kernel.");
            kernel.BeginGoalAcceptanceVerification(follower.Id, $"Reconciling follower gate receipt {receipt.ReceiptId}.");
            kernel.ReconcileGoalAcceptanceFailed(follower.Id, checks,
                $"Follower gate receipt {receipt.ReceiptId} failed after its leader landed exactly.", head, main);
        }
        var result = CompleteLandingAfterAcceptance(follower, prefix, policy,
            new AcceptanceVerificationSummary(passed, [], FailedChecks: checks,
                BranchHeadSha: head, MainHeadSha: main, TestResultPaths: receipt.GateTestResultPaths));
        return new(result, leaderPrefix, passed ? "LandFollower" : "ChargeFollower");
    }
}
