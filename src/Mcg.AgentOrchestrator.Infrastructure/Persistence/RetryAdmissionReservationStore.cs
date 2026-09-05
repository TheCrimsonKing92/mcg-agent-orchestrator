using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class RetryAdmissionReservationStore
{
    public static Task<RetryAdmissionSnapshotResult?> TryReserveAsync(
        string stateDatabasePath,
        GoalId goalId,
        TaskId taskId,
        RetryContextFingerprint fingerprint,
        PaidRouteClassification paidRoute,
        RetryCause cause,
        TaskDispatchRecord preparedDispatch,
        DateTimeOffset recordedAt,
        string reservationOwnerId,
        DateTimeOffset reservationLeaseExpiresAt,
        bool reservationRecoveryConfirmed = false,
        CancellationToken cancellationToken = default,
        DateTimeOffset? retryMarkerAt = null,
        RetryRoundKind? retryRoundKind = null,
        string? retryMessage = null)
    {
        var repository = new SqliteOrchestratorStateRepository(stateDatabasePath);
        return repository.TransactGoalStateAsync<RetryAdmissionSnapshotResult?>(
            "retry-admission-reservation",
            goalId,
            (state, _) =>
            {
                if (state is null)
                    return Task.FromResult((false, (GoalStateSnapshot?)null, (RetryAdmissionSnapshotResult?)null));

                var reservationSnapshot = state.Goal;
                AgentOrchestratorKernel? retryKernel = null;
                RetryMarkerClock? retryClock = null;
                TaskSnapshot? taskBeforeRetry = null;
                if (retryMarkerAt is { } retriedAt)
                {
                    if (string.IsNullOrWhiteSpace(retryMessage))
                    {
                        throw new InvalidOperationException(
                            $"Retry admission cannot persist task '{taskId}' without its TaskRetried message.");
                    }

                    var persistedTask = reservationSnapshot.Tasks.Single(task => task.Id == taskId.Value);
                    taskBeforeRetry = persistedTask;
                    var persistedRetryEvent = reservationSnapshot.Timeline.LastOrDefault(evt =>
                        evt.TaskId == taskId.Value &&
                        evt.Kind == ProgressKind.TaskRetried &&
                        evt.OccurredAt >= retriedAt);
                    var retryEventAlreadyPersisted =
                        persistedTask.LatestRetryAt == retriedAt &&
                        string.Equals(persistedRetryEvent?.Message, retryMessage, StringComparison.Ordinal);
                    retryClock = new RetryMarkerClock(retriedAt);
                    retryKernel = AgentOrchestratorKernel.FromSnapshot(
                        new OrchestratorSnapshot([reservationSnapshot], state.HumanInputRequests),
                        retryClock);
                    if (!retryEventAlreadyPersisted)
                    {
                        retryKernel.RetryTask(
                            goalId,
                            taskId,
                            retryMessage,
                            cause,
                            retryRoundKind: retryRoundKind);
                    }

                    reservationSnapshot = retryKernel.ExportGoalSnapshot(goalId);
                }

                var reservation = RetryAdmissionSnapshotReservation.Apply(
                    reservationSnapshot,
                    taskId,
                    fingerprint,
                    paidRoute,
                    cause,
                    preparedDispatch,
                    recordedAt,
                    reservationOwnerId,
                    reservationLeaseExpiresAt,
                    reservationRecoveryConfirmed);
                // A denied admission persists only the retry marker, leaving a failed task Failed; the prepared dispatch is persisted only when start is allowed.
                if (retryKernel is not null && reservation.Admission.AllowsProcessStart)
                {
                    var durableTask = retryKernel.GetTask(goalId, taskId);
                    var preparedDispatchAlreadyPersisted =
                        durableTask.LastDispatch?.DispatchedAt == preparedDispatch.DispatchedAt &&
                        string.Equals(durableTask.LastDispatch.WorkerName, preparedDispatch.WorkerName, StringComparison.Ordinal);
                    if (!preparedDispatchAlreadyPersisted)
                    {
                        retryClock!.UtcNow = recordedAt;
                        retryKernel.RecordTaskDispatch(goalId, taskId, preparedDispatch);
                    }

                    reservation = RetryAdmissionSnapshotReservation.Apply(
                        retryKernel.ExportGoalSnapshot(goalId), taskId, fingerprint, paidRoute, cause,
                        preparedDispatch, recordedAt, reservationOwnerId, reservationLeaseExpiresAt,
                        reservationRecoveryConfirmed);
                }
                else if (taskBeforeRetry is not null)
                {
                    var deniedTask = reservation.Snapshot.Tasks.Single(task => task.Id == taskId.Value);
                    var retryMarkedTask = taskBeforeRetry with
                    {
                        LatestRetryAt = deniedTask.LatestRetryAt,
                        PendingRetryRoundKind = deniedTask.PendingRetryRoundKind,
                        PendingRetryCause = deniedTask.PendingRetryCause,
                        RetryAdmissionHistory = deniedTask.RetryAdmissionHistory,
                        RetryAdmissionHoldRoute = deniedTask.RetryAdmissionHoldRoute,
                        PendingReviewFindingRepairCheckpoint = deniedTask.PendingReviewFindingRepairCheckpoint
                    };
                    reservation = reservation with
                    {
                        Snapshot = reservation.Snapshot with
                        {
                            Tasks = reservation.Snapshot.Tasks
                                .Select(task => task.Id == taskId.Value ? retryMarkedTask : task)
                                .ToArray()
                        }
                    };
                }
                var kernel = AgentOrchestratorKernel.FromSnapshot(
                    new OrchestratorSnapshot([reservation.Snapshot], state.HumanInputRequests));
                kernel.ApplyPersistedRetryAdmissionOutcome(goalId, taskId, reservation.Admission);
                var persisted = kernel.ExportSnapshot();
                var result = new RetryAdmissionSnapshotResult(
                    persisted.Goals.Single(),
                    reservation.Admission,
                    persisted.HumanInputRequests);
                return Task.FromResult((
                    true,
                    (GoalStateSnapshot?)new GoalStateSnapshot(result.Snapshot, result.HumanInputRequests ?? []),
                    (RetryAdmissionSnapshotResult?)result));
            },
            cancellationToken);
    }

    private sealed class RetryMarkerClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    public static Task<bool?> TryClaimStartAsync(
        string stateDatabasePath,
        GoalId goalId,
        TaskId taskId,
        DateTimeOffset linkedDispatchAt,
        string reservationOwnerId,
        DateTimeOffset workerStartedAt,
        CancellationToken cancellationToken = default)
    {
        var repository = new SqliteOrchestratorStateRepository(stateDatabasePath);
        return repository.TransactGoalAsync<bool?>(
            "retry-admission-start-claim",
            goalId,
            (snapshot, _) =>
            {
                if (snapshot is null)
                    return Task.FromResult((false, (GoalSnapshot?)null, (bool?)false));

                var claim = RetryAdmissionSnapshotStartClaim.Apply(
                    snapshot,
                    taskId,
                    linkedDispatchAt,
                    reservationOwnerId,
                    workerStartedAt);
                return Task.FromResult((true, (GoalSnapshot?)claim.Snapshot, (bool?)claim.Claimed));
            },
            cancellationToken);
    }

    public static Task<RetryAdmissionStartClaimResult?> TryClaimStartSnapshotAsync(
        string stateDatabasePath,
        GoalId goalId,
        TaskId taskId,
        DateTimeOffset linkedDispatchAt,
        string reservationOwnerId,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default) =>
        TransactStartStateAsync(
            "retry-admission-start-claim",
            stateDatabasePath,
            goalId,
            snapshot => RetryAdmissionSnapshotStartClaim.Apply(
                snapshot, taskId, linkedDispatchAt, reservationOwnerId, claimedAt),
            cancellationToken);

    public static Task<RetryAdmissionStartClaimResult?> TryConfirmStartAsync(
        string stateDatabasePath,
        GoalId goalId,
        TaskId taskId,
        DateTimeOffset linkedDispatchAt,
        string reservationOwnerId,
        DateTimeOffset workerStartedAt,
        CancellationToken cancellationToken = default) =>
        TransactStartStateAsync(
            "retry-admission-start-confirmation",
            stateDatabasePath,
            goalId,
            snapshot => RetryAdmissionSnapshotStartConfirmation.Apply(
                snapshot, taskId, linkedDispatchAt, reservationOwnerId, workerStartedAt),
            cancellationToken);

    private static Task<RetryAdmissionStartClaimResult?> TransactStartStateAsync(
        string operation,
        string stateDatabasePath,
        GoalId goalId,
        Func<GoalSnapshot, RetryAdmissionStartClaimResult> apply,
        CancellationToken cancellationToken)
    {
        var repository = new SqliteOrchestratorStateRepository(stateDatabasePath);
        return repository.TransactGoalAsync<RetryAdmissionStartClaimResult?>(
            operation,
            goalId,
            (snapshot, _) =>
            {
                if (snapshot is null)
                    return Task.FromResult((false, (GoalSnapshot?)null, (RetryAdmissionStartClaimResult?)null));

                var result = apply(snapshot);
                return Task.FromResult((true, (GoalSnapshot?)result.Snapshot, (RetryAdmissionStartClaimResult?)result));
            },
            cancellationToken);
    }
}
