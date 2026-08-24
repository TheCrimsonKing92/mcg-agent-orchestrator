using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private readonly Action<string, string> _acceptanceEventSink;
    private readonly Action<TimeSpan> _noTickAcceptancePollDelay;
    private readonly TimeSpan _noTickAcceptancePollTimeout;
    internal static readonly TimeSpan DefaultNoTickAcceptancePollTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan NoTickAcceptancePollInterval = TimeSpan.FromMilliseconds(100);

    private ConductorAdvanceResult ExecuteLanding(Goal goal, string goalPrefix, ConductorAutonomyPolicy policy)
    {
        var sharedAcceptance = TryRunFallbackAcceptance(goal, goalPrefix, policy);
        if (sharedAcceptance is not null)
        {
            return sharedAcceptance;
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
            acceptance = _runAcceptanceVerification(goal, null, null, CancellationToken.None);
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

    private ConductorAdvanceResult ExecuteVerifying(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy) =>
        TryRunFallbackAcceptance(goal, goalPrefix, policy) ??
        MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verifying,
                "Acceptance gate running in background; reconciliation will handle terminal artifact"));

    private ConductorAdvanceResult? TryRunFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        if (!_parallelAcceptanceEnabled)
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
            RunParallelLandingAcceptance,
            _isConductorTick
                ? AcceptanceStableSlotExhaustionPolicy.Fail
                : AcceptanceStableSlotExhaustionPolicy.DegradeToSerial);
        if (_isConductorTick &&
            decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started &&
            decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
        {
            decision = _parallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                RunParallelLandingAcceptance);
        }

        if (!_isConductorTick &&
            decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started)
            {
                EmitNoTickAcceptanceLifecycle(candidate, "started", decision.Attempt);
            }
            var deadline = _utcNow().Add(_noTickAcceptancePollTimeout);
            while (decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
                   ConductorParallelAcceptanceAttemptDecisionKind.Running)
            {
                if (_utcNow() >= deadline)
                {
                    return MakeResult(
                        goal.Id.Value,
                        goalPrefix,
                        policy,
                        new ConductorAdvanceOutcome.Held(
                            GoalLifecycleState.Verified,
                            $"Acceptance verification remains in background after bounded no-tick wait; attempt={decision.Attempt.AttemptId}."));
                }

                _noTickAcceptancePollDelay(NoTickAcceptancePollInterval);
                try
                {
                    decision = _parallelAcceptanceAttemptCoordinator.ObserveExistingAttempt(
                        decision.Attempt,
                        candidate);
                }
                catch (InvalidDataException ex) when (
                    ex.Message.Contains("after reconciliation", StringComparison.Ordinal))
                {
                    return MakeResult(
                        goal.Id.Value,
                        goalPrefix,
                        policy,
                        new ConductorAdvanceOutcome.Held(
                            GoalLifecycleState.Verified,
                            $"Acceptance attempt reconciliation ownership changed; attempt={decision.Attempt.AttemptId}."));
                }
            }
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
                if (!_isConductorTick)
                {
                    EmitNoTickAcceptanceLifecycle(
                        candidate,
                        ConductorBatchLoop.AcceptanceRunDisposition(run),
                        decision.Attempt);
                }
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
                if (!_isConductorTick)
                {
                    EmitNoTickAcceptanceLifecycle(
                        candidate,
                        ConductorBatchLoop.AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome),
                        decision.Attempt);
                }
                return terminal;

            default:
                throw new InvalidOperationException(
                    $"Unsupported acceptance attempt decision '{decision.Kind}'.");
        }
    }

    private void EmitNoTickAcceptanceLifecycle(
        ConductorParallelAcceptanceCandidate candidate,
        string result,
        ConductorParallelAcceptanceAttempt attempt) =>
        _acceptanceEventSink(
            candidate.Goal.Id.Value,
            AcceptanceLifecycleEventFormatter.Format(
                candidate.GoalPrefix,
                candidate.SlotIndex,
                result,
                attempt.AttemptId,
                tick: 0));

    private static (Action<string, string> EventSink, Action<TimeSpan> PollDelay, TimeSpan PollTimeout)
        CreateProductionAcceptanceWaitConfiguration(OrchestratorWorkspace workspace)
    {
        var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        return ((goalId, detail) => writer.Append("acceptance", goalId, detail), Thread.Sleep,
            DefaultNoTickAcceptancePollTimeout);
    }
}
