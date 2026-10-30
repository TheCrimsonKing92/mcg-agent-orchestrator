namespace Mcg.AgentOrchestrator.Core;

/// <summary>Projects declared criteria into brief text, keeping Reviewer removals unindexed and listed once.</summary>
internal sealed class DeclaredTestRemovalBriefContext
{
    private readonly bool _suppressDeclaredSection;

    internal DeclaredTestRemovalBriefContext(Goal goal, AgentRole role)
    {
        HasAuthoritativePlannerCriteria = role == AgentRole.Planner &&
            goal.RefinedSpec is { AcceptanceCriteria.Count: > 0 };
        var reviewerRemovals = role == AgentRole.Reviewer
            ? DeclaredTestRemovalSet.FromObjective(goal.Objective)
            : null;
        _suppressDeclaredSection = HasAuthoritativePlannerCriteria || reviewerRemovals is { Count: > 0 };
        Objective = _suppressDeclaredSection
            ? AcceptanceCriteriaParser.RemoveDeclaredSection(goal.Objective)
            : goal.Objective;
        if (reviewerRemovals is { Count: > 0, Ordinary.Count: > 0 } &&
            goal.RefinedSpec is not { AcceptanceCriteria.Count: > 0 })
        {
            Objective += "\n\n## Acceptance criteria\n" + string.Join("\n",
                reviewerRemovals.Ordinary.Select(criterion => $"- {criterion}"));
        }

        RemovalLines = reviewerRemovals is { Count: > 0 }
            ? [
                "## Declared test removals (context)",
                $"these {reviewerRemovals.Count} removals are declared; confirm the diff removes them",
                .. reviewerRemovals.Declarations.Select(declaration => $"- {declaration.TestIdentity}"),
                string.Empty
            ]
            : [];
    }

    internal bool HasAuthoritativePlannerCriteria { get; }
    internal string Objective { get; }
    internal IReadOnlyList<string> RemovalLines { get; }

    internal string RenderBehavioralContract(string contract) => _suppressDeclaredSection
        ? AcceptanceCriteriaParser.RemoveDeclaredSection(contract)
        : contract;
}
