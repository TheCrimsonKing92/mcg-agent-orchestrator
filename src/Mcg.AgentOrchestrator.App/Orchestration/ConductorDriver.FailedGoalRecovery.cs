using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorAdvanceResult ExecuteFailedGoalRecovery(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state)
    {
        var facts = BuildFailedGoalRecoveryFacts(goal, policy, state);
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        IReadOnlyList<FailedGoalPendingNote> pendingNotes = [];
        FailedGoalFindingObservation? reviewContractObservation = null;
        FailedGoalFindingObservation? verifyingFindingObservation = null;
        var reviewContractObservationCompleted = false;
        var verifyingFindingObservationCompleted = false;

        while (decision.Action is FailedGoalRecoveryAction.ObserveReviewContract or
               FailedGoalRecoveryAction.ObserveVerifyingFinding)
        {
            FailedGoalFindingObservation observation;
            bool hasObservation;
            if (decision.Action == FailedGoalRecoveryAction.ObserveReviewContract)
            {
                if (reviewContractObservationCompleted)
                    throw new InvalidOperationException("Review-contract observation repeated after it was fulfilled.");
                hasObservation = TryObserveReviewContractRecovery(goal, out observation);
                reviewContractObservationCompleted = true;
                reviewContractObservation = hasObservation ? observation : null;
            }
            else
            {
                if (verifyingFindingObservationCompleted)
                    throw new InvalidOperationException("Verifying-finding observation repeated after it was fulfilled.");
                var landingFileScopes = _getLandingFileScopes(goal);
                hasObservation = TryObserveVerifyingFindingRecovery(
                    goal,
                    policy,
                    landingFileScopes,
                    out observation);
                verifyingFindingObservationCompleted = true;
                verifyingFindingObservation = hasObservation ? observation : null;
            }

            if (hasObservation && !observation.PendingNotes.IsDefaultOrEmpty)
                pendingNotes = pendingNotes.Concat(observation.PendingNotes).ToArray();

            goal = GetCurrentGoal(goal);
            state = GoalLifecycle.ResolveState(goal, GetFacts(goal));
            if (state != GoalLifecycleState.Failed)
            {
                ApplyPendingFailedGoalNotes(goal, pendingNotes);
                return MakeResult(
                    goal.Id.Value,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        state,
                        "Failed-goal observation changed lifecycle authority; recovery will be re-observed on the next tick (stale-recovery-facts)."));
            }

            facts = BuildFailedGoalRecoveryFacts(goal, policy, state);
            if (reviewContractObservationCompleted)
                facts = facts.WithReviewContractObservation(reviewContractObservation);
            if (verifyingFindingObservationCompleted)
                facts = facts.WithVerifyingFindingObservation(verifyingFindingObservation);
            decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        }
        if (decision.Action is (FailedGoalRecoveryAction.CriterionRetry or FailedGoalRecoveryAction.Escalate) &&
            TryRecoverFailedWorkerBuildCheck(goal, goalPrefix, policy, state, decision, pendingNotes) is { } buildRecovery)
            return buildRecovery;
        if (decision.Action is FailedGoalRecoveryAction.ReconcileExitedDispatch or
            FailedGoalRecoveryAction.RetryTransient or
            FailedGoalRecoveryAction.RetryStale or
            FailedGoalRecoveryAction.CriterionRetry or
            FailedGoalRecoveryAction.FindingRetry)
        {
            _beforeFailedGoalRecoveryEffect?.Invoke(goal, decision);
        }
        if (decision.Action == FailedGoalRecoveryAction.ReconcileExitedDispatch)
            return ReconcileFailedGoalExitedDispatches(goal, goalPrefix, policy, state, facts, decision);

        if (decision.Action == FailedGoalRecoveryAction.Hold)
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(state, decision.Reason));
        }

        if (decision.Action == FailedGoalRecoveryAction.Escalate)
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            return Escalate(goal, goalPrefix, policy, state, decision.Reason);
        }
        if (decision.Backoff > TimeSpan.Zero)
            _emptyOutputBackoffDelay(decision.Backoff);

        if (!IsFailedGoalRecoveryContextCurrent(goal, policy, state, decision, out var staleReason))
        {
            ApplyPendingFailedGoalNotes(goal, pendingNotes);
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(state, staleReason));
        }

        ApplyPendingFailedGoalNotes(goal, pendingNotes);
        var targetTaskId = decision.Identity.TaskId ??
            throw new InvalidOperationException($"Recovery action {decision.Action} must identify a target task.");
        if (!string.IsNullOrWhiteSpace(decision.WarningMessage))
            _recordTaskNote(goal.Id, targetTaskId, decision.WarningMessage);
        if (decision.Action == FailedGoalRecoveryAction.CriterionRetry)
        {
            _recordCriterionRetryFeedback(
                goal.Id,
                targetTaskId,
                BuildCriterionRetryFeedback(goal, targetTaskId, decision));
        }

        try
        {
            _retryTask(
                goal.Id,
                targetTaskId,
                decision.Reason,
                decision.RoundKind,
                decision.RetryCause ?? throw new InvalidOperationException(
                    $"Recovery action {decision.Action} must carry a typed retry cause."));
        }
        catch (InvalidOperationException ex) when (
            decision.Reason.StartsWith("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal) &&
            ex.Message.Contains("running process", StringComparison.OrdinalIgnoreCase))
        {
            return Escalate(
                goal,
                goalPrefix,
                policy,
                state,
                $"ACTIONABLE_CANDIDATE_RED_LIFECYCLE_CONFLICT: {decision.Reason} " +
                $"Developer retry was not applied because {ex.Message} No additional downstream dispatch was started.");
        }

        var refreshedGoal = GetCurrentGoal(goal);
        var refreshedTarget = refreshedGoal.Tasks.FirstOrDefault(task => task.Id == targetTaskId);
        if (refreshedTarget is null || refreshedTarget.Status is not (WorkTaskStatus.Assigned or WorkTaskStatus.Pending) ||
            refreshedGoal.Tasks.Any(task => task.LastProcess is { IsRunning: true }))
        {
            return MakeResult(
                refreshedGoal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycle.ResolveState(refreshedGoal, GetFacts(refreshedGoal)),
                    $"{decision.Reason} Dispatch start was re-observed and held because the target attempt changed after retry application (stale-recovery-facts)."));
        }

        return ExecuteDispatchAndStart(refreshedGoal, goalPrefix, policy, GoalLifecycleState.WorkspaceReady);
    }

    private ConductorAdvanceResult ReconcileFailedGoalExitedDispatches(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state,
        FailedGoalRecoveryFacts facts,
        FailedGoalRecoveryDecision decision)
    {
        var exitedTaskIds = facts.Tasks
            .Where(task => task.IsExitedWithoutAppliedCompletion)
            .Select(task => task.TaskId)
            .ToArray();
        foreach (var taskId in exitedTaskIds)
        {
            if (!IsFailedGoalRecoveryContextCurrent(goal, policy, state, decision, out var staleReason))
            {
                return MakeResult(
                    goal.Id.Value,
                    goalPrefix,
                    policy,
                    new ConductorAdvanceOutcome.Held(state, staleReason));
            }
            _reconcileExitedDispatch(goal, taskId);
            goal = GetCurrentGoal(goal);
            if (taskId != exitedTaskIds[^1])
            {
                var currentState = GoalLifecycle.ResolveState(goal, GetFacts(goal));
                if (currentState != GoalLifecycleState.Failed)
                {
                    return MakeResult(
                        goal.Id.Value,
                        goalPrefix,
                        policy,
                        new ConductorAdvanceOutcome.Held(
                            currentState,
                            "Exited-dispatch reconciliation changed lifecycle authority; recovery will be re-observed on the next tick (stale-recovery-facts)."));
                }

                facts = BuildFailedGoalRecoveryFacts(
                    goal,
                    policy,
                    currentState);
                decision = FailedGoalRecoveryPolicy.Evaluate(facts);
                if (decision.Action != FailedGoalRecoveryAction.ReconcileExitedDispatch)
                    break;
            }
        }

        state = GoalLifecycle.ResolveState(goal, GetFacts(goal));
        var stillUnapplied = goal.Tasks.FirstOrDefault(task =>
            task.LastProcess is { } process &&
            DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process));
        if (stillUnapplied is not null)
        {
            return MakeResult(
                goal.Id.Value,
                goalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    state,
                    $"Failure handling deferred for task {ShortTaskId(stillUnapplied.Id)}: latest process record exited without an applied completion (exited-unapplied-process-record)."));
        }

        return MakeResult(
            goal.Id.Value,
            goalPrefix,
            policy,
            new ConductorAdvanceOutcome.Held(
                state,
                $"Reconciled exited dispatch for task {ShortTaskId(exitedTaskIds[0])} before failure handling (reconcile-before-failure-handling); deferring the retry decision to the next tick."));
    }

    private FailedGoalRecoveryFacts BuildFailedGoalRecoveryFacts(
        Goal goal,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState state)
    {
        var maxTransientAttempts = policy.MaxEmptyOutputDispatchRetries * policy.MaxEmptyOutputAutoRecoverCycles;
        var taskFacts = goal.Tasks.Select(task =>
        {
            DispatchOutcome? outcome = null;
            if (task.LastVerification is { } verification)
                outcome = DispatchFailureClassifier.Classify(task, verification);
            var outcomeClass = outcome is null
                ? TaskOutcomeClass.UnknownEra
                : TaskOutcomeClassifier.Classify(
                    task.Status,
                    TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt)).Class;
            var staleDisposition = FailedGoalStaleRecoveryDisposition.None;
            string? staleDiagnostic = null;
            if (task.Status == WorkTaskStatus.Failed &&
                TryGetDispatchRecoveryAction(task.LastVerification, out var recoveryAction) &&
                recoveryAction is DispatchRecoveryAction.RetryStale or DispatchRecoveryAction.BudgetExhausted or DispatchRecoveryAction.MarkStale)
            {
                staleDisposition = IsRetryableStaleRecovery(task.LastVerification!)
                    ? FailedGoalStaleRecoveryDisposition.Retry
                    : FailedGoalStaleRecoveryDisposition.Escalate;
                staleDiagnostic = ExtractDispatchRecoveryDiagnostic(task.LastVerification!);
            }

            return new FailedGoalRecoveryTaskFacts(
                task.Id,
                task.RequiredRole,
                task.Status,
                task.LastProcess is { IsRunning: true },
                task.LastProcess is { } process && DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process),
                BuildFailedGoalAttemptIdentity(task),
                outcome?.Kind,
                outcome?.RecoveryRecommendation ?? RecoveryRecommendation.None,
                outcomeClass,
                outcome?.EvidenceSummary,
                staleDisposition,
                staleDiagnostic,
                task.EmptyOutputRetryCount,
                task.CriterionRetryCount,
                outcome is null ? null : AutomaticWorkerRetryCause.Resolve(task, outcome),
                task.LastVerification?.ProviderFailureKind,
                task.LastVerification?.ExitCode,
                task.LastVerification?.Command,
                ComputeEmptyOutputBackoff(policy, task.EmptyOutputRetryCount));
        });

        return new FailedGoalRecoveryFacts(
            goal.Id,
            state,
            goal.AutomaticAcceptanceRetryCount,
            policy.MaxCriterionRetries,
            maxTransientAttempts,
            BuildFailedGoalRecoveryContextVersion(goal, state),
            taskFacts,
            BuildTerminalEscalationReason(goal, state),
            transientAttemptsPerCycle: policy.MaxEmptyOutputDispatchRetries,
            transientRecoveryCycles: policy.MaxEmptyOutputAutoRecoverCycles);
    }

    private bool IsFailedGoalRecoveryContextCurrent(
        Goal goal,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState expectedState,
        FailedGoalRecoveryDecision decision,
        out string reason)
    {
        var current = GetCurrentGoal(goal);
        var state = GoalLifecycle.ResolveState(current, GetFacts(current));
        var currentFacts = BuildFailedGoalRecoveryFacts(current, policy, state);
        if (state == expectedState &&
            string.Equals(currentFacts.ContextVersion, decision.Identity.ContextVersion, StringComparison.Ordinal))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{decision.Reason} Recovery effects were held because the goal/task/attempt authority changed before application " +
            $"(stale-recovery-facts; expected={decision.Identity.ContextVersion}; actual={currentFacts.ContextVersion}; state={state}).";
        return false;
    }

    private void ApplyPendingFailedGoalNotes(Goal goal, IReadOnlyList<FailedGoalPendingNote> notes)
    {
        foreach (var note in notes)
            _recordTaskNote(goal.Id, note.TaskId, note.Message);
    }

    private static string BuildFailedGoalAttemptIdentity(TaskSpec task) => task.LastProcess is { } process
        ? $"process:{process.ProcessId}:{process.StartedAt.UtcTicks}"
        : task.LastVerification is { } verification
            ? $"verification:{verification.CompletedAt.UtcTicks}:{verification.ChildProcessId?.ToString() ?? "none"}"
            : "unobserved-attempt";

    private static string BuildFailedGoalRecoveryContextVersion(Goal goal, GoalLifecycleState state)
    {
        var taskState = string.Join(
            ";",
            goal.Tasks.Select(task => string.Join(
                ":",
                task.Id.Value,
                task.Status,
                task.EmptyOutputRetryCount,
                task.CriterionRetryCount,
                BuildFailedGoalAttemptIdentity(task),
                task.LastVerification?.CompletedAt.UtcTicks.ToString() ?? "none")));
        var lastEvent = goal.Timeline.LastOrDefault();
        return string.Join(
            "|",
            goal.Id.Value,
            goal.Status,
            state,
            goal.AuthoritativeBrief.Version,
            goal.AutomaticAcceptanceRetryCount,
            goal.Timeline.Count,
            lastEvent?.Kind.ToString() ?? "none",
            lastEvent?.OccurredAt.UtcTicks.ToString() ?? "none",
            taskState);
    }

    private static string ShortTaskId(TaskId taskId) => taskId.Value[..Math.Min(8, taskId.Value.Length)];

}
