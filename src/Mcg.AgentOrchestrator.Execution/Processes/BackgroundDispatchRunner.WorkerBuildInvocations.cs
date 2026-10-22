using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private OrchestratorBuildEvidenceResolution ResolveWorkerBuildInvocation(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths,
        ref string? orchestratorFailureReason)
    {
        if (WorkerBuildInvocations is null)
        {
            // Missing apparatus must deny verification without inventing a native worker exit.
            orchestratorFailureReason ??= "worker-build-consumer-unavailable";
            return new(true, false, "worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts");
        }
        var receipt = WorkerBuildInvocations.Consume(kernel, goalId, taskId, changedPaths, dirtyPaths);
        return new(receipt.MissingEvidence, receipt.CompilerFailed, receipt.Diagnostic);
    }
}
