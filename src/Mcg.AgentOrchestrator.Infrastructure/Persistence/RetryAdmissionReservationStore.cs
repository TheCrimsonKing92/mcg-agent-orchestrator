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
                    reservationLeaseExpiresAt);
                return Task.FromResult((true, (GoalSnapshot?)reservation.Snapshot, (RetryAdmissionResult?)reservation.Admission));
            },
            cancellationToken);
    }
}
