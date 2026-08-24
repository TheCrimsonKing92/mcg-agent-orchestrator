using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static readonly TimeSpan InlineLandingStableSlotLeaseTimeout = TimeSpan.FromSeconds(2);

    private ConductorAdvanceResult ExecuteLanding(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        var backgroundAcceptance = TryRunTickFallbackAcceptance(goal, goalPrefix, policy);
        if (backgroundAcceptance is not null)
        {
            return backgroundAcceptance;
        }

        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(goal, "conductor:acceptance-and-land");
        if (evidenceMutationLease is null)
            return ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);

        if (!HasCompletedPassedVerificationForAllTasks(goal))
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    "Goal is not ready for acceptance: complete every task with a passed verification before accepting this gate."));
        }

        var early = RebaseBeforeAcceptance(goal, goalPrefix, policy, applySideEffects: true, out _);
        if (early is not null)
        {
            return early;
        }

        // Gate 2: acceptance verification (test suite quality check) on the integrated worktree.
        AcceptanceVerificationSummary acceptance;
        try
        {
            acceptance = RunInlineLandingAcceptance(goal);
        }
        catch (AcceptanceInfrastructureDeferredException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Acceptance infrastructure deferred ({ex.ReasonCode}); retry on next conduct tick. {ex.Message}"));
        }
        catch (DotnetBuildSlotsBusyException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Stable dotnet build slots busy; retry on next conduct tick. {FormatSlotsBusy(ex.SlotsBusy)}"));
        }
        catch (BuildLockBlockedException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Build artifact lock blocked acceptance; retry on next conduct tick. {FormatBuildLockBlocked(ex.Attribution)}"));
        }
        catch (AcceptanceAttemptCancelledException ex)
        {
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Acceptance attempt stopped by cancellation probe ({ex.Decision.Cause}); retry when the goal is eligible."));
        }

        return CompleteLandingAfterAcceptance(goal, goalPrefix, policy, acceptance);
    }

    private AcceptanceVerificationSummary RunInlineLandingAcceptance(Goal goal)
    {
        var sourceSizeFailure = RunInlineLandingSourceSizePreflight(goal);
        if (sourceSizeFailure is not null)
        {
            return sourceSizeFailure;
        }

        var acquisition = _tryAcquireLandingStableSlotLease(goal);
        if (acquisition is not DotnetBuildLeaseAcquisition.Acquired acquired)
        {
            RecordLandingStableSlotDegradation(goal, acquisition);
            return _runAcceptanceVerification(goal, null, null, CancellationToken.None);
        }

        var stableSlotLease = acquired.Lease;
        var stableSlotIndex = stableSlotLease.Environment.BuildPermitIndex;
        try
        {
            return _runAcceptanceVerification(
                goal,
                stableSlotIndex,
                stableSlotLease,
                CancellationToken.None);
        }
        finally
        {
            TryDisposeInlineLandingStableSlotLease(stableSlotLease);
        }
    }

    internal static DotnetBuildLeaseAcquisition TryAcquireInlineLandingStableSlotLease(
        Func<TimeSpan, DotnetBuildLeaseAcquisition>? acquire = null) =>
        (acquire ?? (timeout =>
            DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(timeout)))(
                InlineLandingStableSlotLeaseTimeout);

    private void RecordLandingStableSlotDegradation(Goal goal, DotnetBuildLeaseAcquisition acquisition)
    {
        if (_cohortWorkspace is null && _executionDirectory is null)
        {
            return;
        }

        var (reason, acquisitionDetail) = acquisition switch
        {
            DotnetBuildLeaseAcquisition.SlotsBusy busy => ("slots-busy", FormatSlotsBusy(busy)),
            DotnetBuildLeaseAcquisition.BuildLockBlocked blocked =>
                ("build-lock-blocked", FormatBuildLockBlocked(blocked.Attribution)),
            _ => throw new InvalidOperationException("Only failed stable-slot acquisitions can be recorded as degraded.")
        };
        var detail =
            $"LANDING_STABLE_SLOT_DEGRADED goal={goal.Id.Value[..8]} reason={reason} " +
            $"bound_ms={(long)InlineLandingStableSlotLeaseTimeout.TotalMilliseconds} {acquisitionDetail}";
        var eventPath = _cohortWorkspace?.ConductEventsLogPath ?? Path.Combine(
            _executionDirectory!,
            ".orchestrator",
            "logs",
            ConductEventLogWriter.CurrentFileName);

        try
        {
            if (!new ConductEventLogWriter(eventPath)
                    .AppendRequired("landing-stable-slot-degraded", goal.Id.Value, detail))
            {
                TryWriteLandingStableSlotDegradationFallback($"{detail} event_write=failed");
            }
        }
        catch (Exception ex)
        {
            TryWriteLandingStableSlotDegradationFallback(
                $"{detail} event_write=failed error={ex.GetType().Name}");
        }
    }

    private static void TryWriteLandingStableSlotDegradationFallback(string detail)
    {
        try
        {
            Console.Error.WriteLine(detail);
        }
        catch
        {
            // Observability remains best-effort; it must not replace the acceptance outcome.
        }
    }

    private static void TryDisposeInlineLandingStableSlotLease(DotnetBuildEnvironmentLease? stableSlotLease)
    {
        try
        {
            stableSlotLease?.Dispose();
        }
        catch
        {
            // Acceptance outcome is dispositive; best-effort lease cleanup must not replace it.
        }
    }

    private ConductorAdvanceResult ExecuteVerifying(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy) =>
        TryRunTickFallbackAcceptance(goal, goalPrefix, policy) ??
        MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verifying,
                "Acceptance gate running in background; reconciliation will handle terminal artifact"));

    private ConductorAdvanceResult? TryRunTickFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        if (!_isConductorTick || !_parallelAcceptanceEnabled)
        {
            return null;
        }

        ConductorParallelAcceptanceCandidate? candidate;
        try
        {
            candidate = TryBuildParallelAcceptanceCandidate(goal, policy, slotIndex: 0);
        }
        catch (EvidenceMutationLeaseUnavailableException)
        {
            return ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);
        }

        if (candidate is null)
        {
            return null;
        }

        var decision = _parallelAcceptanceAttemptCoordinator.Evaluate(
            candidate,
            policy,
            RunParallelLandingAcceptance);
        if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started &&
            decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
        {
            decision = _parallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                RunParallelLandingAcceptance);
        }

        switch (decision.Kind)
        {
            case ConductorParallelAcceptanceAttemptDecisionKind.Started:
            case ConductorParallelAcceptanceAttemptDecisionKind.Running:
                if (_conductorTickKernel is not null)
                {
                    ConductorBatchLoop.MarkParallelAcceptanceStarted(
                        _conductorTickKernel,
                        goal,
                        decision.Attempt,
                        _conductorTick);
                }

                return MakeResult(
                    goal.Id.Value,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verifying,
                        $"Acceptance verification running in background; attempt={decision.Attempt.AttemptId}."));

            case ConductorParallelAcceptanceAttemptDecisionKind.Completed:
                var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
                    candidate,
                    new InvalidOperationException("Completed acceptance attempt had no run result."));
                if (_conductorTickKernel is not null)
                {
                    ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(
                        _conductorTickKernel,
                        goal,
                        run,
                        decision.Attempt);
                }

                var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
                    this,
                    policy,
                    run,
                    decision.Attempt,
                    out var evidenceMutationLeaseHeld);
                ConductorBatchLoop.MarkParallelAcceptanceReconciledUnlessLeaseHeld(
                    this,
                    run,
                    evidenceMutationLeaseHeld,
                    decision.Attempt);
                return result;

            case ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun:
                if (_conductorTickKernel is not null)
                {
                    ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(
                        _conductorTickKernel,
                        goal,
                        decision.Attempt);
                }

                var terminal = ConductorBatchLoop.ParallelAcceptanceTerminal(
                    this,
                    candidate,
                    policy,
                    decision.Attempt);
                _parallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
                return terminal;

            default:
                throw new InvalidOperationException(
                    $"Unsupported acceptance attempt decision '{decision.Kind}'.");
        }
    }
}
