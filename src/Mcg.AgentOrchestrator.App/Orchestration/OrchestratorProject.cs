namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OrchestratorProject(string Name, string RootDirectory)
{
    public OrchestratorWorkspace ResolveWorkspace(string? tenantName = null) =>
        OrchestratorWorkspace.ForProject(Name, RootDirectory, tenantName: tenantName);
}
