using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns application of reconciled passes; receipt custody and current-goal checks remain in the driver.
internal static class PassedAcceptanceReceiptReplay
{
    internal sealed record LandingOperations(
        Func<ConductorParallelAcceptanceCandidate, ConductorAdvanceResult?> Rebase,
        Func<(string? BranchHeadSha, string? MainHeadSha)> ResolveHeads,
        Func<ConductorParallelAcceptanceCandidate, AcceptanceVerificationSummary, AcceptanceMainAdvanceDisposition> Carry,
        Func<ConductorParallelAcceptanceCandidate, AcceptanceVerificationSummary, ConductorAdvanceResult> Land,
        Action<ConductorParallelAcceptanceAttempt, string?> RecordLanding);

    internal static ConductorAdvanceResult? Apply(
        ConductorParallelAcceptanceAttemptDecision decision,
        LandingOperations operations)
    {
        var run = decision.Run!;
        var original = run.Candidate;
        var acceptance = run.Acceptance!;
        var rebase = operations.Rebase(original);
        if (rebase is not null)
            return rebase;
        var current = operations.ResolveHeads();
        if (original.BranchHeadSha != current.BranchHeadSha || original.MainHeadSha != current.MainHeadSha)
        {
            var disposition = operations.Carry(original, acceptance);
            if (disposition is not (AcceptanceMainAdvanceDisposition.Unchanged or AcceptanceMainAdvanceDisposition.Disjoint))
                return null; // Fresh validation still goes through ordinary capacity admission.
        }
        var result = operations.Land(original, acceptance);
        if (result.Outcome is ConductorAdvanceOutcome.Executed { Description: var message } &&
            message.StartsWith("Landed:", StringComparison.Ordinal))
            operations.RecordLanding(decision.Attempt, current.MainHeadSha);
        return result;
    }

    internal static bool TryBeforeAdmission(
        int unreconciledAttemptCount,
        Goal goal,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        Func<AcceptanceEngineHealthSnapshot?> readHealth,
        Dictionary<string, ParallelLandingOutcome> results,
        HashSet<GoalId> changedGoalIds,
        int tick,
        List<string> changedGoalLines,
        Action<string, List<string>> recordProgress)
    {
        if (unreconciledAttemptCount != 0)
            return false;
        var health = readHealth();
        var circuitHold = CircuitHold(goal, policy, health);
        if (circuitHold is not null)
        {
            results[goal.Id.Value] = new ParallelLandingOutcome(circuitHold, null);
            recordProgress(
                $"ADMISSION tick={tick} result=held reason=acceptance-engine-circuit goal={goal.Id.Value[..8]} health={health!.Health}",
                changedGoalLines);
            return true;
        }
        var replay = driver.TryReplayPassedAcceptance(goal, policy);
        if (replay is null)
            return true;
        results[goal.Id.Value] = new ParallelLandingOutcome(replay, null);
        changedGoalIds.Add(goal.Id);
        recordProgress($"ADMISSION tick={tick} result=replayed goal={goal.Id.Value[..8]} gatesLaunched=0", changedGoalLines);
        return true;
    }

    internal static ConductorAdvanceResult? CircuitHold(
        Goal goal, ConductorAutonomyPolicy policy, AcceptanceEngineHealthSnapshot? health) =>
        ConductorBatchLoop.IsAcceptanceEngineCircuitHoldRequired(goal.Status, health)
            ? Held(goal, policy, 1, "acceptance-engine-circuit",
                ConductorBatchLoop.BuildAcceptanceEngineHoldReason(health!),
                [new("goalStatus", goal.Status.ToString()), new("health", health!.Health.ToString()),
                    new("landingSha", health.LandingSha ?? string.Empty),
                    new("failureReason", health.FailureReason ?? string.Empty),
                    new("receiptReference", health.ReceiptReference ?? string.Empty)])
            : null;

    internal static ConductorAdvanceResult Complete(
        ConductorDriver driver,
        ConductorParallelAcceptanceRunResult run,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceAttempt attempt,
        out bool evidenceMutationLeaseHeld)
    {
        evidenceMutationLeaseHeld = false;
        // A pass published between draining and admission still needs replay custody and freshness.
        if (attempt.ReconciledAt is not null && run.Acceptance!.Passed)
            return driver.TryReplayPassedAcceptance(run.Candidate.Goal, policy) ??
                Held(run.Candidate.Goal, policy, 2, "passed-receipt-no-longer-replayable",
                    "Passed receipt is no longer replayable; fresh validation required.",
                    [new("attemptId", attempt.AttemptId), new("attemptOutcome", attempt.Outcome.ToString()),
                        new("reconciledAt", attempt.ReconciledAt.Value.ToString("O")),
                        new("branchHeadSha", attempt.BranchHeadSha ?? string.Empty),
                        new("mainHeadSha", attempt.MainHeadSha ?? string.Empty), new("replayEligible", "false")]);
        return driver.CompleteParallelLandingAcceptance(run.Candidate, policy, run.Acceptance!, out evidenceMutationLeaseHeld);
    }

    internal static void ReconcileUnlessReused(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.ReconciledAt.HasValue)
        {
            if (attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed)
                throw new InvalidOperationException(
                    $"Only a passed acceptance attempt may be reused after reconciliation: {attempt.AttemptId}.");
            return;
        }
        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
    }

    private static ConductorAdvanceResult Held(Goal goal, ConductorAutonomyPolicy policy,
        int rung, string evidence, string reason, IReadOnlyList<PolicyDecisionFact> facts) =>
        new(goal.Id.Value, goal.Id.Value[..8], policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason)
            {
                Owner = ConductorHoldOwner.None,
                Decision = new PolicyDecisionRecord("acceptance-receipt-replay", "Hold", rung, evidence, reason,
                    Array.AsReadOnly<PolicyDecisionFact>([new("goalId", goal.Id.Value), .. facts]))
            });
}
