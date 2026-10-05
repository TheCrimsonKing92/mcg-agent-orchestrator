using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillProposedDependency(string GoalId, string Reason);
internal sealed record BoardFillScopeProposal(string Verdict, IReadOnlyList<BoardFillProposedDependency> Depends);

internal static class BoardFillScopeDepends
{
    internal static BoardFillScopeProposal Propose(string markdown, IReadOnlyCollection<Goal> goals, string backlogItemId)
    {
        var report = GoalScopeCollisionAdvisor.Build([markdown], goals, backlogItemId);
        return new(report.VerdictToken, report.ConflictingGoalIds.Select(id => new BoardFillProposedDependency(id,
            string.Join("; ", report.Collisions.Where(collision => collision.GoalId == id)
                .Select(collision => $"{collision.Kind} {collision.ProposedPath}<->{collision.ConflictingPath}").Distinct()))).ToArray());
    }
}
