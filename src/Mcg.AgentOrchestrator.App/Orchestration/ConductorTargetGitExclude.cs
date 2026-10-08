using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorTargetGitExclude
{
    internal static bool Apply(OrchestratorWorkspace workspace, Action<string>? log = null)
    {
        if (!workspace.IsProjectScoped)
            return false;

        var applied = LocalGitExclude.TryAppendEntries(
            workspace.ExecutionDirectory, [".orchestrator/", ConductorBatchLoop.StopFileName]);
        if (!applied)
            log?.Invoke("[conduct] Warning: could not update the target repository's local git exclude.");
        return applied;
    }
}
