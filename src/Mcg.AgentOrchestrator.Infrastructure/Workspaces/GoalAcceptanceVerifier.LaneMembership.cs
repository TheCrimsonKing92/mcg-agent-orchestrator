namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static IReadOnlyList<AcceptanceTestLane> ResolveOwnedCollectionLanes(
        AcceptanceGateEngineSettings settings, string worktreePath) =>
        AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, worktreePath);
}
