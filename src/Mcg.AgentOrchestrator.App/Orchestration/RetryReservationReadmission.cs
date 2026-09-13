using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RetryReservationReadmission
{
    internal static bool TrySelectExpired(
        Goal goal,
        DateTimeOffset now,
        IReadOnlySet<string> alreadyReadmittedReceiptIds,
        out TaskSpec task,
        out RetryAdmissionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(alreadyReadmittedReceiptIds);

        foreach (var candidateTask in goal.Tasks.OrderBy(candidate => candidate.Id.Value, StringComparer.Ordinal))
        {
            if (candidateTask.RetryAdmissionHoldRoute != RetryAdmissionRoute.ReservationLease ||
                candidateTask.LastDispatch is not { } dispatch)
            {
                continue;
            }

            var candidateReceipt = candidateTask.RetryAdmissionHistory.LastOrDefault(candidate =>
                candidate.LinkedDispatchAt == dispatch.DispatchedAt &&
                candidate.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation &&
                candidate.Route == RetryAdmissionRoute.ReservationLease &&
                candidate.ReservationLeaseExpiresAt is not null);
            if (candidateReceipt?.ReservationLeaseExpiresAt is not { } expiresAt ||
                now < expiresAt ||
                alreadyReadmittedReceiptIds.Contains(candidateReceipt.ReceiptId))
            {
                continue;
            }

            task = candidateTask;
            receipt = candidateReceipt;
            return true;
        }

        task = null!;
        receipt = null!;
        return false;
    }
}
