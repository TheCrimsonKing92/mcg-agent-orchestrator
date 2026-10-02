using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum DispatchRecordCheckpointPhase
{
    BeforeProcessStart,
    ProcessMayHaveStarted,
    BeforeRetryAdmission
}

public sealed record DispatchRefreshOutcome(
    TaskProcessRecord ProcessRecord,
    TaskVerificationRecord? Verification,
    string? ResultCommit = null,
    string? ResultCommitProvenance = null,
    DispatchRecoveryDecision? RecoveryDecision = null,
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown,
    DispatchDiagnosticPayload? DiagnosticPayload = null,
    DispatchAutoRequeueDisposition? AutoRequeueDisposition = null,
    ProviderReportedUsage? ProviderUsage = null,
    string ProviderUsageUnavailableReason = "unsupported",
    DateTimeOffset? DispatchAttemptAt = null,
    DateTimeOffset? ReceiptlessUsageAttemptAt = null);

public sealed record DispatchDiagnosticPayload(int ExitCode, string StandardOutput, string StandardError);

public sealed record DispatchAutoRequeueDisposition(
    string EventName,
    string Message,
    bool ShouldRequeue = true,
    InterruptedWorkCheckpoint? Checkpoint = null,
    string? InterruptedDispatchId = null)
{
    internal static DispatchAutoRequeueDisposition? FromInterruptedWorkCheckpoint(
        InterruptedWorkCheckpointDisposition? disposition,
        DispatchRecoveryDecision? recoveryDecision)
    {
        if (disposition?.IsCheckpoint == true)
        {
            return new DispatchAutoRequeueDisposition(
                "InterruptedDispatchWorkCheckpointed",
                disposition.Message,
                Checkpoint: disposition.Checkpoint,
                InterruptedDispatchId: disposition.Checkpoint!.DispatchId);
        }

        return recoveryDecision?.Action == DispatchRecoveryAction.PreserveInterruptedWork &&
            disposition is { Kind: not InterruptedWorkCheckpointDispositionKind.NotApplicable }
                ? new DispatchAutoRequeueDisposition(
                    "InterruptedDispatchWorkPreserved",
                    disposition.Message,
                    ShouldRequeue: false)
                : null;
    }
}

public sealed record DispatchProcessStartResult(
    TaskProcessRecord? ProcessRecord,
    WorkerSandboxPrepRecoverableAction? RecoveryAction,
    bool RequeueSkipped = false,
    string? FailureReason = null)
{
    public static DispatchProcessStartResult Started(TaskProcessRecord processRecord) => new(processRecord, null);
    public static DispatchProcessStartResult RequiresRecovery(WorkerSandboxPrepRecoverableAction action) => new(null, action);
    public static DispatchProcessStartResult Skipped() => new(null, null, RequeueSkipped: true);
    public static DispatchProcessStartResult Failed(string reason) => new(null, null, FailureReason: reason);
}

public sealed record InterruptedDispatchStateRead(
    GoalStatus? GoalStatus,
    WorkTaskStatus? TaskStatus,
    string? UnreadableEntity = null,
    string? Error = null,
    bool WasTaskCancelledByConductor = false,
    bool WasTaskGracefullyDetachedByConductor = false)
{
    public bool IsReadable => UnreadableEntity is null;
    public static InterruptedDispatchStateRead Unreadable(string entity, string error) => new(null, null, entity, error);
}
