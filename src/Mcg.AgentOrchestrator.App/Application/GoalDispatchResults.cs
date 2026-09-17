using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal sealed record DispatchProcessStartFailure(TaskId TaskId, string Reason);

internal sealed record DispatchProcessStartRefusal(TaskId TaskId, string Reason);

internal sealed record ProcessBatchExecutionResult(
    ProcessBatchPlan Plan,
    IReadOnlyList<TaskSpec> Tasks,
    IReadOnlyList<WorkerSandboxPrepRecoverableAction>? RecoveryActions = null,
    int RequeueSkippedCount = 0,
    IReadOnlyList<DispatchProcessStartFailure>? StartFailures = null,
    IReadOnlyList<DispatchRefreshOutcome>? RefreshOutcomes = null,
    IReadOnlyList<DispatchProcessStartRefusal>? StartRefusals = null);

internal sealed record SubscriptionStartResult(
    IReadOnlyList<WorkerProfileDispatchResult> Dispatches,
    ProcessBatchExecutionResult Processes,
    ParallelExecutionPlan ParallelPlan,
    IReadOnlyList<ReadyBlockedDiagnostic> BlockedDiagnostics);
