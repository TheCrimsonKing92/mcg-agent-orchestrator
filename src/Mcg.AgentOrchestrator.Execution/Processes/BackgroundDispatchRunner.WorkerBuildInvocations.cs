using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private OrchestratorBuildEvidenceResolution ResolveWorkerBuildInvocation(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths,
        string workerOutput, Func<OrchestratorBuildEvidenceResolution> reconcileBuild,
        ref string? orchestratorFailureReason)
    {
        if (WorkerBuildInvocations is null)
        {
            // Missing apparatus must deny verification without inventing a native worker exit.
            orchestratorFailureReason ??= "worker-build-consumer-unavailable";
            return new(true, false, "worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts");
        }
        var receipt = WorkerBuildInvocations.Consume(kernel, goalId, taskId, changedPaths, dirtyPaths);
        var host = new OrchestratorBuildEvidenceResolution(receipt.MissingEvidence, receipt.CompilerFailed, receipt.Diagnostic);
        var task = kernel.GetTask(goalId, taskId);
        var infrastructure = WorkerBuildInfrastructureEvidence.Classify(task);
        if (!receipt.MissingEvidence || receipt.CompilerFailed || infrastructure == WorkerBuildInfrastructureBlocker.None ||
            _completionBuildEvidence is null ||
            WorkerResultBlockers.TryGetAssignedScopeComplete(workerOutput, out var complete, out _) && !complete)
            return host;

        var build = reconcileBuild();
        var diagnostic = $"worker_build_infrastructure={infrastructure}";
        if (build.Pending || build.BuildFailed ||
            !build.FailsRound && build.Operation is { Phase: CompletionBuildEvidencePhase.Success } operation &&
            operation.DispatchAt == task.LastDispatch?.DispatchedAt)
            return build with { Diagnostic = AppendDiagnostic(build.Diagnostic, diagnostic) };

        // A receipt alone cannot certify a protocol-1 round, nor can another dispatch's operation.
        return host with { Diagnostic = AppendDiagnostic(AppendDiagnostic(host.Diagnostic, build.Diagnostic), diagnostic) };
    }
}
