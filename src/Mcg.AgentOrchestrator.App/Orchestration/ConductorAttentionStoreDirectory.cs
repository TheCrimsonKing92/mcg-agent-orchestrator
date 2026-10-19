namespace Mcg.AgentOrchestrator.App.Orchestration;

// The workspace owns attention state; drivers without one retain the execution-directory fallback.
internal static class ConductorAttentionStoreDirectory
{
    public static string? Resolve(OrchestratorWorkspace? workspace, string? executionDirectory)
    {
        if (workspace is not null)
        {
            return workspace.OrchestratorDirectory;
        }

        if (string.IsNullOrWhiteSpace(executionDirectory))
        {
            return null;
        }

        return Path.Combine(executionDirectory, ".orchestrator");
    }
}
