using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum AcceptanceAttemptCancellationCause
{
    None,
    GoalRecordMissing,
    GoalRecordUnreadable,
    StoppedDisposition,
    AttemptInvalidated
}

internal sealed record AcceptanceAttemptCancellationDecision(
    bool ShouldCancel,
    AcceptanceAttemptCancellationCause Cause,
    GoalStatus? ObservedStatus)
{
    internal static AcceptanceAttemptCancellationDecision Continue(GoalStatus status) =>
        new(false, AcceptanceAttemptCancellationCause.None, status);

    internal static AcceptanceAttemptCancellationDecision Cancel(
        AcceptanceAttemptCancellationCause cause,
        GoalStatus? observedStatus = null) =>
        new(true, cause, observedStatus);
}

internal static class AcceptanceAttemptCancellation
{
    internal static AcceptanceAttemptCancellationDecision Decide(
        GoalStatus? observedStatus,
        bool goalRecordReadable,
        bool attemptInvalidationRecorded,
        bool explicitParkHoldsAdmissionOnly = false)
    {
        if (!goalRecordReadable)
        {
            return AcceptanceAttemptCancellationDecision.Cancel(
                AcceptanceAttemptCancellationCause.GoalRecordUnreadable);
        }

        if (!observedStatus.HasValue)
        {
            return AcceptanceAttemptCancellationDecision.Cancel(
                AcceptanceAttemptCancellationCause.GoalRecordMissing);
        }

        // Legacy parks retain their stop behavior. A typed explicit park holds new admission
        // while already-owned work drains; cancellation still requires a separate stop or
        // attempt invalidation. Active and AcceptanceFailed can be lagging pre-gate rows.
        if (observedStatus.Value == GoalStatus.Parked && !explicitParkHoldsAdmissionOnly ||
            observedStatus.Value is GoalStatus.Cancelled or
            GoalStatus.Superseded or
            GoalStatus.Failed)
        {
            return AcceptanceAttemptCancellationDecision.Cancel(
                AcceptanceAttemptCancellationCause.StoppedDisposition,
                observedStatus);
        }

        if (attemptInvalidationRecorded)
        {
            return AcceptanceAttemptCancellationDecision.Cancel(
                AcceptanceAttemptCancellationCause.AttemptInvalidated,
                observedStatus);
        }

        return AcceptanceAttemptCancellationDecision.Continue(observedStatus.Value);
    }
}

internal sealed class AcceptanceAttemptCancelledException : OperationCanceledException
{
    internal AcceptanceAttemptCancelledException(
        AcceptanceAttemptCancellationDecision decision,
        Exception? innerException = null)
        : base($"Acceptance attempt cancelled: {decision.Cause}.", innerException)
    {
        Decision = decision;
    }

    internal AcceptanceAttemptCancellationDecision Decision { get; }
}
