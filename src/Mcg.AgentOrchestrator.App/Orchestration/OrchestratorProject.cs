using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record OrchestratorProject(string Name, string RootDirectory)
{
    public string IntegrationBranch { get; init; } = TrunkBranchName.Default;

    public OrchestratorWorkspace ResolveWorkspace(string? tenantName = null) =>
        OrchestratorWorkspace.ForProject(Name, RootDirectory, tenantName: tenantName, integrationBranch: IntegrationBranch);
}
