using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum WorkerBuildNoBuildCause { None, LockTimeout, SlotCapacity }

internal static class WorkerBuildCheckRecoveryText
{
    internal const string BuildSlotLockTimeout = "Timed out waiting for build lease execution lock";
    internal const string BuildSlotLockTimeoutBlock =
        "build-slot lock timeout: the build check never ran because the build-slot lock timed out.";
    internal const string SlotCapacityOutcome = "outcome=CapacityBeforeStart";
    internal const string SlotCapacityBlock =
        "build-slot capacity: no compiler ran because every host build slot stayed busy for the whole queue wait; this is not a code verdict.";
    internal const string RetryAppliedHeld =
        "Worker build check retry was applied; dispatch start awaits a fresh lifecycle observation.";

    internal static WorkerBuildNoBuildCause NoBuildCause(string output)
    {
        if (output.Split('\n').Any(WorkerBuildLogErrorReader.IsCompilerDiagnostic))
            return WorkerBuildNoBuildCause.None;
        if (output.Contains(BuildSlotLockTimeout, StringComparison.Ordinal))
            return WorkerBuildNoBuildCause.LockTimeout;
        return output.Contains(SlotCapacityOutcome, StringComparison.Ordinal)
            ? WorkerBuildNoBuildCause.SlotCapacity : WorkerBuildNoBuildCause.None;
    }

    internal static string ErrorBlock(string? formattedErrors,
        WorkerBuildNoBuildCause cause = WorkerBuildNoBuildCause.LockTimeout) =>
        formattedErrors ?? (cause == WorkerBuildNoBuildCause.SlotCapacity ? SlotCapacityBlock : BuildSlotLockTimeoutBlock);

    internal static string CheckpointSubject(bool exhausted, int recoveryCount, string taskId) => exhausted
        ? $"checkpoint: worker-build-check-failed exhausted 2/2 for task {taskId}"
        : $"checkpoint: worker-build-check-failed recovery {recoveryCount + 1}/2 for task {taskId}";

    internal static string EscalationReason(string policyReason, string detail, string errorBlock) =>
        $"{policyReason}{Environment.NewLine}{detail}{Environment.NewLine}{errorBlock}";

    internal static string CapacityHeldReason(WorkerBuildCapacitySnapshot snapshot, int rounds) =>
        $"worker build capacity: waiting for a host build permit; unheld={snapshot.UnheldPermits}/{snapshot.TotalPermits}; " +
        $"waiting={snapshot.WaitingTickets}; capacity rounds={rounds}/{WorkerBuildCapacityGate.MaxCapacityRetries}";

    internal static string CapacityCheckpointSubject(bool exhausted, int rounds, string taskId) => exhausted
        ? $"checkpoint: worker-build-capacity exhausted 3/3 for task {taskId}"
        : $"checkpoint: worker-build-capacity retry {rounds}/3 for task {taskId}";

    internal static string CapacityRetryFeedback(int rounds, bool committed, string checkpoint) =>
        $"worker-build-capacity automatic retry {rounds}/3:" +
        Environment.NewLine + (committed ? $"checkpoint commit {checkpoint}" :
            $"no uncommitted changes were found; goal branch HEAD {checkpoint}") +
        Environment.NewLine + SlotCapacityBlock +
        Environment.NewLine + "Rerun scripts/Invoke-WorkerBuildCheck.ps1 after the last edit.";

    internal static string RetryFeedback(int recoveryCount, bool committed, string checkpoint, string? formattedErrors,
        WorkerBuildNoBuildCause cause = WorkerBuildNoBuildCause.LockTimeout) =>
        $"worker-build-check-failed automatic recovery {recoveryCount + 1}/2:" +
        Environment.NewLine + (committed ? $"checkpoint commit {checkpoint}" :
            $"no uncommitted changes were found; goal branch HEAD {checkpoint}") +
        Environment.NewLine + (formattedErrors is null
            ? ErrorBlock(null, cause) + Environment.NewLine + "Rerun scripts/Invoke-WorkerBuildCheck.ps1 after the last edit."
            : "Fix only what the build reports below, and run scripts/Invoke-WorkerBuildCheck.ps1 after the last edit." +
                Environment.NewLine + formattedErrors);
}
