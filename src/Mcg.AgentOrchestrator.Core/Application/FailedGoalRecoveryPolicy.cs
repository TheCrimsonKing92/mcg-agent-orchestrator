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
    ObserveFindingDecision,
    Escalate
}

public enum FailedGoalStaleRecoveryDisposition
{
    None,
    Retry,
    Escalate
}

public enum FailedGoalFindingAction
{
    Hold,
    Retry,
    Escalate
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
    TimeSpan RetryBackoff);

public sealed record FailedGoalFindingCandidate(
    FailedGoalFindingAction Action,
    TaskId? TargetTaskId,
    string? AttemptIdentity,
    string Reason,
    RetryRoundKind? RoundKind = null,
    RetryCause? Cause = null,
    string? WarningMessage = null,
    string Attribution = "structured-finding-routing");

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
        bool findingObservationCompleted = false,
        FailedGoalFindingCandidate? findingCandidate = null,
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
        FindingObservationCompleted = findingObservationCompleted;
        FindingCandidate = findingCandidate;
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
    public bool FindingObservationCompleted { get; }
    public FailedGoalFindingCandidate? FindingCandidate { get; }
    public int TransientAttemptsPerCycle { get; }
    public int TransientRecoveryCycles { get; }

    public FailedGoalRecoveryFacts WithFindingCandidate(FailedGoalFindingCandidate? candidate) =>
        new(
            GoalId,
            State,
            AutomaticAcceptanceRetryCount,
            MaxCriterionRetries,
            MaxTransientAttempts,
            ContextVersion,
            Tasks,
            TerminalEscalationReason,
            findingObservationCompleted: true,
            candidate,
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
        FindingObservationCompleted == other.FindingObservationCompleted &&
        TransientAttemptsPerCycle == other.TransientAttemptsPerCycle &&
        TransientRecoveryCycles == other.TransientRecoveryCycles &&
        Equals(FindingCandidate, other.FindingCandidate) &&
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
        hash.Add(FindingObservationCompleted);
        hash.Add(TransientAttemptsPerCycle);
        hash.Add(TransientRecoveryCycles);
        hash.Add(FindingCandidate);
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
    string? WarningMessage = null);

/// <summary>
/// Selects one Failed-lifecycle recovery action from already-observed immutable facts.
/// It deliberately owns no process, file, provider, state, budget, clock, or output effect.
/// </summary>
public static class FailedGoalRecoveryPolicy
{
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
                    "real-failure-missing-typed-cause",
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
                $"(attempt {realFailure.CriterionRetryCount + 1}/{facts.MaxCriterionRetries}); {command}; {evidence}",
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

        if (!facts.FindingObservationCompleted)
        {
            return new FailedGoalRecoveryDecision(
                FailedGoalRecoveryAction.ObserveFindingDecision,
                new FailedGoalRecoveryIdentity(facts.GoalId, null, null, facts.ContextVersion),
                8,
                "structured-finding-observation-required",
                "Observe current structured finding and operator-disposition evidence before selecting recovery.");
        }

        if (facts.FindingCandidate is { } finding)
        {
            var action = finding.Action switch
            {
                FailedGoalFindingAction.Hold => FailedGoalRecoveryAction.Hold,
                FailedGoalFindingAction.Retry => FailedGoalRecoveryAction.FindingRetry,
                FailedGoalFindingAction.Escalate => FailedGoalRecoveryAction.Escalate,
                _ => throw new InvalidOperationException($"Unknown finding action {finding.Action}.")
            };
            if (action == FailedGoalRecoveryAction.FindingRetry && finding.Cause is null)
                throw new InvalidOperationException("A finding retry candidate must carry a typed retry cause.");
            return new FailedGoalRecoveryDecision(
                action,
                new FailedGoalRecoveryIdentity(facts.GoalId, finding.TargetTaskId, finding.AttemptIdentity, facts.ContextVersion),
                8,
                finding.Attribution,
                finding.Reason,
                finding.Cause,
                finding.RoundKind,
                WarningMessage: finding.WarningMessage);
        }

        var missing = facts.Tasks.FirstOrDefault(task =>
            task.Status == WorkTaskStatus.Failed && task.OutcomeKind is null);
        if (missing is not null)
        {
            return Decide(
                facts,
                missing,
                FailedGoalRecoveryAction.Hold,
                8,
                "missing-current-failure-evidence",
                $"Failure handling for task {Short(missing.TaskId)} is held because current attempt evidence is missing; re-observation is required.");
        }

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

    private static string Short(TaskId taskId) => taskId.Value[..Math.Min(8, taskId.Value.Length)];
}
