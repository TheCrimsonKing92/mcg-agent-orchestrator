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
        DateTimeOffset linkedDispatchAt,
        DateTimeOffset recordedAt,
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
                    linkedDispatchAt,
                    recordedAt);
                return Task.FromResult((true, (GoalSnapshot?)reservation.Snapshot, (RetryAdmissionResult?)reservation.Admission));
            },
            cancellationToken);
    }
}
