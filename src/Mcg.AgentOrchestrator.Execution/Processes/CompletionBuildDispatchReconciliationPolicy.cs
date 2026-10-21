using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the boundary between attached build evidence and the current worker's completion.
// Historical operations retain custody and identity, but cannot certify a newer dispatch.
internal static class CompletionBuildDispatchReconciliationPolicy
{
    internal static bool HasCurrentWorker(TaskSpec task) =>
        task.CompletionBuildEvidence is { } operation && task.LastDispatch?.DispatchedAt == operation.DispatchAt &&
        task.LastProcess is { } process && process.ProcessId == operation.WorkerProcess.ProcessId &&
        process.StartedAt == operation.WorkerProcess.StartedAt;

    internal static bool HoldsRefresh(TaskSpec task, TaskProcessRecord process) =>
        process.WasCancelled && task.CompletionBuildEvidence is { Phase: CompletionBuildEvidencePhase.Cancelled } ||
        task.CompletionBuildEvidence is { } operation && !HasCurrentWorker(task) &&
        (operation.IsPending || !operation.CustodyReleased);

    internal static OrchestratorBuildEvidenceResolution Resolve(
        TaskSpec task,
        string worktreeRoot,
        IEnumerable<string> changedPaths,
        IEnumerable<string> dirtyPaths,
        bool failedWorkerBuildCheck,
        Func<WorkerBuildReceiptVerdict> workerBuildReceipt,
        Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult> runBuildCheck,
        Func<OrchestratorBuildCheckRequest, CompletionBuildEvidenceOperation>? reconcileOperation,
        GoalId? goalId = null)
    {
        var historical = task.CompletionBuildEvidence is { } attached && !HasCurrentWorker(task) ? attached : null;
        // A newer round needs its own matching receipt. Once historical custody releases,
        // an uncertified round can fail normally and retry without replacing that operation.
        return OrchestratorBuildEvidenceCheck.Resolve(
            worktreeRoot, task.RequiredRole, changedPaths, dirtyPaths, failedWorkerBuildCheck, workerBuildReceipt,
            historical is null ? runBuildCheck : _ => new(false, -1,
                $"Historical completion build {historical.Id} cannot certify this dispatch; current build receipt is required, or retry after custody release."),
            historical is null ? reconcileOperation : null,
            HasCurrentWorker(task), task.LastDispatch?.WorkerBuildScope?.MetadataDigest,
            goalId is { } owner && task.LastDispatch is { } dispatch
                ? new(owner, task.Id, dispatch) : null);
    }
}
