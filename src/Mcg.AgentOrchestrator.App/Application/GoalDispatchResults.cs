using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

public sealed record DispatchProcessStartFailure(TaskId TaskId, string Reason);

public enum DispatchAssignmentHoldCode
{
    AgentNotFound,
    AgentUnavailable,
    RoleMismatch,
    ProfileUnavailable,
    HarnessRebindUnsupported
}

public sealed record DispatchAssignmentHold(
    DispatchAssignmentHoldCode Code,
    string Message,
    string? AssignedAgentId = null,
    string? WorkerProfileName = null);

internal sealed class DispatchAssignmentHoldException(DispatchAssignmentHold hold)
    : InvalidOperationException(hold.Message)
{
    internal DispatchAssignmentHold Hold { get; } = hold;
}

public sealed record DispatchProcessStartRefusal(
    TaskId TaskId,
    string Reason,
    DispatchAssignmentHold? AssignmentHold = null);

public sealed record ProcessBatchExecutionResult(
    ProcessBatchPlan Plan,
    IReadOnlyList<TaskSpec> Tasks,
    IReadOnlyList<WorkerSandboxPrepRecoverableAction>? RecoveryActions = null,
    int RequeueSkippedCount = 0,
    IReadOnlyList<DispatchProcessStartFailure>? StartFailures = null,
    IReadOnlyList<DispatchRefreshOutcome>? RefreshOutcomes = null,
    IReadOnlyList<DispatchProcessStartRefusal>? StartRefusals = null);

public sealed record SubscriptionStartResult(
    IReadOnlyList<WorkerProfileDispatchResult> Dispatches,
    ProcessBatchExecutionResult Processes,
    ParallelExecutionPlan ParallelPlan,
    IReadOnlyList<ReadyBlockedDiagnostic> BlockedDiagnostics);
