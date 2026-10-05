using System.Diagnostics.CodeAnalysis;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Owns the two decisions the exited-process sweep used to make inline and inconsistently:
/// which tasks it may reconcile, and whether an applied exit may also requeue the dispatch.
///
/// The sweep is the single owner of exit application. Its old status filter skipped
/// <see cref="WorkTaskStatus.Failed"/> before the process/cancellation/already-applied guards were
/// ever consulted, so a round that exited after the task had already been marked Failed was never
/// reconciled and its exit artifact sat unapplied indefinitely. A Failed task is now eligible, and
/// the requeue fence keeps that application from re-preparing a dispatch behind the operator's back.
/// </summary>
public static class DispatchExitSweepEligibility
{
    /// <summary>
    /// True when the sweep may read <paramref name="process"/>'s exit artifact and apply the result.
    ///
    /// <see cref="WorkTaskStatus.WaitingForHuman"/> stays skipped because a live worker holding a
    /// pending question is the normal state there, not an unapplied exit.
    /// <see cref="WorkTaskStatus.Cancelled"/> stays skipped because its process may have exited
    /// naturally with <c>WasCancelled</c> false, and applying that exit would resurrect verification
    /// on work the operator abandoned.
    /// </summary>
    public static bool IsEligibleForExitSweep(TaskSpec task, [NotNullWhen(true)] TaskProcessRecord? process)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.Status is not (WorkTaskStatus.WaitingForHuman or WorkTaskStatus.Cancelled) &&
               process is not null &&
               !process.WasCancelled &&
               !DispatchProcessCompletionState.HasAlreadyBeenApplied(task, process);
    }

    /// <summary>
    /// True when applying an exit for a task already in a terminal status must not also requeue it.
    /// </summary>
    public static bool SuppressesAutoRequeue(TaskSpec task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.Status is WorkTaskStatus.Failed or WorkTaskStatus.Cancelled or WorkTaskStatus.Completed;
    }

    /// <summary>
    /// Strips the requeue from an auto-requeue disposition when the task's status is already terminal,
    /// while preserving the disposition itself so the journal still records why the exit was applied.
    ///
    /// Must be evaluated against the task status observed before the outcome is applied.
    /// </summary>
    public static DispatchRefreshOutcome FenceAutoRequeue(TaskSpec task, DispatchRefreshOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.AutoRequeueDisposition is not { ShouldRequeue: true } disposition ||
               !SuppressesAutoRequeue(task)
            ? outcome
            : outcome with { AutoRequeueDisposition = disposition with { ShouldRequeue = false } };
    }
}
