using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillProposedDependency(string GoalId, string Reason);
internal sealed record BoardFillScopeProposal(string Verdict, IReadOnlyList<BoardFillProposedDependency> Depends);

internal static class BoardFillScopeDepends
{
    internal static BoardFillScopeProposal Propose(string markdown, IReadOnlyCollection<Goal> goals, string backlogItemId)
    {
        var report = GoalScopeCollisionAdvisor.Build([markdown], goals, backlogItemId);
        var dependencies = report.ConflictingGoalIds.Select(id =>
        {
            var reasons = report.Collisions.Where(collision => collision.GoalId == id && Counts(collision))
                .Select(collision => $"{collision.Kind} {collision.ProposedPath}<->{collision.ConflictingPath}").Distinct().ToArray();
            return (GoalId: id, Reasons: reasons);
        }).Where(candidate => candidate.Reasons.Length > 0)
            .Select(candidate => new BoardFillProposedDependency(candidate.GoalId, string.Join("; ", candidate.Reasons)))
            .ToArray();
        return new(report.VerdictToken, dependencies);
    }

    private static bool Counts(ScopeCollision collision)
    {
        if (collision.Kind != ScopeCollisionKind.DirectoryPrefix)
        {
            return true;
        }

        var proposed = RepositoryPathOverlap.Normalize(collision.ProposedPath);
        var conflicting = RepositoryPathOverlap.Normalize(collision.ConflictingPath);
        var containing = conflicting.Length < proposed.Length ? conflicting : proposed;
        return !containing.Equals("tests", StringComparison.OrdinalIgnoreCase)
            && !containing.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);
    }
}
