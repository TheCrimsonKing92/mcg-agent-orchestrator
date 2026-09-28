using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class OperatorCancelAwareDispatchRunner
{
    public static BackgroundDispatchRunner ForWorkspace(OrchestratorWorkspace workspace) =>
        new()
        {
            OperatorIntents = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory, workspace.LogDirectory)
        };
}
