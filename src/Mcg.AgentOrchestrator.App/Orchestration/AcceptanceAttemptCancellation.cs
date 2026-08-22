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
        bool attemptInvalidationRecorded)
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

        // A gate cannot be admitted from these dispositions: Parked is excluded before the batch walk,
        // and Failed/Cancelled/Superseded are excluded by GoalStatusSemantics. Seeing one after a gate
        // started therefore records a real stop, while Active and AcceptanceFailed may merely be lagging
        // pre-gate rows and are not stop signals on their own.
        if (observedStatus.Value is GoalStatus.Parked or
            GoalStatus.Cancelled or
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
