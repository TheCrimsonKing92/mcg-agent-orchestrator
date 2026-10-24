namespace Mcg.AgentOrchestrator.Core;

// Dispatch stage prerequisites apply across the graph, including later positions.
// Ideation has no implicit dispatch-stage prerequisite relationship.
public static class SdlcTaskDependencies
{
    public static bool IsEarlierStage(AgentRole candidate, AgentRole current) =>
        Order(candidate) is { } candidateOrder && Order(current) is { } currentOrder && candidateOrder < currentOrder;

    private static int? Order(AgentRole role) => role switch
    {
        AgentRole.Researcher => 0,
        AgentRole.Planner => 1,
        AgentRole.Developer => 2,
        AgentRole.Tester => 3,
        AgentRole.Reviewer => 4,
        _ => null
    };
}
