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
        var facts = new VerifiedAdmissionFacts(goal.Status);
        if (HasActiveOwnerReviewHold(goal, out var ownerSha, out var ownerReceipt))
            return OwnerReviewHeld(goal, goalPrefix, policy, ownerSha, ownerReceipt!,
                VerifiedAdmissionPolicy.Evaluate(facts with
                {
                    OwnerReviewHold = true, OwnerReviewSha = ownerSha, OwnerReviewReason = ownerReceipt!.Reason,
                    OwnerReviewFingerprint = ownerReceipt.Fingerprint ?? "none"
                }));
        facts = facts with { OwnerReviewHold = false };

        if (HasRoutableRecordedCohortAttributionFailure(goal))
        {
            return RouteRecordedCohortAttributionFailure(goal, goalPrefix, policy,
                VerifiedAdmissionPolicy.Evaluate(facts with
                {
                    CohortAttribution = true,
                    CohortAttributionEvidence = goal.LatestAcceptanceFailure!.CheckAttributions![0].Evidence
                }));
        }
        facts = facts with { CohortAttribution = false };
        if (HasActiveApparatusHold(goal, out _))
        {
            var failure = goal.LatestAcceptanceFailure!;
            return AdmissionHeld(facts with
            {
                ApparatusHold = true, ApparatusBranchSha = failure.BranchHeadSha ?? "unknown",
                ApparatusMainSha = failure.MainHeadSha ?? "unknown",
                ApparatusCandidate = FormatAcceptanceCandidate(failure.BranchHeadSha, failure.MainHeadSha)
            });
        }
        facts = facts with { ApparatusHold = false };

        var sharedAcceptance = TryRunFallbackAcceptance(goal, goalPrefix, policy, GoalLifecycleState.Verified);
        if (sharedAcceptance is not null)
        {
            return sharedAcceptance;
        }

        using var evidenceMutationLease = _tryAcquireEvidenceMutationLease(goal, "conductor:acceptance-and-land");
        if (evidenceMutationLease is null)
        {
            var result = ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);
            var held = (ConductorAdvanceOutcome.Held)result.Outcome;
            var decision = VerifiedAdmissionPolicy.Evaluate(facts with
                { EvidenceLease = false, EvidenceLeaseReason = held.Reason });
            return result with { Outcome = held with { Decision = decision.ToRecord() } };
        }
        facts = facts with { EvidenceLease = true };

        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
        {
            return AdmissionHeld(facts with { AllTasksPassed = false });
        }
        facts = facts with { AllTasksPassed = true };

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
            return AdmissionHeld(facts with
            {
                GateStartDeferral = "infrastructure-deferred",
                InfrastructureDeferredReasonCode = ex.ReasonCode, InfrastructureDeferredMessage = ex.Message
            });
        }
        catch (DotnetBuildSlotsBusyException ex)
        {
            return AdmissionHeld(facts with
                { GateStartDeferral = "build-slots-busy", BuildSlotsBusyDetail = FormatSlotsBusy(ex.SlotsBusy) });
        }
        catch (BuildLockBlockedException ex)
        {
            return AdmissionHeld(facts with
                { GateStartDeferral = "build-lock-blocked", BuildLockBlockedDetail = FormatBuildLockBlocked(ex.Attribution) });
        }
        catch (AcceptanceAttemptCancelledException ex)
        {
            return AdmissionHeld(facts with
                { GateStartDeferral = "attempt-cancelled", CancellationProbeCause = ex.Decision.Cause.ToString() });
        }

        return CompleteLandingAfterAcceptance(goal, goalPrefix, policy, acceptance);

        ConductorAdvanceResult AdmissionHeld(VerifiedAdmissionFacts observed)
        {
            var decision = VerifiedAdmissionPolicy.Evaluate(observed);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason,
                    StableIdentity: decision.StableIdentity.Length == 0 ? null : decision.StableIdentity)
                { Decision = decision.ToRecord() });
        }
    }

    private ConductorAdvanceResult ExecuteVerifying(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy)
    {
        if (!_isConductorTick)
        {
            var decision = VerifyingStagePolicy.Evaluate(new(
                GoalLifecycleState.Verifying, _isConductorTick, goal.Status));
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verifying,
                    decision.Reason) { Decision = decision.ToRecord() });
        }

        var fallback = TryRunFallbackAcceptance(goal, goalPrefix, policy, GoalLifecycleState.Verifying, out var facts);
        if (fallback is not null)
            return fallback;

        var background = VerifyingStagePolicy.Evaluate(facts);
        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verifying,
                background.Reason) { Decision = background.ToRecord() });
    }

    private ConductorAdvanceResult? TryRunFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState callerState)
        => TryRunFallbackAcceptance(goal, goalPrefix, policy, callerState, out _);

    private ConductorAdvanceResult? TryRunFallbackAcceptance(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState callerState,
        out VerifyingStageFacts facts)
    {
        facts = new(callerState, _isConductorTick, goal.Status,
            ParallelAcceptanceEnabled: _parallelAcceptanceEnabled);

        ConductorAdvanceResult Hold(GoalLifecycleState state, VerifyingStageFacts observed,
            ConductorHoldOwner owner = ConductorHoldOwner.None)
        {
            var hold = VerifyingStagePolicy.Evaluate(observed);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(state, hold.Reason) { Owner = owner, Decision = hold.ToRecord() });
        }

        if (!_parallelAcceptanceEnabled)
        {
            return null;
        }

        if (goal.Status == GoalStatus.Completed)
        {
            return Hold(GoalLifecycleState.Verified, facts);
        }

        ConductorParallelAcceptanceCandidate? candidate;
        try
        {
            candidate = TryBuildParallelAcceptanceCandidate(goal, policy, slotIndex: 0, out _);
        }
        catch (EvidenceMutationLeaseUnavailableException)
        {
            var result = ReplacementEvidenceMutationHeld(goal, goalPrefix, policy);
            var held = (ConductorAdvanceOutcome.Held)result.Outcome;
            facts = facts with { ReplacementLeaseAvailable = false, ReplacementLeaseReason = held.Reason };
            var hold = VerifyingStagePolicy.Evaluate(facts);
            return result with { Outcome = held with { Decision = hold.ToRecord() } };
        }

        facts = facts with { ReplacementLeaseAvailable = candidate is null ? null : true, CandidateBuilt = candidate is not null };
        if (candidate is null)
        {
            return null;
        }

        if (_isConductorTick &&
            policy.AcceptanceWidth >= ConductorAutonomyPolicy.MinimumAcceptanceWidth &&
            _parallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]).Count == 0)
        {
            // Capture at launch time so attempts started earlier in this tick consume width.
            var admission = ConductorBatchLoop.DecideFallbackAcceptanceAdmission(
                _parallelAcceptanceAttemptCoordinator,
                _conductorTickKernel?.Goals.Where(active => active.Status != GoalStatus.Completed).ToArray() ?? [goal],
                GetActiveAcceptanceCohortCapacity(),
                goal.Id.Value,
                Math.Min(GetAcceptanceSlotCount(goal), policy.AcceptanceWidth),
                _conductorTick);
            facts = facts with { AdmissionAdmitted = admission.IsAdmitted, AdmissionReason = admission.Reason };
            if (!admission.IsAdmitted)
            {
                return Hold(callerState, facts);
            }
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
        var execution = VerifyingAttemptExecutor.Execute(
            _isConductorTick,
            exhaustion => _parallelAcceptanceAttemptCoordinator.Evaluate(candidate, policy, runAcceptance, exhaustion),
            attempt => _parallelAcceptanceAttemptCoordinator.ObserveExistingAttempt(attempt, candidate),
            attempt => EmitNoTickAcceptanceLifecycle(candidate, "started", attempt),
            _utcNow,
            _noTickAcceptancePollDelay,
            NoTickAcceptancePollInterval,
            _noTickAcceptancePollTimeout);
        if (execution.ArtifactWriterBusyMessage is { } busyMessage)
        {
            facts = facts with { ArtifactWriterBusy = true, ArtifactWriterMessage = busyMessage };
            return Hold(GoalLifecycleState.Verified, facts);
        }

        var decision = execution.Decision!;
        facts = facts with
        {
            ArtifactWriterBusy = false,
            AttemptDecisionKind = decision.Kind.ToString(),
            AttemptId = decision.Attempt.AttemptId
        };
        if (execution.NoTickWaitOutcome is { } waitOutcome)
        {
            facts = facts with { NoTickWaitOutcome = waitOutcome };
            return Hold(GoalLifecycleState.Verified, facts,
                waitOutcome == VerifyingAttemptExecutor.DeadlineElapsed
                    ? ConductorHoldOwner.BackgroundAttempt
                    : ConductorHoldOwner.None);
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

                facts = facts with { GoalStatus = goal.Status };
                return Hold(GoalLifecycleState.Verifying, facts);

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
