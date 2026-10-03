using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static void PersistCriticalDispatchStartOrThrow(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRecordCheckpointPhase checkpointPhase = DispatchRecordCheckpointPhase.BeforeProcessStart)
    {
        var tickLines = new List<string>();
        PersistWriteAttemptResult persistResult;
        try
        {
            persistResult = TryPersistWithBusyContainmentResult(
                () => persistGoalTick(kernel, [goalId]),
                tick: 0,
                goals: ShortGoalId(goalId.Value),
                kind: "dispatch-start",
                tickLines,
                busyWriteDelay: null,
                allowLegacyMessageClassification: false);
        }
        catch (Exception ex)
        {
            var failure = DispatchRecordWriteException.From(
                ex,
                checkpointPhase,
                "dispatch-start",
                goalId,
                taskId);
            if (failure.IsFatal)
                EmitProgress(failure.Message);
            throw failure;
        }

        if (persistResult.Succeeded)
            return;

        var contentionFailure = DispatchRecordWriteException.From(
            persistResult.ContentionFailure!,
            checkpointPhase,
            "dispatch-start",
            goalId,
            taskId);
        if (contentionFailure.IsFatal)
            EmitProgress(contentionFailure.Message);
        throw contentionFailure;
    }

    private void CompletePersistedOperatorIntents(
        IReadOnlyCollection<GoalId> persistedGoalIds,
        List<string> tickLines)
    {
        if (_operatorIntents is null)
        {
            return;
        }

        try
        {
            _operatorIntents.CompletePersisted(persistedGoalIds);
        }
        catch (Exception ex)
        {
            var line =
                $"OPERATOR_INTENT goals={ResolveGoalContext(persistedGoalIds, onlyGoalId: null)} result=completion-deferred reason={SanitizeReason(ex.Message)}";
            EmitProgress(line, tickLines);
        }
    }

    private static IReadOnlyCollection<GoalId> ApplyCheckpointOutcomes(
        IReadOnlyList<GoalSnapshotCheckpointResult> outcomes,
        IReadOnlyCollection<GoalId> requestedGoalIds,
        Dictionary<string, GoalSnapshotCheckpointResult> heldGoals,
        int tick,
        string kind,
        List<string> tickLines,
        bool deferEmission = false)
    {
        var byGoal = outcomes.ToDictionary(outcome => outcome.GoalId, StringComparer.Ordinal);
        var durable = new List<GoalId>(requestedGoalIds.Count);
        foreach (var goalId in requestedGoalIds)
        {
            if (!byGoal.TryGetValue(goalId.Value, out var outcome))
            {
                throw new InvalidOperationException(
                    $"Checkpoint persistence returned no disposition for goal {ShortGoalId(goalId.Value)}.");
            }

            var goal = ShortGoalId(goalId.Value);
            if (outcome.IsDurable)
            {
                durable.Add(goalId);
                if (heldGoals.Remove(goalId.Value, out var heldOutcome))
                {
                    var line =
                        $"TICK_CHECKPOINT_RECOVERED tick={tick} kind={kind} goal={goal} store={Sanitize(heldOutcome.Store)} " +
                        $"database={SanitizeReceiptToken(heldOutcome.DatabasePath)} operation={SanitizeReceiptToken(heldOutcome.Operation)} " +
                        $"sqlite_code={heldOutcome.SqliteErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                        $"sqlite_extended_code={heldOutcome.SqliteExtendedErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                        $"attempt={outcome.AttemptCount} elapsed_ms={heldOutcome.ElapsedMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} disposition=recovered";
                    if (deferEmission)
                        tickLines.Add(line);
                    else
                        EmitProgress(line, tickLines);
                }
                continue;
            }

            var firstHoldReceipt = !heldGoals.ContainsKey(goalId.Value);
            heldGoals[goalId.Value] = outcome;
            var eventName = firstHoldReceipt ? "TICK_CHECKPOINT_HOLD" : "TICK_CHECKPOINT_RETRY";
            var disposition = firstHoldReceipt ? "exhausted-held" : "still-held";
            var holdLine =
                $"{eventName} tick={tick} kind={kind} goal={goal} store={Sanitize(outcome.Store)} " +
                $"database={SanitizeReceiptToken(outcome.DatabasePath)} operation={SanitizeReceiptToken(outcome.Operation)} " +
                $"sqlite_code={outcome.SqliteErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                $"sqlite_extended_code={outcome.SqliteExtendedErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
                $"attempt={outcome.AttemptCount} elapsed_ms={outcome.ElapsedMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
                $"disposition={disposition} holder=unknown";
            if (deferEmission)
                tickLines.Add(holdLine);
            else
                EmitProgress(holdLine, tickLines);
        }

        return durable;
    }

    private static void PersistGoalTickOrThrow(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> changedGoalIds,
        int tick,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay)
    {
        var goals = ResolveGoalContext(changedGoalIds, onlyGoalId: null);
        if (!TryPersistGoalTick(
                persistGoalTick,
                kernel,
                changedGoalIds,
                tick,
                "goal",
                tickLines,
                busyWriteDelay))
        {
            ThrowCriticalPersistFailure("goal", goals, taskId: null);
        }
    }

    private static bool TryPersistGoalTick(
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persistGoalTick,
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> changedGoalIds,
        int tick,
        string kind,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay) =>
        TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, changedGoalIds),
            tick,
            ResolveGoalContext(changedGoalIds, onlyGoalId: null),
            kind,
            tickLines,
            busyWriteDelay);

    private static void ThrowCriticalPersistFailure(string kind, string goals, TaskId? taskId)
    {
        var task = taskId is null ? "" : $" task={ShortGoalId(taskId.Value)}";
        var message = $"DISPATCH_RECORD_WRITE_FAILED kind={kind} goal={goals}{task} error=sqlite-busy-retry-exhausted";
        EmitProgress(message);
        throw new InvalidOperationException(message);
    }

    private static bool TryPersistTick(
        Action<AgentOrchestratorKernel>? persistTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string goals,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null)
    {
        if (persistTick is null)
        {
            return true;
        }

        return TryPersistWithBusyContainment(
            () => persistTick(kernel),
            tick,
            goals,
            kind,
            tickLines,
            busyWriteDelay,
            diagnosticAttempt);
    }

    private static bool TryPersistCheckpoint(
        Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string? onlyGoalId,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick = null,
        Dictionary<string, GoalSnapshotCheckpointResult>? checkpointHeldGoalIds = null)
    {
        if (checkpointGoalTick is null && persistGoalTick is null)
        {
            return TryPersistTick(
                persistTick,
                kernel,
                tick,
                ResolveGoalContext(kernel, onlyGoalId),
                kind,
                tickLines,
                busyWriteDelay,
                diagnosticAttempt);
        }

        var goalIds = ResolveCheckpointGoalIds(kernel, onlyGoalId);
        if (goalIds.Length == 0)
        {
            return true;
        }

        if (checkpointGoalTick is not null)
        {
            ArgumentNullException.ThrowIfNull(checkpointHeldGoalIds);
            var lines = tickLines ?? [];
            return ApplyCheckpointOutcomes(
                checkpointGoalTick(kernel, goalIds),
                goalIds,
                checkpointHeldGoalIds,
                tick,
                kind,
                lines).Count == goalIds.Length;
        }

        return TryPersistWithBusyContainment(
            () => persistGoalTick(kernel, goalIds),
            tick,
            ResolveGoalContext(goalIds, onlyGoalId),
            kind,
            tickLines,
            busyWriteDelay,
            diagnosticAttempt);
    }

    private void PersistGracefulDetachCheckpoint(
        Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        AgentOrchestratorKernel kernel,
        int tick,
        string? onlyGoalId,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoalIds)
    {
        if (checkpointGoalTick is not null)
        {
            if (!TryPersistCheckpoint(
                    persistTick,
                    persistGoalTick,
                    kernel,
                    tick,
                    onlyGoalId,
                    kind,
                    tickLines,
                    busyWriteDelay,
                    checkpointGoalTick: checkpointGoalTick,
                    checkpointHeldGoalIds: checkpointHeldGoalIds))
            {
                EmitProgress(
                    $"TICK_WRITE_DETACH_CHECKPOINT_DEFERRED tick={tick} kind={kind} goal={ResolveGoalContext(kernel, onlyGoalId)} disposition=held",
                    tickLines);
            }
            return;
        }

        for (var attempt = 1; attempt <= DefaultGracefulDetachCheckpointAttempts; attempt++)
        {
            if (TryPersistCheckpoint(
                    persistTick,
                    persistGoalTick,
                    kernel,
                    tick,
                    onlyGoalId,
                    kind,
                    tickLines,
                    busyWriteDelay,
                    attempt))
            {
                CompleteWriteRetryDiagnostics(ResolveGoalContext(kernel, onlyGoalId), kind, tickLines);
                return;
            }

            if (attempt == DefaultGracefulDetachCheckpointAttempts)
            {
                EmitProgress(
                    $"TICK_WRITE_DETACH_CHECKPOINT_FAILED tick={tick} kind={kind} goal={ResolveGoalContext(kernel, onlyGoalId)} attempts={attempt} recoveryEvidence=spawn-registry-lifecycle",
                    tickLines);
                CompleteWriteRetryDiagnostics(ResolveGoalContext(kernel, onlyGoalId), kind, tickLines);
                return;
            }

            var goalContext = ResolveGoalContext(kernel, onlyGoalId);
            EmitRetryDiagnostic(
                "TICK_WRITE_RETRYING",
                goalContext,
                kind,
                $"TICK_WRITE_RETRYING tick={tick} kind={kind} goal={goalContext} attempt={attempt} reason=graceful-detach-checkpoint-required",
                tickLines);
            var delay = RetryLoopPolicy.GetWriteDelay(attempt, _writeJitter());
            if (busyWriteDelay is null)
                Thread.Sleep(delay);
            else
                busyWriteDelay(delay);
        }

    }

    private static GoalId[] ResolveCheckpointGoalIds(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        var goals = onlyGoalId is null
            ? kernel.Goals.Where(goal => !IsTerminalGoal(goal))
            : kernel.Goals.Where(goal => goal.Id.Value == onlyGoalId);
        return goals.Select(goal => goal.Id).ToArray();
    }

    private static bool TryPersistWithBusyContainment(
        Action persist,
        int tick,
        string goals,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null)
        => TryPersistWithBusyContainmentResult(
            persist,
            tick,
            goals,
            kind,
            tickLines,
            busyWriteDelay,
            diagnosticAttempt).Succeeded;

    private static PersistWriteAttemptResult TryPersistWithBusyContainmentResult(
        Action persist,
        int tick,
        string goals,
        string kind,
        List<string>? tickLines,
        Action<TimeSpan>? busyWriteDelay,
        int? diagnosticAttempt = null,
        bool allowLegacyMessageClassification = true)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; attempt <= DefaultMaxBusyWriteAttempts; attempt++)
        {
            try
            {
                persist();
                return PersistWriteAttemptResult.Success;
            }
            catch (Exception ex) when (IsTransientSqliteLock(ex, allowLegacyMessageClassification))
            {
                var reportedAttempt = diagnosticAttempt ?? attempt;
                EmitRetryDiagnostic(
                    "TICK_WRITE_BUSY",
                    goals,
                    kind,
                    $"TICK_WRITE_BUSY tick={tick} kind={kind} goal={goals} attempt={reportedAttempt} holder=unknown",
                    tickLines);

                if (attempt == DefaultMaxBusyWriteAttempts)
                {
                    EmitRetryDiagnostic(
                        "TICK_WRITE_DEGRADED",
                        goals,
                        kind,
                        $"TICK_WRITE_DEGRADED tick={tick} kind={kind} goal={goals} attempt={reportedAttempt} disposition=exhausted holder=unknown error={SanitizeReason(ex.Message)}",
                        tickLines);
                    return new PersistWriteAttemptResult(false, ex);
                }

                if (busyWriteDelay is null)
                    Thread.Sleep(delay);
                else
                    busyWriteDelay(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }

        throw new UnreachableException("The bounded persistence loop must return on success or final contention.");
    }

    private static bool IsTransientSqliteLock(Exception ex, bool allowLegacyMessageClassification = true)
    {
        if (DispatchRecordWriteException.IsSqliteBusyOrLocked(ex))
        {
            return true;
        }

        if (allowLegacyMessageClassification
            && LegacySqliteLockMessageClassifier.IsBusyOrLocked(ex))
        {
            return true;
        }

        return ex.InnerException is not null
            && IsTransientSqliteLock(ex.InnerException, allowLegacyMessageClassification);
    }

    private readonly record struct PersistWriteAttemptResult(bool Succeeded, Exception? ContentionFailure)
    {
        internal static PersistWriteAttemptResult Success => new(true, null);
    }

    private static void EmitRetryDiagnostic(
        string eventName,
        string goal,
        string condition,
        string verbatim,
        List<string>? tickLines = null)
    {
        var line = CurrentRetryDiagnostics.Value is { } diagnostics
            ? diagnostics.Observe(new RetryDiagnosticKey(eventName, goal, condition), verbatim)
            : verbatim;
        if (line is not null)
            EmitProgress(line, tickLines);
    }

    private static void CompleteWriteRetryDiagnostics(string goal, string kind, List<string>? tickLines)
    {
        CompleteRetryDiagnostic("TICK_WRITE_BUSY", goal, kind, tickLines);
        CompleteRetryDiagnostic("TICK_WRITE_DEGRADED", goal, kind, tickLines);
        CompleteRetryDiagnostic("TICK_WRITE_RETRYING", goal, kind, tickLines);
    }

    private static void CompleteRetryDiagnostic(
        string eventName,
        string goal,
        string condition,
        List<string>? tickLines = null)
    {
        var line = CurrentRetryDiagnostics.Value?.Complete(
            new RetryDiagnosticKey(eventName, goal, condition));
        if (line is not null)
            EmitProgress(line, tickLines);
    }
}
