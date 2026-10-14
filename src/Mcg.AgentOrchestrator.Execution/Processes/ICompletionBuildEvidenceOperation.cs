using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface ICompletionBuildEvidenceOperation
{
    CompletionBuildEvidenceOperation Reconcile(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        OrchestratorBuildCheckRequest request, TaskProcessRecord workerProcess);

    void Cancel(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId);

    CompletionBuildEvidenceOperation ReconcileAttached(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, CompletionBuildEvidenceTarget target);

    CompletionBuildEvidenceOperation CancelAttached(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, CompletionBuildEvidenceTarget target);
}
