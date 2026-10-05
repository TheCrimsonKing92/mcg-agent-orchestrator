using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorAdvanceResult? CreateLandingRebaseOutcome(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, string phase,
        bool applySideEffects, GoalWorktreeRebaseResult rebase,
        out ConductorParallelAcceptanceEarlyOutcome? earlyOutcome)
    {
        earlyOutcome = null;
        var decision = LandingRebasePolicy.Evaluate(new LandingRebaseFacts(
            phase, rebase.UpdatedBranch, rebase.Status.ToString(), Array.AsReadOnly(rebase.ConflictFiles.ToArray()),
            rebase.Message, applySideEffects));
        if (decision.Action == LandingRebaseAction.Proceed)
            return null;
        if (decision.Action == LandingRebaseAction.Retire)
        {
            earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetired(GoalLifecycleState.CleanedUp, decision.Reason);
            if (applySideEffects)
                _recordMissingBranchRetirement(goal, decision.Reason);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp) { Decision = decision.ToRecord() });
        }
        earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalated(GoalLifecycleState.Verified, decision.Reason);
        var result = applySideEffects
            ? Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, decision.Reason)
            : MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, decision.Reason));
        return result with { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = decision.ToRecord() } };
    }

    private static LandingCompletionFacts ObserveLandingCompletion(AcceptanceVerificationSummary acceptance) => new()
    {
        ApparatusRun = false, BranchSha = acceptance.BranchHeadSha, MainSha = acceptance.MainHeadSha,
        RunPassed = acceptance.Passed, UnmetCriteriaCount = acceptance.RequiredUnmetCriteria.Count
    };

    private ConductorAdvanceResult CreateApparatusCompletionHold(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance,
        string? branchHeadSha, string? mainHeadSha)
    {
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with
        {
            ApparatusRun = true, BranchSha = branchHeadSha, MainSha = mainHeadSha,
            ApparatusCandidate = FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)
        });
        RecordEscalation(goal, GoalLifecycleState.Verified, decision.Reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason, decision.StableIdentity)
            { Decision = decision.ToRecord() });
    }

    private ConductorAdvanceResult CreateFailedAcceptanceCompletion(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance, bool timedOut) =>
        CreateLandingCompletionEscalation(goal, goalPrefix, policy, ObserveLandingCompletion(acceptance) with
        { TimedOut = timedOut, FailureTail = FormatFailureTail(acceptance.FailureDetail) });

    private static ConductorAdvanceOutcome.Held AttachCriterionEvidenceCompletionDecision(
        AcceptanceVerificationSummary acceptance, ConductorAdvanceOutcome.Held hold)
    {
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with
        { EvidenceHoldReason = hold.Reason, EvidenceHoldState = hold.State, EvidenceHoldIdentity = hold.StableIdentity });
        return hold with { Decision = decision.ToRecord() };
    }

    private ConductorAdvanceResult CreateUnmetCriteriaCompletion(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance,
        string criteria, bool retryTaskAvailable) =>
        CreateLandingCompletionEscalation(goal, goalPrefix, policy, ObserveLandingCompletion(acceptance) with
        {
            UnmetCriteria = criteria, RetryTaskAvailable = retryTaskAvailable,
            RetryCount = goal.AutomaticAcceptanceRetryCount, RetryBudget = policy.MaxCriterionRetries
        });

    private ConductorAdvanceResult CreateLandingCompletionEscalation(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, LandingCompletionFacts facts)
    {
        var decision = LandingCompletionPolicy.Evaluate(facts);
        if (decision.Action != LandingCompletionAction.Escalate)
            throw new InvalidOperationException("Landing completion escalation requires an escalate decision.");
        var result = Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, decision.Reason, decision.EscalationKind);
        return result with { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = decision.ToRecord() } };
    }

    private ConductorAdvanceResult CreateLandingMutationCompletionHold(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance, string mutationBlockReason)
    {
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with { MutationBlockReason = mutationBlockReason });
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason) { Decision = decision.ToRecord() });
    }

    // Single landing-result mapping seam shared with the next landing-decision slice.
    private ConductorAdvanceResult? CreateLandingResultCompletionOutcome(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance, LandingResult landResult)
    {
        var escalate = landResult.Decision as LandingDecision.Escalate;
        var kind = escalate is null ? "promote"
            : LandingExecutor.IsMutationHoldEscalation(escalate.Reason) ? "mutation-hold"
            : LandingExecutor.IsOwnershipHoldEscalation(escalate.Reason) ? "ownership-hold" : "escalate";
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with
        { LandingResultKind = kind, LandingReason = escalate?.Reason });
        if (decision.Action == LandingCompletionAction.Proceed)
            return null;
        if (decision.Action == LandingCompletionAction.Hold)
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason) { Decision = landResult.Decision.Decision ?? decision.ToRecord() });
        if (kind == "ownership-hold")
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, decision.Reason) { Decision = landResult.Decision.Decision ?? decision.ToRecord() });
        var result = Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, decision.Reason);
        return result with { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = landResult.Decision.Decision ?? decision.ToRecord() } };
    }
}
