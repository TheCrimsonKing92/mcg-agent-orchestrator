using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static void ReconcileParallelAcceptanceTerminalState(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        EnsureParallelAcceptanceTerminalIsVerifying(kernel, goal, attempt);
        if (ReconcileIdentityStaleAcceptance(kernel, goal, run, attempt))
        {
            return;
        }
        if (goal.Status != GoalStatus.Verifying)
        {
            return;
        }

        var disposition = AcceptanceRunDisposition(run);
        if (IsPassingAcceptanceRun(run))
        {
            kernel.ReconcileGoalAcceptanceVerified(
                goal.Id,
                $"Batch loop reconciled background acceptance gate {attempt.AttemptId} terminal artifact ({disposition}); goal returned to Verified for deterministic landing classification.");
            return;
        }

        if (IsEnvironmentInterferenceAcceptanceRun(run))
        {
            kernel.ReconcileGoalAcceptanceVerified(
                goal.Id,
                $"Batch loop reconciled background acceptance gate {attempt.AttemptId} environmental interference ({disposition}); goal returned to Verified for re-gating.");
            return;
        }

        if (IsRetryableAcceptanceRun(run))
        {
            return;
        }

        if (run.Acceptance is null && ConductorParallelAcceptanceAttemptCoordinator
                .ClassifyWorkerRegistrationFault(run.Exception) != WorkerRegistrationFaultDisposition.None)
        {
            // Keep classified registration faults distinct; unclassified faults retain AcceptanceFailed.
            return;
        }

        kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            BuildFailedAcceptanceChecks(run, attempt),
            $"Batch loop reconciled background acceptance gate {attempt.AttemptId} terminal artifact ({disposition}); goal moved to AcceptanceFailed.",
            run.Candidate.BranchHeadSha ?? attempt.BranchHeadSha,
            run.Candidate.MainHeadSha ?? attempt.MainHeadSha,
            run.Acceptance?.CheckAttributions,
            run.Acceptance?.BaselineAttestation);
    }

    internal static void ReconcileParallelAcceptanceTerminalState(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt)
    {
        EnsureParallelAcceptanceTerminalIsVerifying(kernel, goal, attempt);
        kernel.ResetAcceptanceIdentityStale(goal.Id);
        if (goal.Status != GoalStatus.Verifying)
        {
            return;
        }

        if (IsRetryableTerminalAttempt(attempt))
        {
            return;
        }

        // Terminal-without-run outcomes are process/artifact infrastructure failures, not positive
        // acceptance-test failures. The caller surfaces the over-budget case as an operator escalation.
        return;
    }

    private static bool IsPassingAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.Exception is null &&
        (run.EarlyResult is not null
            ? !run.EarlyResult.WasEscalated
            : run.Acceptance is { Passed: true });

    private static bool IsRetryableAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.EarlyResult is not null ||
        run.Exception is AcceptanceInfrastructureDeferredException or
            AcceptanceGateEngineException or
            DotnetBuildSlotsBusyException or
            BuildLockBlockedException or
            OperationCanceledException;

    private static bool IsEnvironmentInterferenceAcceptanceRun(ConductorParallelAcceptanceRunResult run) =>
        run.Acceptance is { } acceptance &&
        ConductorDriver.IsEnvironmentalApparatusAcceptanceRun(acceptance);

    private static bool IsRetryableTerminalAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Outcome is ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot
            or ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock
            or ConductorParallelAcceptanceAttemptOutcome.Cancelled ||
        ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(attempt) &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap;

    private static IReadOnlyList<string> BuildFailedAcceptanceChecks(
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (run.Acceptance is { } acceptance)
        {
            var checks = acceptance.FailedChecks is { Count: > 0 }
                ? acceptance.FailedChecks
                : acceptance.RequiredUnmetCriteria.Select(criterion => criterion.Name).ToArray();
            if (checks.Count > 0)
            {
                return checks;
            }
        }

        if (run.Exception is not null)
        {
            return [$"background-acceptance-fault: {SanitizeReason(run.Exception.Message)}"];
        }

        if (run.EarlyOutcome is not null)
        {
            return [$"{run.EarlyOutcome.Kind}: {SanitizeReason(run.EarlyOutcome.Detail)}"];
        }

        return [AcceptanceAttemptFailureCheck(attempt)];
    }

    private static string AcceptanceAttemptFailureCheck(ConductorParallelAcceptanceAttempt attempt) =>
        $"background-acceptance-{AcceptanceAttemptOutcomeToken(attempt.Outcome)}: {SanitizeReason(attempt.Detail ?? attempt.AttemptId)}";

    private static void EnsureParallelAcceptanceTerminalIsVerifying(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (goal.Status == GoalStatus.Verified)
        {
            kernel.BeginGoalAcceptanceVerification(
                goal.Id,
                $"Batch loop observed terminal background acceptance gate {attempt.AttemptId}; goal entered Verifying before terminal reconciliation.");
        }
    }

    internal static ConductorAdvanceResult CompleteParallelAcceptanceRun(
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt,
        out bool evidenceMutationLeaseHeld)
    {
        evidenceMutationLeaseHeld = false;
        if (run.Exception is not null)
        {
            if (IsIdentityStaleRun(run))
            {
                return CompleteIdentityStaleRun(driver, policy, run, attempt);
            }

            if (run.Exception is AcceptanceGateEngineException gateEngineFault)
            {
                if (attempt.TransientFailureCount >= ParallelAcceptanceTransientFailureCap)
                {
                    return driver.EscalateParallelLandingAcceptance(
                        run.Candidate,
                        policy,
                        $"background acceptance gate-engine fault: {SanitizeReason(gateEngineFault.Message)}",
                        ConductorEscalationKind.BackgroundAcceptanceFailed);
                }

                return ParallelAcceptanceHeld(
                    run.Candidate,
                    policy,
                    $"Acceptance gate engine fault ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); " +
                    $"retry on next conduct tick. {gateEngineFault.Message}");
            }

            if (run.Exception is AcceptanceInfrastructureDeferredException infrastructureDeferred)
            {
                if (attempt.TransientFailureCount >= ParallelAcceptanceTransientFailureCap)
                {
                    return driver.EscalateParallelLandingAcceptance(
                        run.Candidate,
                        policy,
                        $"background acceptance infrastructure-deferred: {SanitizeReason(infrastructureDeferred.Message)}",
                        ConductorEscalationKind.BackgroundAcceptanceFailed);
                }

                return ParallelAcceptanceHeld(
                    run.Candidate,
                    policy,
                    $"Acceptance infrastructure deferred ({infrastructureDeferred.ReasonCode}) " +
                    $"({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. " +
                    infrastructureDeferred.Message);
            }

            if (run.Exception is DotnetBuildSlotsBusyException slotsBusy)
            {
                return new ConductorAdvanceResult(
                    run.Candidate.Goal.Id.Value,
                    run.Candidate.GoalPrefix,
                    policy.Name,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Stable dotnet build slots busy; retry on next conduct tick. {FormatSlotsBusy(slotsBusy.SlotsBusy)}"));
            }

            if (run.Exception is OperationCanceledException cancelled)
            {
                return ParallelAcceptanceHeld(
                    run.Candidate,
                    policy,
                    $"Background acceptance attempt cancelled; retry on next conduct tick: {SanitizeReason(cancelled.Message)}");
            }

            if (run.Exception is BuildLockBlockedException buildLock)
            {
                if (attempt.TransientFailureCount >= ParallelAcceptanceTransientFailureCap)
                {
                    return driver.EscalateParallelLandingAcceptance(
                        run.Candidate,
                        policy,
                        $"background acceptance blocked-build-lock: {FormatBuildLockBlocked(buildLock.Attribution)}",
                        ConductorEscalationKind.BackgroundAcceptanceFailed);
                }

                return new ConductorAdvanceResult(
                    run.Candidate.Goal.Id.Value,
                    run.Candidate.GoalPrefix,
                    policy.Name,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Build artifact lock blocked acceptance; retry on next conduct tick. {FormatBuildLockBlocked(buildLock.Attribution)}"));
            }

            return ParallelAcceptanceFault(driver, run.Candidate, policy, attempt, run.Exception);
        }

        if (run.EarlyResult is not null)
        {
            return driver.ReplayParallelLandingEarlyOutcome(run.Candidate, policy, run.EarlyResult, run.EarlyOutcome);
        }

        if (run.Acceptance is null)
        {
            return ParallelAcceptanceUnclassifiedFault(
                driver,
                run.Candidate,
                policy,
                new InvalidOperationException("Parallel acceptance produced no result."));
        }

        try
        {
            return driver.CompleteParallelLandingAcceptance(
                run.Candidate,
                policy,
                run.Acceptance,
                out evidenceMutationLeaseHeld);
        }
        catch (Exception ex)
        {
            return ParallelAcceptanceUnclassifiedFault(driver, run.Candidate, policy, ex);
        }
    }

    internal static void MarkParallelAcceptanceReconciledUnlessLeaseHeld(
        ConductorDriver driver,
        ConductorParallelAcceptanceRunResult run,
        bool evidenceMutationLeaseHeld,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (run.Acceptance is { Passed: true } && evidenceMutationLeaseHeld)
        {
            return;
        }

        driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(attempt);
    }

    private static ConductorAdvanceResult ParallelAcceptanceHeld(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string reason,
        ConductorHoldOwner owner = ConductorHoldOwner.None) =>
        ParallelAcceptanceHeld(candidate.Goal, policy, reason, owner);

    private static ConductorAdvanceResult ParallelAcceptanceHeld(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string reason,
        ConductorHoldOwner owner = ConductorHoldOwner.None) =>
        new(
            goal.Id.Value,
            goal.Id.Value[..8],
            policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, reason) { Owner = owner });

    private static string BuildAcceptanceEngineHoldReason(AcceptanceEngineHealthSnapshot snapshot)
    {
        var decision = AcceptanceEngineAcceptanceGate.Decide(
            snapshot.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
        return $"{decision.Reason}" +
               (string.IsNullOrWhiteSpace(snapshot.LandingSha) ? string.Empty : $"; landing={snapshot.LandingSha}") +
               (string.IsNullOrWhiteSpace(snapshot.FailureReason) ? string.Empty : $"; failure={snapshot.FailureReason}") +
               "; acceptance and landing are blocked until the canary passes or an operator runs acceptance-engine clear.";
    }

    internal static bool IsAcceptanceEngineCircuitHoldRequired(
        GoalStatus goalStatus,
        AcceptanceEngineHealthSnapshot? snapshot) =>
        goalStatus == GoalStatus.Verified &&
        snapshot is not null &&
        !AcceptanceEngineAcceptanceGate.Decide(
            snapshot.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy).Allowed;

    internal static ConductorAdvanceResult ParallelAcceptanceTerminal(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Stable dotnet build slots busy in background acceptance attempt; retry on next conduct tick. attempt={attempt.AttemptId}");
        }

        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Build artifact lock blocked background acceptance attempt; retry on next conduct tick. attempt={attempt.AttemptId}");
        }

        if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Cancelled)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Background acceptance attempt cancelled; retry on next conduct tick. attempt={attempt.AttemptId}: {SanitizeReason(attempt.Detail ?? "cancelled")}");
        }

        if (ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(attempt) &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Transient background acceptance {AcceptanceAttemptOutcomeToken(attempt.Outcome)} ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. attempt={attempt.AttemptId}: {SanitizeReason(attempt.Detail ?? "transient artifact fault")}");
        }

        return driver.EscalateParallelLandingAcceptance(
            candidate,
            policy,
            $"background acceptance {AcceptanceAttemptOutcomeToken(attempt.Outcome)}: {SanitizeReason(attempt.Detail ?? attempt.AttemptId)}",
            ConductorEscalationKind.BackgroundAcceptanceFailed);
    }

    private static ConductorAdvanceResult ParallelAcceptanceFault(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceAttempt attempt,
        Exception exception)
    {
        var registrationFault =
            ConductorParallelAcceptanceAttemptCoordinator.WorkerRegistrationFaultMessage(exception);
        var disposition =
            ConductorParallelAcceptanceAttemptCoordinator.ClassifyWorkerRegistrationFault(registrationFault);
        if (disposition == WorkerRegistrationFaultDisposition.BoundedRetry &&
            attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap)
        {
            return ParallelAcceptanceHeld(
                candidate,
                policy,
                $"Transient worker-process registration fault ({attempt.TransientFailureCount}/{ParallelAcceptanceTransientFailureCap}); retry on next conduct tick. attempt={attempt.AttemptId}: {registrationFault}");
        }

        if (disposition is WorkerRegistrationFaultDisposition.BoundedRetry or WorkerRegistrationFaultDisposition.Terminal)
        {
            return driver.EscalateParallelLandingAcceptance(
                candidate,
                policy,
                $"background acceptance worker-process registration fault: {registrationFault}",
                ConductorEscalationKind.BackgroundAcceptanceFailed);
        }

        return ParallelAcceptanceUnclassifiedFault(driver, candidate, policy, exception);
    }

    private static ConductorAdvanceResult ParallelAcceptanceUnclassifiedFault(
        ConductorDriver driver,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Exception exception) =>
        driver.EscalateParallelLandingAcceptance(
            candidate,
            policy,
            $"parallel acceptance fault: {SanitizeReason(exception.Message)}");

    internal static string AcceptanceRunDisposition(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return run.Exception switch
            {
                DotnetBuildSlotsBusyException => "slots-busy",
                OperationCanceledException => "cancelled",
                BuildLockBlockedException => "build-lock-blocked",
                AcceptanceInfrastructureDeferredException deferred when
                    deferred.ReasonCode == "structural-coverage-permit-unavailable" =>
                    "structural-coverage-permit-unavailable",
                AcceptanceInfrastructureDeferredException => "infrastructure-deferred",
                AcceptanceGateEngineException => "gate-engine-fault",
                AcceptanceExecutionIdentityChangedException { IsChangedIdentity: true } => IdentityStaleDisposition,
                _ => "fault"
            };
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated ? "blocked" : "done";
        }

        return run.Acceptance?.Passed == true ? "passed" : "failed";
    }

    internal static string AcceptanceAttemptOutcomeToken(ConductorParallelAcceptanceAttemptOutcome outcome) =>
        outcome switch
        {
            ConductorParallelAcceptanceAttemptOutcome.Running => "running",
            ConductorParallelAcceptanceAttemptOutcome.Passed => "passed",
            ConductorParallelAcceptanceAttemptOutcome.Failed => "failed",
            ConductorParallelAcceptanceAttemptOutcome.StaleCandidate => "stale-candidate",
            ConductorParallelAcceptanceAttemptOutcome.ProcessDied => "process-died",
            ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts => "corrupt-artifacts",
            ConductorParallelAcceptanceAttemptOutcome.Cancelled => "cancelled",
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot => "blocked-build-slot",
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock => "blocked-build-lock",
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred => "infrastructure-deferred",
            ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable => "structural-coverage-permit-unavailable",
            ConductorParallelAcceptanceAttemptOutcome.GateEngineFault => "gate-engine-fault",
            ConductorParallelAcceptanceAttemptOutcome.LaunchFailed => "launch-failed",
            ConductorParallelAcceptanceAttemptOutcome.Faulted => "faulted",
            ConductorParallelAcceptanceAttemptOutcome.Reconciled => "reconciled",
            _ => "unknown"
        };
}
