using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    // Lifecycle-only result: never feed historical WorkerProcess into ApplyRefreshOutcome.
    public CompletionBuildEvidenceOperation ReconcileCompletionBuildEvidence(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, CompletionBuildEvidenceTarget target) =>
        RequireCompletionBuildCoordinator().ReconcileAttached(kernel, goalId, taskId, target);

    public CompletionBuildEvidenceOperation CancelCompletionBuildEvidence(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, CompletionBuildEvidenceTarget target) =>
        RequireCompletionBuildCoordinator().CancelAttached(kernel, goalId, taskId, target);

    private ICompletionBuildEvidenceOperation RequireCompletionBuildCoordinator() =>
        _completionBuildEvidence ?? throw new InvalidOperationException("Completion build operation recovery requires its owning coordinator.");

    public static bool HasCurrentCompletionBuildWorker(TaskSpec task) =>
        task.CompletionBuildEvidence is { } operation && task.LastDispatch?.DispatchedAt == operation.DispatchAt &&
        task.LastProcess is { } process && process.ProcessId == operation.WorkerProcess.ProcessId &&
        process.StartedAt == operation.WorkerProcess.StartedAt;
}
