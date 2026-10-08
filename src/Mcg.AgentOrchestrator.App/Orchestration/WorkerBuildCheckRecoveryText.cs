namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkerBuildCheckRecoveryText
{
    internal const string BuildSlotLockTimeout = "Timed out waiting for build lease execution lock";
    internal const string BuildSlotLockTimeoutBlock =
        "build-slot lock timeout: the build check never ran because the build-slot lock timed out.";
    internal const string RetryAppliedHeld =
        "Worker build check retry was applied; dispatch start awaits a fresh lifecycle observation.";

    internal static string ErrorBlock(string? formattedErrors) => formattedErrors ?? BuildSlotLockTimeoutBlock;

    internal static string CheckpointSubject(bool exhausted, int recoveryCount, string taskId) => exhausted
        ? $"checkpoint: worker-build-check-failed exhausted 2/2 for task {taskId}"
        : $"checkpoint: worker-build-check-failed recovery {recoveryCount + 1}/2 for task {taskId}";

    internal static string EscalationReason(string policyReason, string detail, string errorBlock) =>
        $"{policyReason}{Environment.NewLine}{detail}{Environment.NewLine}{errorBlock}";

    internal static string RetryFeedback(int recoveryCount, bool committed, string checkpoint, string? formattedErrors) =>
        $"worker-build-check-failed automatic recovery {recoveryCount + 1}/2:" +
        Environment.NewLine + (committed ? $"checkpoint commit {checkpoint}" :
            $"no uncommitted changes were found; goal branch HEAD {checkpoint}") +
        Environment.NewLine + (formattedErrors is null
            ? BuildSlotLockTimeoutBlock + Environment.NewLine + "Rerun scripts/Invoke-WorkerBuildCheck.ps1 after the last edit."
            : "Fix only what the build reports below, and run scripts/Invoke-WorkerBuildCheck.ps1 after the last edit." +
                Environment.NewLine + formattedErrors);
}
