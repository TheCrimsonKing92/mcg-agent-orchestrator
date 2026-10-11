using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CohortMemberTestRemovalSelection
{
    internal static IReadOnlyList<MemberDeclaredTestRemoval> FromBatch(
        IEnumerable<Goal> batchGoals,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings)
    {
        Goal[] goals;
        try
        {
            goals = batchGoals.ToArray();
        }
        catch (InvalidOperationException)
        {
            // A changing live batch cannot authorize removals; retain the ordinary coverage check.
            return [];
        }

        return MemberDeclaredTestRemovals.FromMembers(bindings.Select(binding =>
            (binding.GoalId, goals.FirstOrDefault(goal => goal.Id == binding.GoalId)?.Objective)));
    }
}
