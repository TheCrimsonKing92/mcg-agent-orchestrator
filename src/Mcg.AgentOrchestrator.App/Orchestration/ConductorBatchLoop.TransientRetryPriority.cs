using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private enum SoloAcceptanceAdmissionKind
    {
        Admissible, Completed, VerificationGate, EngineCircuit, SlotSettings,
        Capacity, Fairness, CandidateUnavailable, ResourceConflict
    }

    private sealed record SoloAcceptanceAdmission(
        SoloAcceptanceAdmissionKind Kind,
        ConductorParallelAcceptanceCandidate? Candidate = null,
        string? Reason = null,
        string? ProgressLine = null,
        LiveAcceptanceAdmissionDecision? CapacityDecision = null,
        ParallelAcceptanceOldestWaiterObservation? StalledOldestBypass = null);

    // Observe without escalating or holding: grouped selection and solo admission share every skip.
    private SoloAcceptanceAdmission EvaluateSoloAcceptanceAdmissibility(
        AgentOrchestratorKernel kernel, ConductorDriver driver, Goal goal,
        ConductorAutonomyPolicy policy, LiveAcceptanceCensus census, int configuredWidth,
        IReadOnlySet<int> activeSlots, IReadOnlyList<ConductorParallelAcceptanceCandidate> activeCandidates,
        IReadOnlySet<string> liveGoalIds, ParallelAcceptanceOldestWaiterObservation oldestObservation,
        bool oldestServedThisTick, int tick)
    {
        if (goal.Status == GoalStatus.Completed)
        {
            return new(SoloAcceptanceAdmissionKind.Completed,
                Reason: "Completed goal requires operator acceptance; background acceptance cannot reopen a landed goal.",
                ProgressLine: $"ADMISSION tick={tick} result=held reason=completed-goal-operator-acceptance goal={goal.Id.Value[..8]}");
        }

        var verificationGate = kernel.BuildVerificationGate(goal.Id);
        if (!verificationGate.IsSatisfied)
        {
            var blockingReasons = string.Join(',', verificationGate.Tasks
                .Where(task => task.GateStatus != VerificationGateStatus.Passed)
                .Select(task => $"{task.Role}:{task.Reason}"));
            var reason = BoundSingleLine(
                $"inconsistent {goal.Status} state: authoritative task verification gate unsatisfied ({blockingReasons}); " +
                "apply verify-manual or retry before acceptance");
            return new(SoloAcceptanceAdmissionKind.VerificationGate, Reason: reason,
                ProgressLine: $"ADMISSION tick={tick} result=escalated reason=authoritative-verification-gate-unsatisfied goal={goal.Id.Value[..8]} detail={SanitizeReason(reason)}");
        }

        var engineHealth = _acceptanceEngineCircuit?.Read();
        if (IsAcceptanceEngineCircuitHoldRequired(goal.Status, engineHealth))
        {
            return new(SoloAcceptanceAdmissionKind.EngineCircuit,
                Reason: BuildAcceptanceEngineHoldReason(engineHealth!),
                ProgressLine: $"ADMISSION tick={tick} result=held reason=acceptance-engine-circuit goal={goal.Id.Value[..8]} health={engineHealth!.Health}");
        }

        int slotCount;
        try
        {
            slotCount = driver.GetAcceptanceSlotCount(goal);
            if (slotCount is < 1 || slotCount > MaxParallelAcceptanceCapacity)
            {
                throw new InvalidDataException(
                    $"Acceptance slot count {slotCount} must be between 1 and maximum {MaxParallelAcceptanceCapacity}.");
            }
            slotCount = Math.Min(slotCount, configuredWidth);
        }
        catch (Exception ex)
        {
            return new(SoloAcceptanceAdmissionKind.SlotSettings,
                Reason: $"invalid parallel acceptance slot settings: {SanitizeReason(ex.Message)}",
                ProgressLine: $"ADMISSION tick={tick} result=escalated reason=parallel-acceptance-slot-settings goal={goal.Id.Value[..8]} detail={SanitizeReason(ex.Message)}");
        }

        var capacity = DecideLiveAcceptanceAdmission(census, slotCount);
        if (!capacity.IsAdmitted)
        {
            return new(SoloAcceptanceAdmissionKind.Capacity, CapacityDecision: capacity);
        }

        var oldestWaiter = oldestObservation.Waiter;
        var fairnessApplies = !liveGoalIds.Contains(goal.Id.Value) &&
            oldestWaiter is not null && goal.Id != oldestWaiter.Id &&
            !oldestServedThisTick && ShouldDeferForParallelAcceptanceFairness(oldestWaiter.Id.Value);
        var candidate = TryBuildParallelAcceptanceCandidate(driver, goal, policy,
            SelectAvailableParallelAcceptanceSlot(activeSlots, slotCount), out var buildException);
        if (candidate is null)
        {
            return buildException is null
                ? new(SoloAcceptanceAdmissionKind.CandidateUnavailable)
                : new(SoloAcceptanceAdmissionKind.CandidateUnavailable,
                    Reason: FormatParallelAcceptanceCandidateUnavailable(buildException),
                    ProgressLine: $"ADMISSION tick={tick} result=held reason=parallel-acceptance-candidate goal={goal.Id.Value[..8]} detail={FormatParallelAcceptanceCandidateUnavailableDetail(buildException)}");
        }
        if (fairnessApplies && !oldestObservation.IsStalled)
        {
            return new(SoloAcceptanceAdmissionKind.Fairness, candidate,
                $"parallel acceptance fairness waiting for oldest verified goal {oldestWaiter!.Id.Value[..8]}; retry on next conduct tick",
                $"ADMISSION tick={tick} result=deferred reason=parallel-acceptance-fairness goal={goal.Id.Value[..8]} oldest={oldestWaiter.Id.Value[..8]}");
        }
        if (activeCandidates.Any(existing => existing.Overlaps(candidate)))
        {
            return new(SoloAcceptanceAdmissionKind.ResourceConflict, candidate,
                "parallel acceptance resource conflict; retry on next conduct tick");
        }
        return new(SoloAcceptanceAdmissionKind.Admissible, candidate,
            StalledOldestBypass: fairnessApplies ? oldestObservation : null);
    }

    private static ParallelLandingOutcome? ApplySoloAcceptanceHold(
        SoloAcceptanceAdmission admission, ConductorDriver driver, Goal goal,
        ConductorAutonomyPolicy policy, List<string> changedGoalLines)
    {
        var result = admission.Kind switch
        {
            SoloAcceptanceAdmissionKind.VerificationGate =>
                EscalateParallelAcceptanceSafely(driver, goal, policy, admission.Reason!),
            SoloAcceptanceAdmissionKind.SlotSettings =>
                driver.EscalateParallelLandingAcceptance(goal, policy, admission.Reason!),
            SoloAcceptanceAdmissionKind.Capacity =>
                AdmissionDeniedHeld(goal, policy, admission.CapacityDecision!),
            SoloAcceptanceAdmissionKind.Fairness or SoloAcceptanceAdmissionKind.ResourceConflict =>
                ParallelAcceptanceHeld(admission.Candidate!, policy, admission.Reason!),
            SoloAcceptanceAdmissionKind.Completed or SoloAcceptanceAdmissionKind.EngineCircuit =>
                ParallelAcceptanceHeld(goal, policy, admission.Reason!),
            SoloAcceptanceAdmissionKind.CandidateUnavailable when admission.Reason is not null =>
                ParallelAcceptanceHeld(goal, policy, admission.Reason),
            SoloAcceptanceAdmissionKind.CandidateUnavailable => null,
            _ => throw new InvalidOperationException($"Cannot hold solo admission {admission.Kind}.")
        };
        if (admission.ProgressLine is not null)
        {
            RecordParallelAcceptanceProgress(admission.ProgressLine, changedGoalLines);
        }
        return result is null ? null : new ParallelLandingOutcome(result, null);
    }

    private static bool HoldAfterSingleSlotTransientRetry(
        Goal goal, Goal? priorityGoal, int configuredWidth, ConductorAutonomyPolicy policy,
        Dictionary<string, ParallelLandingOutcome> results)
    {
        if (configuredWidth != 1 || priorityGoal is null || goal.Id == priorityGoal.Id ||
            !results.TryGetValue(priorityGoal.Id.Value, out var retryOutcome) ||
            retryOutcome.SlotIndex is null)
        {
            return false;
        }

        // The priority retry consumes this tick's single admission even if it finishes inline.
        // Keep this a queue hold; a terminal attempt must not be reported as a live occupant.
        results[goal.Id.Value] = new ParallelLandingOutcome(
            ParallelAcceptanceHeld(goal, policy,
                $"acceptance slot served transient retry {priorityGoal.Id.Value[..8]}; retry on next conduct tick",
                ConductorHoldOwner.AcceptanceQueue), null);
        return true;
    }

    private Goal? SelectTransientRetryPriorityGoal(
        AgentOrchestratorKernel kernel, ConductorDriver driver, IReadOnlyList<Goal> eligible,
        ConductorAutonomyPolicy policy, LiveAcceptanceCensus census, int configuredWidth,
        IReadOnlySet<int> activeSlots, IReadOnlyList<ConductorParallelAcceptanceCandidate> activeCandidates,
        IReadOnlySet<string> liveGoalIds, ParallelAcceptanceOldestWaiterObservation oldestObservation,
        Dictionary<string, ParallelLandingOutcome> results, int tick, List<string> changedGoalLines)
    {
        foreach (var goal in eligible)
        {
            if (goal.Status != GoalStatus.Verifying || results.ContainsKey(goal.Id.Value)) continue;
            // Candidate/head/probe faults retain the existing solo hold or escalation path.
            SoloAcceptanceAdmission admission;
            int count;
            try
            {
                admission = EvaluateSoloAcceptanceAdmissibility(kernel, driver, goal, policy,
                    census, configuredWidth, activeSlots, activeCandidates, liveGoalIds,
                    oldestObservation, oldestServedThisTick: false, tick);
                if (admission.Kind != SoloAcceptanceAdmissionKind.Admissible) continue;
                count = driver.ParallelAcceptanceAttemptCoordinator.CountPendingTransientRetries(admission.Candidate!);
            }
            catch (Exception)
            {
                continue;
            }
            if (count < 1 || count >= ParallelAcceptanceTransientFailureCap) continue;
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=deferred reason=transient-retry-priority goal={goal.Id.Value[..8]}",
                changedGoalLines);
            return goal;
        }
        return null;
    }
}
