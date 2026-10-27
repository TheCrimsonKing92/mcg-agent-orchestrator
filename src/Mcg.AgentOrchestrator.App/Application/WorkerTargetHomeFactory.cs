using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal static class WorkerTargetHomeFactory
{
    internal static WorkerTargetHome For(OrchestratorHome home, OrchestratorWorkspace workspace) =>
        new WorkerTargetHome(home.IsHome(workspace), WorkerAcceptanceManifestResolver.For(workspace), workspace.ProjectHomeDirectoryOrNull);
}
