using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DispatchProcessCompletionState
{
    public static bool HasAlreadyBeenApplied(TaskSpec task, TaskProcessRecord process) =>
        (task.Status == WorkTaskStatus.Completed &&
         task.LastVerification is not null &&
         process.CompletedAt is not null &&
         process.ExitCode is not null) ||
        (process.CompletedAt is not null &&
         process.ExitCode is not null &&
         task.VerificationHistory.Any(verification =>
             verification.ExitCode == process.ExitCode &&
             verification.Command.Equals(process.Command, StringComparison.Ordinal) &&
             verification.WorkingDirectory.Equals(process.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
             verification.CompletedAt == process.CompletedAt));

    public static bool IsExitedWithoutAppliedCompletion(TaskSpec task, TaskProcessRecord process) =>
        process is { CompletedAt: not null, ExitCode: not null } &&
        !process.WasCancelled &&
        !HasAlreadyBeenApplied(task, process);

    /// <summary>
    /// Widens <see cref="IsExitedWithoutAppliedCompletion(TaskSpec, TaskProcessRecord)"/> to also count a
    /// round whose exit artifact is already on disk but has never been read back onto the process record.
    /// That shape — a durably Running process with a present exit artifact — is precisely the round whose
    /// exit sits unapplied, and it is invisible to the record-only predicate.
    /// </summary>
    public static bool IsExitedWithoutAppliedCompletion(
        TaskSpec task,
        TaskProcessRecord process,
        Func<string, bool> exitArtifactExists)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(exitArtifactExists);

        return !process.WasCancelled &&
               !HasAlreadyBeenApplied(task, process) &&
               (process is { CompletedAt: not null, ExitCode: not null } ||
                (!string.IsNullOrWhiteSpace(process.ExitCodePath) && exitArtifactExists(process.ExitCodePath)));
    }
}
