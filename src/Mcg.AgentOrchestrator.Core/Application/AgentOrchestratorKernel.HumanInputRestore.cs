namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static RetryAdmissionReceipt? GetRefusedUnstartedDispatch(TaskSpec task)
    {
        var latestAdmission = task.RetryAdmissionHistory.LastOrDefault(receipt =>
            receipt.Decision is RetryAdmissionDecision.Allowed or
                RetryAdmissionDecision.ResumedReservation or RetryAdmissionDecision.Prevented);
        if (latestAdmission is not { Decision: RetryAdmissionDecision.Prevented } ||
            task.LastProcess is { IsRunning: true } ||
            task.LastProcess is { } process && process.StartedAt >= latestAdmission.LinkedDispatchAt ||
            task.LastDispatch is { } dispatch && dispatch.DispatchedAt > latestAdmission.LinkedDispatchAt)
            return null;

        return task.RetryAdmissionHistory.Any(receipt =>
            receipt.LinkedDispatchAt == latestAdmission.LinkedDispatchAt &&
            (receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation ||
             receipt.WorkerStartClaimedAt is not null || receipt.WorkerStartedAt is not null))
            ? null
            : latestAdmission;
    }

    private static void ClearStaleVerificationForAnsweredRestore(TaskSpec task)
    {
        // Keep the prior round in verification history while allowing its answered continuation to dispatch.
        if (task.LastVerification is { Succeeded: true }) task.ClearLatestVerification();
    }
}
