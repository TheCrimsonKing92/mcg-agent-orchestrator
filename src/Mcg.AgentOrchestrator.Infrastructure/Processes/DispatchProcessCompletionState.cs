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
}
