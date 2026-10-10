using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record MemberDeclaredTestRemoval(GoalId GoalId, string TestIdentity);

/// <summary>Attempt-scoped removal declarations, retaining the first declaring member's attribution.</summary>
public static class MemberDeclaredTestRemovals
{
    public static IReadOnlyList<MemberDeclaredTestRemoval> FromMembers(
        IEnumerable<(GoalId GoalId, string? Objective)> members)
    {
        var declarations = new List<MemberDeclaredTestRemoval>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (goalId, objective) in members)
        {
            if (string.IsNullOrEmpty(objective))
                continue;

            foreach (var criterion in AcceptanceCriteriaParser.Parse(objective))
            {
                if (string.Equals(criterion.Type, "test-removal", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(criterion.TestIdentity) && seen.Add(criterion.TestIdentity))
                    declarations.Add(new MemberDeclaredTestRemoval(goalId, criterion.TestIdentity));
            }
        }
        return declarations.AsReadOnly();
    }

    public static string[] UnionSanctioned(
        string[] worktreeRemovals,
        IReadOnlyList<MemberDeclaredTestRemoval>? memberRemovals)
    {
        // Preserve the solo path's list verbatim, including its order and representation.
        if (memberRemovals is null or { Count: 0 })
            return worktreeRemovals;

        return worktreeRemovals.Concat(memberRemovals.Select(removal => removal.TestIdentity))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
