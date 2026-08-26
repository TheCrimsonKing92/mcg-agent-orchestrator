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
        CancellationToken cancellationToken = default)
    {
        var repository = new SqliteOrchestratorStateRepository(stateDatabasePath);
        return repository.TransactGoalStateAsync<RetryAdmissionSnapshotResult?>(
            "retry-admission-reservation",
            goalId,
            (state, _) =>
            {
                if (state is null)
                    return Task.FromResult((false, (GoalStateSnapshot?)null, (RetryAdmissionSnapshotResult?)null));

                var reservation = RetryAdmissionSnapshotReservation.Apply(
                    state.Goal,
                    taskId,
                    fingerprint,
                    paidRoute,
                    cause,
                    preparedDispatch,
                    recordedAt,
                    reservationOwnerId,
                    reservationLeaseExpiresAt,
                    reservationRecoveryConfirmed);
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
