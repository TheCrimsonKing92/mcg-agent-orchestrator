using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Core;

public enum FailedGoalRecoveryAction
{
    Hold,
    ReconcileExitedDispatch,
    RetryTransient,
    RetryStale,
    CriterionRetry,
    FindingRetry,
    ObserveReviewContract,
    ObserveVerifyingFinding,
    Escalate, RerunTimedOutSelections
}

public enum FailedGoalStaleRecoveryDisposition
{
    None,
    Retry,
    Escalate
}

public enum FailedGoalFindingObservationKind
{
    None,
    ReviewContractLedgerUnavailable,
    ReviewRetryCapReached,
    ReviewTouchProofUnavailable,
    ReviewContractRepairLimitReached,
    ReviewContractRepairEnvelopeAvailable,
    FindingOperatorEvidenceRequired,
    FindingRouteUnavailable,
    FindingRetryCapReached,
    FindingRepeatedFailingTestSet,
    FindingConvergenceViolation,
    FindingEvidencePending,
    FindingEvidenceReceiptPersistenceFailed,
    FindingActionableRedRouteUnavailable,
    FindingActionableRed,
    FindingEvidenceDeliveryRecorded,
    FindingResultMissing,
    FindingRouteObserved
}

public sealed record FailedGoalRecoveryIdentity(
    GoalId GoalId,
    TaskId? TaskId,
    string? AttemptIdentity,
    string ContextVersion);

public sealed record FailedGoalRecoveryTaskFacts(
    TaskId TaskId,
    AgentRole RequiredRole,
    WorkTaskStatus Status,
    bool HasLiveProcess,
    bool IsExitedWithoutAppliedCompletion,
    string? AttemptIdentity,
    DispatchOutcomeKind? OutcomeKind,
    RecoveryRecommendation RecoveryRecommendation,
    TaskOutcomeClass OutcomeClass,
    string? EvidenceSummary,
    FailedGoalStaleRecoveryDisposition StaleRecoveryDisposition,
    string? StaleRecoveryDiagnostic,
    int EmptyOutputRetryCount,
    int CriterionRetryCount,
    RetryCause? AutomaticRetryCause,
    ProviderFailureKind? ProviderFailureKind,
    int? ExitCode,
    string? Command,
    TimeSpan RetryBackoff,
    FailedGoalInconclusiveRoundPair? InconclusiveRounds = null, FailedGoalTimedOutSelectionFacts? TimedOutSelections = null);

public sealed record FailedGoalPendingNote(TaskId TaskId, string Message);

public sealed record FailedGoalFindingObservation(
    FailedGoalFindingObservationKind Kind,
    TaskId? TargetTaskId,
    string? AttemptIdentity,
    string Evidence,
    RetryRoundKind? RoundKind = null,
    RetryCause? ObservedCause = null,
    string? WarningMessage = null,
    string Attribution = "structured-finding-routing",
    ImmutableArray<FailedGoalPendingNote> PendingNotes = default, Conductor.ConductorHoldOwner HoldOwner = default)
{
    public static FailedGoalFindingObservation None { get; } = new(
        FailedGoalFindingObservationKind.None,
        null,
        null,
        string.Empty);

    public static FailedGoalFindingObservation Observed(
        FailedGoalFindingObservationKind kind,
        string evidence) => new(
        kind,
        null,
        null,
        evidence);

    public static FailedGoalFindingObservation Routed(
        FailedGoalFindingObservationKind kind,
        TaskId targetTaskId,
        string attemptIdentity,
        string evidence,
        string? warningMessage,
        RetryRoundKind? roundKind = null,
        RetryCause? observedCause = null) => new(
            kind,
            targetTaskId,
            attemptIdentity,
            evidence,
            roundKind,
            observedCause,
            warningMessage);
}

