using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerQuestionReadModelFactory
{
    internal static OwnerQuestionReadModel Create(IOrchestratorStateQueries state, string orchestratorDirectory,
        OwnerConsoleRefreshGoalSnapshots? snapshots = null) =>
        new OwnerQuestionReadModel(state, orchestratorDirectory, snapshots);

    internal static OwnerQuestionReadModel ForWorkspace(OrchestratorWorkspace workspace) =>
        Create(File.Exists(workspace.SqliteStatePath)
            ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            : new EmptyOwnerConsoleStateQueries(), workspace.OrchestratorDirectory);
}
