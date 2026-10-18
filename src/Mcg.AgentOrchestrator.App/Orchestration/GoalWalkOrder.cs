using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalWalkOrder
{
    internal static IReadOnlyList<Goal> Order(IReadOnlyList<Goal> goals) =>
        GoalSchedulingOrder.ForWalk(goals);
}
