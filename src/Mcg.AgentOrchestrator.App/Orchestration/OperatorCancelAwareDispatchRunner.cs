using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class OperatorCancelAwareDispatchRunner
{
    public static BackgroundDispatchRunner ForWorkspace(OrchestratorWorkspace workspace) =>
        ForWorkspace(workspace, null);

    internal static BackgroundDispatchRunner ForWorkspace(OrchestratorWorkspace workspace,
        ICompletionBuildEvidenceOperation? completionBuildEvidence) =>
        new(completionBuildEvidence: completionBuildEvidence ?? new CompletionBuildEvidenceCoordinator(
            workspace.SqliteStatePath, workspace.ExecutionDirectory))
        {
            WorkerBuildInvocations = new WorkerBuildInvocationOperations(workspace.SqliteStatePath, workspace.ExecutionDirectory),
            OperatorIntents = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory, workspace.LogDirectory)
        };
}