public sealed record FailedGoalReviewContractCandidate(
    TaskId TaskId,
    AgentRole RequiredRole,
    WorkTaskStatus Status,
    string? ViolationCode,
    bool HasMergedReviewFindings);

public sealed record FailedGoalReviewContractSelectionFacts(
    FailedGoalReviewContractCandidate Candidate,
    string? LedgerUnavailableEvidence,
    string? RetryCapEvidence,
    string? TouchProofUnavailableEvidence,
    int PriorRepairCount,
    int MaxRepairCount,
    string RepairLimitEvidence,
    string RepairEnvelope,
    string AttemptIdentity);

public sealed record FailedGoalFindingRouteTask(
    TaskId TaskId,
    AgentRole RequiredRole);

public sealed class FailedGoalRecoveryFacts : IEquatable<FailedGoalRecoveryFacts>
{
    public FailedGoalRecoveryFacts(
        GoalId goalId,
        GoalLifecycleState state,
        int automaticAcceptanceRetryCount,
        int maxCriterionRetries,
        int maxTransientAttempts,
        string contextVersion,
        IEnumerable<FailedGoalRecoveryTaskFacts> tasks,
        string terminalEscalationReason,
        bool reviewContractObservationCompleted = false,
        FailedGoalFindingObservation? reviewContractObservation = null,
        bool verifyingFindingObservationCompleted = false,
        FailedGoalFindingObservation? verifyingFindingObservation = null,
        int transientAttemptsPerCycle = 1,
        int transientRecoveryCycles = 1)
    {
        GoalId = goalId;
        State = state;
        AutomaticAcceptanceRetryCount = automaticAcceptanceRetryCount;
        MaxCriterionRetries = maxCriterionRetries;
        MaxTransientAttempts = maxTransientAttempts;
        ContextVersion = contextVersion;
        Tasks = tasks.ToImmutableArray();
        TerminalEscalationReason = terminalEscalationReason;
        ReviewContractObservationCompleted = reviewContractObservationCompleted;
        ReviewContractObservation = reviewContractObservation;
        VerifyingFindingObservationCompleted = verifyingFindingObservationCompleted;
        VerifyingFindingObservation = verifyingFindingObservation;
        TransientAttemptsPerCycle = Math.Max(1, transientAttemptsPerCycle);
        TransientRecoveryCycles = Math.Max(1, transientRecoveryCycles);
    }

    public GoalId GoalId { get; }
    public GoalLifecycleState State { get; }
    public int AutomaticAcceptanceRetryCount { get; }
    public int MaxCriterionRetries { get; }
    public int MaxTransientAttempts { get; }
    public string ContextVersion { get; }
    public ImmutableArray<FailedGoalRecoveryTaskFacts> Tasks { get; }
    public string TerminalEscalationReason { get; }
    public bool ReviewContractObservationCompleted { get; }
    public FailedGoalFindingObservation? ReviewContractObservation { get; }
    public bool VerifyingFindingObservationCompleted { get; }
    public FailedGoalFindingObservation? VerifyingFindingObservation { get; }
    public int TransientAttemptsPerCycle { get; }
    public int TransientRecoveryCycles { get; }

    public FailedGoalRecoveryFacts WithReviewContractObservation(FailedGoalFindingObservation? observation) =>
        new(
            GoalId,
            State,
            AutomaticAcceptanceRetryCount,
            MaxCriterionRetries,
            MaxTransientAttempts,
            ContextVersion,
            Tasks,
            TerminalEscalationReason,
            reviewContractObservationCompleted: true,
            observation,
            VerifyingFindingObservationCompleted,
            VerifyingFindingObservation,
            TransientAttemptsPerCycle,
            TransientRecoveryCycles);

    public FailedGoalRecoveryFacts WithVerifyingFindingObservation(FailedGoalFindingObservation? observation) =>
        new(
            GoalId,
            State,
            AutomaticAcceptanceRetryCount,
            MaxCriterionRetries,
            MaxTransientAttempts,
            ContextVersion,
            Tasks,
            TerminalEscalationReason,
            ReviewContractObservationCompleted,
            ReviewContractObservation,
            verifyingFindingObservationCompleted: true,
            observation,
            TransientAttemptsPerCycle,
            TransientRecoveryCycles);

