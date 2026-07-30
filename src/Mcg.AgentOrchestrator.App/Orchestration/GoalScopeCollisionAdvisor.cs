using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ScopeCollisionKind
{
    ExactFile,
    DirectoryPrefix
}

internal enum ScopeEvidenceGap
{
    ProposedScopesMissing,
    ProposedExplicitScopesMissing,
    ActiveScopesMissing,
    ActiveExplicitScopesMissing,
    ComparisonTruncated,
    TextTruncated
}

internal enum ScopeCollisionVerdict
{
    NoOverlapDetected,
    OverlapDetected,
    InsufficientEvidence
}

internal sealed record ScopeCollision(
    string GoalId,
    string GoalPrefix,
    string GoalStatus,
    ScopeCollisionKind Kind,
    string ProposedPath,
    FileScopeProvenance ProposedProvenance,
    string ConflictingPath,
    FileScopeProvenance ConflictingProvenance);

internal sealed record ScopeEvidenceGapNote(string? GoalId, ScopeEvidenceGap Gap, string Message);

internal sealed record GoalScopeCollisionReport(
    IReadOnlyList<DeclaredFileScope> ProposedScopes,
    int ComparedGoalCount,
    ScopeCollisionVerdict Verdict,
    IReadOnlyList<ScopeCollision> Collisions,
    IReadOnlyList<ScopeEvidenceGapNote> EvidenceGaps)
{
    public IReadOnlyList<string> ConflictingGoalIds { get; } = Collisions
        .Select(collision => collision.GoalId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public int ExplicitCollisionCount { get; } = Collisions.Count(collision =>
        collision.ProposedProvenance == FileScopeProvenance.Explicit &&
        collision.ConflictingProvenance == FileScopeProvenance.Explicit);

    public string VerdictToken { get; } = Verdict switch
    {
        ScopeCollisionVerdict.NoOverlapDetected => "no-overlap-detected",
        ScopeCollisionVerdict.OverlapDetected => "overlap-detected",
        _ => "insufficient-evidence"
    };
}

internal static class GoalScopeCollisionAdvisor
{
    private const int MaximumCandidateGoals = 24;
    private const int MaximumTextCharacters = 64_000;

    public static GoalScopeCollisionReport Build(
        IReadOnlyList<string> proposedText,
        IReadOnlyCollection<Goal> goals,
        string? excludedSourceBacklogItemId = null)
    {
        var gaps = new List<ScopeEvidenceGapNote>();
        var proposedScopes = GoalFileScopeInference.FromTextParts(
            proposedText,
            MaximumTextCharacters,
            out var proposedTextTruncated);
        if (proposedTextTruncated)
        {
            gaps.Add(new ScopeEvidenceGapNote(
                null,
                ScopeEvidenceGap.TextTruncated,
                $"Proposed scope text exceeded {MaximumTextCharacters} characters and was truncated."));
        }

        AddProposedEvidenceGap(proposedScopes, gaps);

        var eligibleGoals = goals
            .Where(goal =>
                !goal.IsTerminal &&
                (string.IsNullOrWhiteSpace(excludedSourceBacklogItemId) ||
                 !string.Equals(
                     goal.SourceBacklogItemId,
                     excludedSourceBacklogItemId,
                     StringComparison.Ordinal)))
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (eligibleGoals.Length > MaximumCandidateGoals)
        {
            gaps.Add(new ScopeEvidenceGapNote(
                null,
                ScopeEvidenceGap.ComparisonTruncated,
                $"Compared the first {MaximumCandidateGoals} active goals out of {eligibleGoals.Length}."));
        }

        var candidates = eligibleGoals.Take(MaximumCandidateGoals).ToArray();
        var collisions = new List<ScopeCollision>();
        foreach (var goal in candidates)
        {
            var candidateScopes = GoalFileScopeInference.FromGoal(
                goal,
                MaximumTextCharacters,
                out var candidateTextTruncated);
            if (candidateTextTruncated)
            {
                gaps.Add(new ScopeEvidenceGapNote(
                    goal.Id.Value,
                    ScopeEvidenceGap.TextTruncated,
                    $"Goal {GoalPrefix(goal)} scope text exceeded {MaximumTextCharacters} characters and was truncated."));
            }

            AddActiveEvidenceGap(goal, candidateScopes, gaps);
            foreach (var proposedScope in proposedScopes)
            {
                foreach (var candidateScope in candidateScopes)
                {
                    var overlap = RepositoryPathOverlap.Classify(proposedScope.Path, candidateScope.Path);
                    if (overlap == PathOverlapKind.None)
                    {
                        continue;
                    }

                    collisions.Add(new ScopeCollision(
                        goal.Id.Value,
                        GoalPrefix(goal),
                        goal.Status.ToString(),
                        overlap == PathOverlapKind.Exact
                            ? ScopeCollisionKind.ExactFile
                            : ScopeCollisionKind.DirectoryPrefix,
                        proposedScope.Path,
                        proposedScope.Provenance,
                        candidateScope.Path,
                        candidateScope.Provenance));
                }
            }
        }

        var orderedCollisions = collisions
            .OrderBy(collision => collision.GoalId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collision => collision.Kind)
            .ThenBy(collision => collision.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collision => collision.ConflictingPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var verdict = orderedCollisions.Length > 0
            ? ScopeCollisionVerdict.OverlapDetected
            : gaps.Count == 0
                ? ScopeCollisionVerdict.NoOverlapDetected
                : ScopeCollisionVerdict.InsufficientEvidence;

        return new GoalScopeCollisionReport(
            proposedScopes,
            candidates.Length,
            verdict,
            orderedCollisions,
            gaps.ToArray());
    }

    private static void AddProposedEvidenceGap(
        IReadOnlyList<DeclaredFileScope> scopes,
        ICollection<ScopeEvidenceGapNote> gaps)
    {
        if (scopes.Count == 0)
        {
            gaps.Add(new ScopeEvidenceGapNote(
                null,
                ScopeEvidenceGap.ProposedScopesMissing,
                "The proposed goal declares no repository file scopes."));
        }
        else if (!scopes.Any(scope => scope.Provenance == FileScopeProvenance.Explicit))
        {
            gaps.Add(new ScopeEvidenceGapNote(
                null,
                ScopeEvidenceGap.ProposedExplicitScopesMissing,
                "The proposed goal has inferred scopes but no explicit repository file scope."));
        }
    }

    private static void AddActiveEvidenceGap(
        Goal goal,
        IReadOnlyList<DeclaredFileScope> scopes,
        ICollection<ScopeEvidenceGapNote> gaps)
    {
        if (scopes.Count == 0)
        {
            gaps.Add(new ScopeEvidenceGapNote(
                goal.Id.Value,
                ScopeEvidenceGap.ActiveScopesMissing,
                $"Goal {GoalPrefix(goal)} declares no repository file scopes."));
        }
        else if (!scopes.Any(scope => scope.Provenance == FileScopeProvenance.Explicit))
        {
            gaps.Add(new ScopeEvidenceGapNote(
                goal.Id.Value,
                ScopeEvidenceGap.ActiveExplicitScopesMissing,
                $"Goal {GoalPrefix(goal)} has inferred scopes but no explicit repository file scope."));
        }
    }

    private static string GoalPrefix(Goal goal) =>
        goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];
}
