using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class RetryAdmissionReservationStore
{
    public static Task<RetryAdmissionResult?> TryReserveAsync(
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
        CancellationToken cancellationToken = default)
    {
        var repository = new SqliteOrchestratorStateRepository(stateDatabasePath);
        return repository.TransactGoalAsync<RetryAdmissionResult?>(
            "retry-admission-reservation",
            goalId,
            (snapshot, _) =>
            {
                if (snapshot is null)
                    return Task.FromResult((false, (GoalSnapshot?)null, (RetryAdmissionResult?)null));

                var reservation = RetryAdmissionSnapshotReservation.Apply(
                    snapshot,
                    taskId,
                    fingerprint,
                    paidRoute,
                    cause,
                    preparedDispatch,
                    recordedAt,
                    reservationOwnerId,
                    reservationLeaseExpiresAt,
                    reservationRecoveryConfirmed);
                return Task.FromResult((true, (GoalSnapshot?)reservation.Snapshot, (RetryAdmissionResult?)reservation.Admission));
            },
            cancellationToken);
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
                    return Task.FromResult((false, (GoalSnapshot?)null, (bool?)null));

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
}