    public bool Equals(FailedGoalRecoveryFacts? other) =>
        other is not null &&
        GoalId == other.GoalId &&
        State == other.State &&
        AutomaticAcceptanceRetryCount == other.AutomaticAcceptanceRetryCount &&
        MaxCriterionRetries == other.MaxCriterionRetries &&
        MaxTransientAttempts == other.MaxTransientAttempts &&
        string.Equals(ContextVersion, other.ContextVersion, StringComparison.Ordinal) &&
        string.Equals(TerminalEscalationReason, other.TerminalEscalationReason, StringComparison.Ordinal) &&
        ReviewContractObservationCompleted == other.ReviewContractObservationCompleted &&
        VerifyingFindingObservationCompleted == other.VerifyingFindingObservationCompleted &&
        TransientAttemptsPerCycle == other.TransientAttemptsPerCycle &&
        TransientRecoveryCycles == other.TransientRecoveryCycles &&
        Equals(ReviewContractObservation, other.ReviewContractObservation) &&
        Equals(VerifyingFindingObservation, other.VerifyingFindingObservation) &&
        Tasks.SequenceEqual(other.Tasks);

    public override bool Equals(object? obj) => Equals(obj as FailedGoalRecoveryFacts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GoalId);
        hash.Add(State);
        hash.Add(AutomaticAcceptanceRetryCount);
        hash.Add(MaxCriterionRetries);
        hash.Add(MaxTransientAttempts);
        hash.Add(ContextVersion, StringComparer.Ordinal);
        hash.Add(TerminalEscalationReason, StringComparer.Ordinal);
        hash.Add(ReviewContractObservationCompleted);
        hash.Add(VerifyingFindingObservationCompleted);
        hash.Add(TransientAttemptsPerCycle);
        hash.Add(TransientRecoveryCycles);
        hash.Add(ReviewContractObservation);
        hash.Add(VerifyingFindingObservation);
        foreach (var task in Tasks)
            hash.Add(task);
        return hash.ToHashCode();
    }
}

public sealed record FailedGoalRecoveryDecision(
    FailedGoalRecoveryAction Action,
    FailedGoalRecoveryIdentity Identity,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    RetryCause? RetryCause = null,
    RetryRoundKind? RoundKind = null,
    TimeSpan Backoff = default,
    string? FeedbackCommand = null,
    string? FeedbackEvidence = null,
    string? WarningMessage = null, Conductor.ConductorHoldOwner HoldOwner = default, FailedGoalTimedOutSelectionRerun? TimedOutRerun = null);

/// <summary>
/// Selects one Failed-lifecycle recovery action from already-observed immutable facts.
/// It deliberately owns no process, file, provider, state, budget, clock, or output effect.
/// </summary>
public static class FailedGoalRecoveryPolicy
{
    public static FailedGoalReviewContractCandidate? SelectReviewContractCandidate(
        IEnumerable<FailedGoalReviewContractCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var candidate = candidates.FirstOrDefault(item =>
            item.RequiredRole is AgentRole.Reviewer or AgentRole.Tester &&
            item.Status == WorkTaskStatus.Failed &&
            !string.IsNullOrWhiteSpace(item.ViolationCode));
        if (candidate is null)
            return null;

        return candidate.HasMergedReviewFindings &&
            candidate.ViolationCode is ReviewFindingConvergence.IdentityMovedViolationCode or
                ReviewFindingConvergence.RecycledAnchorIdentityViolationCode
            ? null
            : candidate;
    }

