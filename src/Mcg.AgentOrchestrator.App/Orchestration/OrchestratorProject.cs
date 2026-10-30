using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record OrchestratorProject(string Name, string RootDirectory)
{
    public string IntegrationBranch { get; init; } = TrunkBranchName.Default;
    public string IntegrationBranchSource { get; init; } = IntegrationBranchResolution.Default;
    public string? IntegrationBranchNote { get; init; }
    public string? DataRootDirectory { get; init; }
    public string Delivery { get; init; } = ProjectDelivery.Direct;
    public string? DeliveryDecider { get; init; }
    public OwnerAnswer? DeliveryAnswer { get; init; }
    public OwnerAnswer? DeliveryDeciderAnswer { get; init; }

    public OrchestratorWorkspace ResolveWorkspace(string? tenantName = null) =>
        OrchestratorWorkspace.ForProject(Name, RootDirectory, tenantName: tenantName,
            integrationBranch: IntegrationBranch, dataRootDirectory: DataRootDirectory);
}
