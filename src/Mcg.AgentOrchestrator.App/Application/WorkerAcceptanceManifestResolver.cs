using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal static class WorkerAcceptanceManifestResolver
{
    public static Func<string, string> For(OrchestratorWorkspace workspace) =>
        worktree => AcceptanceManifestLocator.Resolve(worktree, workspace.ProjectHomeDirectoryOrNull);
}