    public static FailedGoalFindingObservation SelectReviewContractObservation(
        FailedGoalReviewContractSelectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!string.IsNullOrWhiteSpace(facts.LedgerUnavailableEvidence))
        {
            return FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.ReviewContractLedgerUnavailable,
                facts.LedgerUnavailableEvidence);
        }

        if (!string.IsNullOrWhiteSpace(facts.RetryCapEvidence))
        {
            return FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.ReviewRetryCapReached,
                facts.RetryCapEvidence);
        }

        if (!string.IsNullOrWhiteSpace(facts.TouchProofUnavailableEvidence))
        {
            return FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.ReviewTouchProofUnavailable,
                facts.TouchProofUnavailableEvidence);
        }

        if (facts.PriorRepairCount >= facts.MaxRepairCount)
        {
            return FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.ReviewContractRepairLimitReached,
                facts.RepairLimitEvidence);
        }

        return FailedGoalFindingObservation.Routed(
            FailedGoalFindingObservationKind.ReviewContractRepairEnvelopeAvailable,
            facts.Candidate.TaskId,
            facts.AttemptIdentity,
            facts.RepairEnvelope,
            warningMessage: null);
    }

    public static FailedGoalVerifyingFindingRouteSelection SelectVerifyingFindingRoute(
        FailedGoalVerifyingFindingRouteFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var priorTasks = facts.PriorTasks;
        var targetRole = facts.ReviewerTargetRole ?? AgentRole.Developer;

        if (facts.ReviewerEscalatesToOperator)
        {
            return Selection(FailedGoalVerifyingFindingRouteKind.OperatorEvidenceRequired, null, targetRole);
        }

        var targetTaskId = facts.TriggeringRole == AgentRole.Tester && targetRole == AgentRole.Tester
            ? facts.TriggeringTaskId : facts.ExplicitTargetTaskId;
        if (targetTaskId is null && !facts.RequiresCommittedTarget)
            targetTaskId = priorTasks.LastOrDefault(task => task.RequiredRole == targetRole)?.TaskId;
        if (targetTaskId is null && facts.TriggeringRole == AgentRole.Reviewer && targetRole != AgentRole.Developer)
        {
            targetRole = AgentRole.Developer;
            targetTaskId = priorTasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer)?.TaskId;
        }

        if (targetTaskId is null)
            return Selection(FailedGoalVerifyingFindingRouteKind.TargetUnavailable, null, targetRole);
        if (facts.LifetimeBackstop > 0 && facts.LifetimeRound >= facts.LifetimeBackstop)
            return Selection(FailedGoalVerifyingFindingRouteKind.LifetimeBackstopReached, targetTaskId, targetRole);
        if (facts.Round >= facts.StopRound)
            return Selection(FailedGoalVerifyingFindingRouteKind.RetryCapReached, targetTaskId, targetRole);
        if (facts.MissingFindingResult)
        {
            return new FailedGoalVerifyingFindingRouteSelection(
                FailedGoalVerifyingFindingRouteKind.MissingFindingResult,
                facts.TriggeringTaskId,
                facts.TriggeringRole,
                facts.TriggerAttemptIdentity,
                facts.Round,
                RetryCause.EnvironmentApparatusFailure,
                RetryRoundKind.Mechanical,
                EmitWarning: false);
        }

        if (targetRole == AgentRole.Developer && facts.RepeatedFailure?.HoldRequired == true)
            return Selection(FailedGoalVerifyingFindingRouteKind.RepeatedFailingTestSet, targetTaskId, targetRole);

        return new FailedGoalVerifyingFindingRouteSelection(
            FailedGoalVerifyingFindingRouteKind.Routed,
            targetTaskId,
            targetRole,
            string.Empty,
            facts.Round,
            facts.ObservedCause ?? RetryCause.CriterionEvidenceOwnerMismatch,
            null,
            facts.Round >= facts.WarningRound);

        FailedGoalVerifyingFindingRouteSelection Selection(
            FailedGoalVerifyingFindingRouteKind kind,
            TaskId? taskId,
            AgentRole role) => new(
                kind,
                taskId,
                role,
                string.Empty,
                facts.Round,
                null,
                null,
                EmitWarning: false);
    }

    public static FailedGoalRecoveryDecision Evaluate(FailedGoalRecoveryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.State != GoalLifecycleState.Failed)
            throw new InvalidOperationException($"Failed-goal recovery cannot evaluate lifecycle state {facts.State}.");

        var live = facts.Tasks.FirstOrDefault(task => task.HasLiveProcess);
        if (live is not null)
        {
            return Decide(
                facts,
                live,
                FailedGoalRecoveryAction.Hold,
                1,
                "live-sibling-or-owned-attempt",
                $"Failure handling deferred while task {Short(live.TaskId)} still has a live worker process.");
        }

        var exited = facts.Tasks.FirstOrDefault(task => task.IsExitedWithoutAppliedCompletion);
        if (exited is not null)
        {
            return Decide(
                facts,
                exited,
                FailedGoalRecoveryAction.ReconcileExitedDispatch,
                2,
                "exited-without-applied-completion",
                $"Task {Short(exited.TaskId)} has an exited process result that has not been applied.");
        }

        var inconclusiveTester = facts.Tasks.FirstOrDefault(task =>
            task.RequiredRole == AgentRole.Tester &&
            task.Status == WorkTaskStatus.Failed &&
            task.OutcomeKind == DispatchOutcomeKind.VerificationInconclusive);
        if (inconclusiveTester is not null)
        {
            if (inconclusiveTester.EmptyOutputRetryCount > facts.MaxTransientAttempts)
            {
                return Decide(
                    facts,
                    inconclusiveTester,
                    FailedGoalRecoveryAction.Escalate,
                    3,
                    "verification-inconclusive-budget-exhausted",
                    $"Tester task {Short(inconclusiveTester.TaskId)} exhausted verification-inconclusive recovery " +
                    $"({inconclusiveTester.EmptyOutputRetryCount}/{facts.MaxTransientAttempts}); operator action required. " +
                    $"Latest current-round receipt: {inconclusiveTester.EvidenceSummary}");
            }

            if (inconclusiveTester.InconclusiveRounds is { InputsUnchanged: true } rounds)
                return FailedGoalTimedOutSelectionRerunRule.TryDecide(facts, inconclusiveTester, rounds) ?? Decide(facts, inconclusiveTester, FailedGoalRecoveryAction.Escalate, 3,
                    "verification-inconclusive-unchanged-inputs",
                    $"Tester task {Short(inconclusiveTester.TaskId)} stayed verification-inconclusive on unchanged inputs; operator action required. {rounds.DescribeUnchanged(inconclusiveTester.EvidenceSummary)}");

            return Decide(
                facts,
                inconclusiveTester,
                FailedGoalRecoveryAction.RetryTransient,
                3,
                "verification-inconclusive",
                $"Auto-retry verification-inconclusive Tester task {Short(inconclusiveTester.TaskId)} " +
                $"on the shared transient budget ({inconclusiveTester.EmptyOutputRetryCount}/{facts.MaxTransientAttempts}) " +
                $"without reopening upstream Developer work. Latest current-round receipt: {inconclusiveTester.EvidenceSummary}",
                RetryCause.EnvironmentApparatusFailure,
                backoff: inconclusiveTester.RetryBackoff);
        }

        var stale = facts.Tasks.FirstOrDefault(task =>
            task.Status == WorkTaskStatus.Failed &&
            task.StaleRecoveryDisposition != FailedGoalStaleRecoveryDisposition.None);
        if (stale is not null)
        {
            if (stale.StaleRecoveryDisposition == FailedGoalStaleRecoveryDisposition.Retry)
            {
                return Decide(
                    facts,
                    stale,
                    FailedGoalRecoveryAction.RetryStale,
                    4,
                    "stale-dispatch-retry",
                    $"Auto-retry stale dispatch recovery for task {Short(stale.TaskId)}; {stale.StaleRecoveryDiagnostic}",
                    RetryCause.EnvironmentApparatusFailure);
            }

            return Decide(
                facts,
                stale,
                FailedGoalRecoveryAction.Escalate,
                4,
                "stale-dispatch-operator-hold",
                $"Task {Short(stale.TaskId)} blocked by stale dispatch recovery; {stale.StaleRecoveryDiagnostic}");
        }

        var preflight = facts.Tasks.FirstOrDefault(task =>
            task.Status == WorkTaskStatus.Failed && task.OutcomeKind == DispatchOutcomeKind.PreflightFailure);
        if (preflight is not null)
        {
            if (preflight.EmptyOutputRetryCount > facts.MaxTransientAttempts)
            {
                return Decide(
                    facts,
                    preflight,
                    FailedGoalRecoveryAction.Escalate,
                    5,
                    "preflight-budget-exhausted",
                    $"Task {Short(preflight.TaskId)} exhausted sandbox-preflight dispatch recovery " +
                    $"({preflight.EmptyOutputRetryCount}/{facts.MaxTransientAttempts}); operator action required: {preflight.EvidenceSummary}");
            }

            return Decide(
                facts,
                preflight,
                FailedGoalRecoveryAction.RetryTransient,
                5,
                "preflight-failure",
                $"Auto-retry sandbox-preflight dispatch flake {preflight.EmptyOutputRetryCount}/{facts.MaxTransientAttempts} " +
                $"for task {Short(preflight.TaskId)}; worker never launched (preflight failure): {preflight.EvidenceSummary}",
                RetryCause.EnvironmentApparatusFailure,
                backoff: preflight.RetryBackoff);
        }

        var realFailure = facts.Tasks.FirstOrDefault(task =>
            task.Status == WorkTaskStatus.Failed &&
            task.RecoveryRecommendation == RecoveryRecommendation.AutoRetry &&
            task.OutcomeClass == TaskOutcomeClass.RealFailure);
        if (realFailure is not null)
        {
            if (facts.AutomaticAcceptanceRetryCount >= facts.MaxCriterionRetries)
            {
                return Decide(
                    facts,
                    realFailure,
                    FailedGoalRecoveryAction.Escalate,
                    6,
                    "real-failure-budget-exhausted",
                    $"Task {Short(realFailure.TaskId)} exhausted bounded real-failure retries " +
                    $"({facts.AutomaticAcceptanceRetryCount}/{facts.MaxCriterionRetries}); failed command: {realFailure.Command}; " +
                    $"failure evidence: {realFailure.EvidenceSummary}");
            }

            if (realFailure.AutomaticRetryCause is null)
            {
                return Decide(
                    facts,
                    realFailure,
                    FailedGoalRecoveryAction.Escalate,
                    6,
                    "real-failure-missing-typed-cause-no-budget-spend",
                    $"Task {Short(realFailure.TaskId)} has a real failure without a typed retry cause; " +
                    "automatic redispatch was held for operator classification.");
            }

            var command = $"Failed command: {realFailure.Command}";
            var evidence = $"Failure evidence: {realFailure.EvidenceSummary}";
            return Decide(
                facts,
                realFailure,
                FailedGoalRecoveryAction.CriterionRetry,
                6,
                "real-worker-or-command-failure",
                $"Auto-retry real worker/command failure for task {Short(realFailure.TaskId)} " +
                $"(attempt {facts.AutomaticAcceptanceRetryCount + 1}/{facts.MaxCriterionRetries}); {command}; {evidence}",
                realFailure.AutomaticRetryCause,
                feedbackCommand: command,
                feedbackEvidence: evidence);
        }

        var flake = facts.Tasks.FirstOrDefault(task =>
            task.Status == WorkTaskStatus.Failed &&
            task.OutcomeKind is DispatchOutcomeKind.LaunchFailure or DispatchOutcomeKind.EmptyOutputFlake &&
            task.EmptyOutputRetryCount > 0);
        if (flake is not null)
        {
            if (flake.EmptyOutputRetryCount > facts.MaxTransientAttempts)
            {
                return Decide(
                    facts,
                    flake,
                    FailedGoalRecoveryAction.Escalate,
                    7,
                    "empty-output-budget-exhausted",
                    $"Task {Short(flake.TaskId)} exhausted empty-output dispatch recovery " +
                    $"({flake.EmptyOutputRetryCount}/{facts.MaxTransientAttempts}); operator action required");
            }

            var attemptInCycle = ((flake.EmptyOutputRetryCount - 1) % facts.TransientAttemptsPerCycle) + 1;
            var cycle = ((flake.EmptyOutputRetryCount - 1) / facts.TransientAttemptsPerCycle) + 1;
            var label = flake.ProviderFailureKind == ProviderFailureKind.Sandbox1312
                ? "sandbox command-launch failure"
                : flake.OutcomeKind == DispatchOutcomeKind.LaunchFailure
                    ? "silent launch failure"
                    : "empty-output dispatch flake";
            var evidence = flake.ProviderFailureKind == ProviderFailureKind.Sandbox1312
                ? $"sandbox logon session failed with root exit {flake.ExitCode}"
                : flake.OutcomeKind == DispatchOutcomeKind.LaunchFailure
                    ? $"task produced zero bytes on both streams with root exit {flake.ExitCode}"
                    : $"task produced zero-byte stdout with exit {flake.ExitCode}";
            var note = attemptInCycle == facts.TransientAttemptsPerCycle
                ? $"Auto-recover+re-admit {label} cycle {cycle}/{facts.TransientRecoveryCycles}; {evidence}"
                : $"Auto-retry {label} {attemptInCycle}/{facts.TransientAttemptsPerCycle} " +
                    $"in recovery cycle {cycle}/{facts.TransientRecoveryCycles}; {evidence}";
            return Decide(
                facts,
                flake,
                FailedGoalRecoveryAction.RetryTransient,
                7,
                "launch-or-empty-output-flake",
                note,
                RetryCause.EnvironmentApparatusFailure,
                backoff: flake.RetryBackoff);
        }

        if (!facts.ReviewContractObservationCompleted)
        {
            return new FailedGoalRecoveryDecision(
                FailedGoalRecoveryAction.ObserveReviewContract,
                new FailedGoalRecoveryIdentity(facts.GoalId, null, null, facts.ContextVersion),
                8,
                "review-contract-observation-required",
                "Observe current review-contract evidence before selecting finding recovery.");
        }

        if (facts.ReviewContractObservation is { Kind: not FailedGoalFindingObservationKind.None } contract)
            return DecideFindingObservation(facts, contract, "review-contract-routing");

        if (!facts.VerifyingFindingObservationCompleted)
        {
            return new FailedGoalRecoveryDecision(
                FailedGoalRecoveryAction.ObserveVerifyingFinding,
                new FailedGoalRecoveryIdentity(facts.GoalId, null, null, facts.ContextVersion),
                8,
                "verifying-finding-observation-required",
                "Observe current structured finding and operator-disposition evidence before selecting recovery.");
        }

        if (facts.VerifyingFindingObservation is { Kind: not FailedGoalFindingObservationKind.None } finding)
            return DecideFindingObservation(facts, finding, "structured-finding-routing");

        return new FailedGoalRecoveryDecision(
            FailedGoalRecoveryAction.Escalate,
            new FailedGoalRecoveryIdentity(facts.GoalId, null, null, facts.ContextVersion),
            9,
            "failed-terminal-fallthrough",
            facts.TerminalEscalationReason);
    }

    private static FailedGoalRecoveryDecision Decide(
        FailedGoalRecoveryFacts facts,
        FailedGoalRecoveryTaskFacts task,
        FailedGoalRecoveryAction action,
        int rung,
        string evidence,
        string reason,
        RetryCause? cause = null,
        RetryRoundKind? roundKind = null,
        TimeSpan backoff = default,
        string? feedbackCommand = null,
        string? feedbackEvidence = null) =>
        new(
            action,
            new FailedGoalRecoveryIdentity(facts.GoalId, task.TaskId, task.AttemptIdentity, facts.ContextVersion),
            rung,
            evidence,
            reason,
            cause,
            roundKind,
            backoff,
            feedbackCommand,
            feedbackEvidence);

    private static FailedGoalRecoveryDecision DecideFindingObservation(
        FailedGoalRecoveryFacts facts,
        FailedGoalFindingObservation observation,
        string defaultAttribution)
    {
        var (action, cause, roundKind) = observation.Kind switch
        {
            FailedGoalFindingObservationKind.FindingEvidencePending =>
                (FailedGoalRecoveryAction.Hold, (RetryCause?)null, (RetryRoundKind?)null),
            FailedGoalFindingObservationKind.ReviewContractRepairEnvelopeAvailable =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.CriterionEvidenceOwnerMismatch, RetryRoundKind.Mechanical),
            FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.CriterionEvidenceOwnerMismatch, RetryRoundKind.Mechanical),
            FailedGoalFindingObservationKind.FindingResultMissing =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.EnvironmentApparatusFailure, RetryRoundKind.Mechanical),
            FailedGoalFindingObservationKind.FindingActionableRed =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.NewSourceFinding, (RetryRoundKind?)null),
            FailedGoalFindingObservationKind.FindingRouteObserved =>
                (FailedGoalRecoveryAction.FindingRetry, observation.ObservedCause, observation.RoundKind),
            FailedGoalFindingObservationKind.ReviewContractLedgerUnavailable or
            FailedGoalFindingObservationKind.ReviewRetryCapReached or
            FailedGoalFindingObservationKind.ReviewTouchProofUnavailable or
            FailedGoalFindingObservationKind.ReviewContractRepairLimitReached or
            FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired or
            FailedGoalFindingObservationKind.FindingRouteUnavailable or
            FailedGoalFindingObservationKind.FindingRetryCapReached or
            FailedGoalFindingObservationKind.FindingRepeatedFailingTestSet or
            FailedGoalFindingObservationKind.FindingConvergenceViolation or
            FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed or
            FailedGoalFindingObservationKind.FindingActionableRedRouteUnavailable =>
                (FailedGoalRecoveryAction.Escalate, (RetryCause?)null, (RetryRoundKind?)null),
            FailedGoalFindingObservationKind.None => throw new InvalidOperationException(
                "An empty finding observation cannot select a recovery action."),
            _ => throw new InvalidOperationException($"Unknown finding observation {observation.Kind}.")
        };
        if (action == FailedGoalRecoveryAction.FindingRetry &&
            (observation.TargetTaskId is null ||
             string.IsNullOrWhiteSpace(observation.AttemptIdentity) ||
             cause is null))
        {
            throw new InvalidOperationException(
                "A finding retry observation must carry target attempt identity and a typed retry cause.");
        }

        return new FailedGoalRecoveryDecision(
            action,
            new FailedGoalRecoveryIdentity(
                facts.GoalId,
                observation.TargetTaskId,
                observation.AttemptIdentity,
                facts.ContextVersion),
            8,
            string.IsNullOrWhiteSpace(observation.Attribution) ? defaultAttribution : observation.Attribution,
            observation.Evidence,
            cause,
            roundKind,
            WarningMessage: observation.WarningMessage, HoldOwner: observation.HoldOwner);
    }

    private static string Short(TaskId taskId) => taskId.Value[..Math.Min(8, taskId.Value.Length)];
}
