using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private OrchestratorBuildEvidenceResolution ResolveWorkerBuildInvocation(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths)
    {
        if (WorkerBuildInvocations is null)
            return new(true, false, "worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts");
        var receipt = WorkerBuildInvocations.Consume(kernel, goalId, taskId, changedPaths, dirtyPaths);
        return new(receipt.MissingEvidence, receipt.CompilerFailed, receipt.Diagnostic);
    }
}
