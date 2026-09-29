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
        if (RouteRecordedCohortAttributionFailure(goal, goalPrefix, policy) is { } attributedFailure)
        {
            return attributedFailure;
        }
        if (HasActiveApparatusHold(goal, out _))
        {
            var failure = goal.LatestAcceptanceFailure!;
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Acceptance apparatus hold remains active for unchanged candidate " +
                    $"{FormatAcceptanceCandidate(failure.BranchHeadSha, failure.MainHeadSha)}. " +
                    "Repair main or confirm acceptance-retry before another acceptance process starts.",
                    StableIdentity: $"acceptance-apparatus:{failure.BranchHeadSha ?? "unknown"}:{failure.MainHeadSha ?? "unknown"}"));
        }

        var sharedAcceptance = TryRunFallbackAcceptance(goal, goalPrefix, policy);
        if (sharedAcceptance is not null)
        {
            return sharedAcceptance;
        }

        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(goal, "conductor:acceptance-and-land");
        if (evidenceMutationLease is null)
            return ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);

        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
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
            acceptance = RunInlineLandingSourceSizePreflight(goal) ??
                _runAcceptanceVerification(
                    goal,
                    null,
                    null,
                    CancellationToken.None,
                    new AcceptanceRunExecutionOptions());
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
        ConductorAutonomyPolicy policy)
    {
        if (!_isConductorTick)
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verifying,
                    "Acceptance gate is owned by the conduct loop; reconciliation will handle terminal artifact"));
        }

        return TryRunFallbackAcceptance(goal, goalPrefix, policy) ??
            MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verifying,
                    "Acceptance gate running in background; reconciliation will handle terminal artifact"));
    }

    private ConductorAdvanceResult? TryRunFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        if (!_parallelAcceptanceEnabled)
        {
            return null;
        }

        if (goal.Status == GoalStatus.Completed)
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    "Completed goal requires operator acceptance; background acceptance cannot reopen a landed goal."));
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

        ConductorParallelAcceptanceRunAcceptance runAcceptance = _isConductorTick
            ? RunParallelLandingAcceptance
            : (attemptCandidate, attemptPolicy, lease, cancellationToken, executionOptions) =>
                RunParallelLandingAcceptance(
                    attemptCandidate,
                    attemptPolicy,
                    lease,
                    cancellationToken,
                    executionOptions,
                    omitStableSlotIndexWithoutLease: true);
        ConductorParallelAcceptanceAttemptDecision decision;
        try
        {
            decision = _parallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                runAcceptance,
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
                    runAcceptance);
            }
        }
        catch (AcceptanceArtifactWriterLeaseBusyException ex)
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Acceptance artifact writer busy; retry on next conduct tick. {ex.Message}"));
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
                    ex.Message.Contains(" is unreadable.", StringComparison.Ordinal))
                {
                    continue;
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
                catch (InvalidDataException)
                {
                    return MakeResult(
                        goal.Id.Value,
                        goalPrefix,
                        policy,
                        new ConductorAdvanceOutcome.Held(
                            GoalLifecycleState.Verified,
                            $"Acceptance attempt metadata or ownership changed; attempt={decision.Attempt.AttemptId}."));
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
        ConductorParallelAcceptanceAttempt attempt)
    {
        try
        {
            _acceptanceEventSink(
                candidate.GoalPrefix,
                AcceptanceLifecycleEventFormatter.Format(
                    candidate.GoalPrefix,
                    candidate.SlotIndex,
                    result,
                    attempt.AttemptId,
                    tick: 0));
        }
        catch
        {
            // Advisory observability cannot replace a computed acceptance verdict.
        }
    }

    private static (Action<string, string> EventSink, Action<TimeSpan> PollDelay, TimeSpan PollTimeout)
        CreateProductionAcceptanceWaitConfiguration(OrchestratorWorkspace workspace)
    {
        var writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        return ((goalId, detail) => writer.Append("acceptance", goalId, detail), Thread.Sleep,
            DefaultNoTickAcceptancePollTimeout);
    }

    internal ConductorDriver(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalAcceptanceVerifier acceptanceVerifier,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        ConductorParallelAcceptanceAttemptCoordinator parallelAcceptanceAttemptCoordinator)
        : this(kernel, workspace, acceptanceVerifier, agents, profiles)
    {
        _parallelAcceptanceAttemptCoordinator = parallelAcceptanceAttemptCoordinator;
    }

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions) =>
        RunParallelLandingAcceptance(
            candidate,
            policy,
            stableSlotLease,
            cancellationToken,
            executionOptions,
            omitStableSlotIndexWithoutLease: false);

    internal ConductorParallelAcceptanceRunResult RunParallelLandingAcceptance(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions,
        bool omitStableSlotIndexWithoutLease)
    {
        var effectiveCandidate = candidate;
        var evidenceMutationLease = _tryAcquireEvidenceMutationLease(
            candidate.Goal,
            "conductor:parallel-acceptance");
        if (evidenceMutationLease is null)
        {
            return ConductorParallelAcceptanceRunResult.Early(
                candidate,
                ReplacementEvidenceMutationHeld(candidate.Goal, candidate.GoalPrefix, policy),
                null);
        }
        ConductorParallelAcceptanceRunResult result;
        try
        {
            var early = RebaseBeforeAcceptance(
                candidate.Goal,
                candidate.GoalPrefix,
                policy,
                applySideEffects: false,
                out var earlyOutcome);
            if (early is not null)
            {
                result = ConductorParallelAcceptanceRunResult.Early(candidate, early, earlyOutcome);
                return result;
            }

            effectiveCandidate = RefreshParallelAcceptanceCandidate(candidate);
            result = ConductorParallelAcceptanceRunResult.Accepted(
                effectiveCandidate,
                _runAcceptanceVerification(
                    effectiveCandidate.Goal,
                    omitStableSlotIndexWithoutLease && stableSlotLease is null
                        ? null
                        : effectiveCandidate.SlotIndex,
                    stableSlotLease,
                    cancellationToken,
                    executionOptions));
        }
        catch (Exception ex)
        {
            result = ConductorParallelAcceptanceRunResult.Fault(effectiveCandidate, ex);
        }
        finally
        {
            try
            {
                evidenceMutationLease.Dispose();
            }
            catch
            {
                // Cleanup cannot replace a computed acceptance result. Store-backed leases emit
                // typed failure evidence and retain their owner-qualified row for expiry recovery.
            }
        }

        return result;
    }
}
