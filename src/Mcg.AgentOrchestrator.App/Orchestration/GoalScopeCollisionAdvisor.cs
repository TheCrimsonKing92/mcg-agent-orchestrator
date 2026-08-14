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
    LifecycleEvidenceUnavailable,
    ComparisonTruncated,
    TextTruncated
}

internal enum ScopeCollisionVerdict
{
    NoOverlapDetected,
    OverlapDetected,
    OverlapDetectedIncomplete,
    InsufficientEvidence
}

internal sealed record GoalScopeLifecycleObservation(
    GoalLifecycleState? State,
    string? UnavailableReason = null)
{
    public bool IsAvailable => State.HasValue;
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

internal sealed record SliceBatchScope(string NodeId, IReadOnlyList<string> Paths);

internal sealed record SliceBatchScopeCollision(
    string LeftNodeId,
    string LeftPath,
    string RightNodeId,
    string RightPath,
    ScopeCollisionKind Kind);

internal sealed record GoalScopeCollisionReport(
    IReadOnlyList<DeclaredFileScope> ProposedScopes,
    int InputGoalCount,
    int EligibleGoalCount,
    int ComparedGoalCount,
    int UncheckableGoalCount,
    int UncomparedGoalCount,
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
        (collision.ProposedProvenance is FileScopeProvenance.Explicit or FileScopeProvenance.Derived) &&
        (collision.ConflictingProvenance is FileScopeProvenance.Explicit or FileScopeProvenance.Derived));

    public string VerdictToken { get; } = Verdict switch
    {
        ScopeCollisionVerdict.NoOverlapDetected => "no-overlap-detected",
        ScopeCollisionVerdict.OverlapDetected => "overlap-detected",
        ScopeCollisionVerdict.OverlapDetectedIncomplete => "overlap-detected-incomplete",
        _ => "insufficient-evidence"
    };
}

internal static class GoalScopeCollisionAdvisor
{
    private const int MaximumTextCharacters = 64_000;

    public static GoalScopeCollisionReport Build(
        IReadOnlyList<string> proposedText,
        IReadOnlyCollection<Goal> goals,
        string? excludedSourceBacklogItemId = null,
        IReadOnlyDictionary<string, GoalScopeLifecycleObservation>? lifecycleObservations = null)
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

        var eligibleGoals = SelectComparisonCandidates(goals, excludedSourceBacklogItemId)
            .Where(goal => IsLifecycleEligible(goal, lifecycleObservations, gaps))
            .ToArray();

        var collisions = new List<ScopeCollision>();
        var uncheckableGoalCount = 0;
        foreach (var goal in eligibleGoals)
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
            if (candidateScopes.Count == 0)
            {
                uncheckableGoalCount++;
            }

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
            .OrderBy(collision => collision.Kind)
            .ThenByDescending(TrustedSideCount)
            .ThenBy(collision => collision.ProposedProvenance)
            .ThenBy(collision => collision.ConflictingProvenance)
            .ThenBy(collision => collision.GoalId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collision => collision.ProposedPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(collision => collision.ConflictingPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var verdict = orderedCollisions.Length > 0
            ? gaps.Count == 0
                ? ScopeCollisionVerdict.OverlapDetected
                : ScopeCollisionVerdict.OverlapDetectedIncomplete
            : gaps.Count == 0
                ? ScopeCollisionVerdict.NoOverlapDetected
                : ScopeCollisionVerdict.InsufficientEvidence;

        return new GoalScopeCollisionReport(
            proposedScopes,
            goals.Count,
            eligibleGoals.Length,
            eligibleGoals.Length,
            uncheckableGoalCount,
            0,
            verdict,
            orderedCollisions,
            gaps.ToArray());
    }

    internal static IReadOnlyList<Goal> SelectComparisonCandidates(
        IEnumerable<Goal> goals,
        string? excludedSourceBacklogItemId = null) =>
        goals
            .Where(goal =>
                !goal.IsTerminal &&
                goal.Status != GoalStatus.Parked &&
                (string.IsNullOrWhiteSpace(excludedSourceBacklogItemId) ||
                 !string.Equals(
                     goal.SourceBacklogItemId,
                     excludedSourceBacklogItemId,
                     StringComparison.Ordinal)))
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();

    private static bool IsLifecycleEligible(
        Goal goal,
        IReadOnlyDictionary<string, GoalScopeLifecycleObservation>? lifecycleObservations,
        ICollection<ScopeEvidenceGapNote> gaps)
    {
        if (lifecycleObservations is null)
        {
            return true;
        }

        if (!lifecycleObservations.TryGetValue(goal.Id.Value, out var observation) || !observation.IsAvailable)
        {
            var reason = observation?.UnavailableReason;
            gaps.Add(new ScopeEvidenceGapNote(
                goal.Id.Value,
                ScopeEvidenceGap.LifecycleEvidenceUnavailable,
                string.IsNullOrWhiteSpace(reason)
                    ? $"Goal {GoalPrefix(goal)} lifecycle evidence is unavailable; it remains eligible."
                    : $"Goal {GoalPrefix(goal)} lifecycle evidence is unavailable ({reason}); it remains eligible."));
            return true;
        }

        return observation.State != GoalLifecycleState.CleanedUp;
    }

    private static int TrustedSideCount(ScopeCollision collision) =>
        (IsTrusted(collision.ProposedProvenance) ? 1 : 0) +
        (IsTrusted(collision.ConflictingProvenance) ? 1 : 0);

    private static bool IsTrusted(FileScopeProvenance provenance) =>
        provenance is FileScopeProvenance.Explicit or FileScopeProvenance.Derived;

    public static IReadOnlyList<SliceBatchScopeCollision> FindPairwiseOverlaps(
        IReadOnlyList<SliceBatchScope> scopes)
    {
        var collisions = new List<SliceBatchScopeCollision>();
        for (var leftIndex = 0; leftIndex < scopes.Count; leftIndex++)
        {
            var left = scopes[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < scopes.Count; rightIndex++)
            {
                var right = scopes[rightIndex];
                foreach (var leftPath in left.Paths)
                {
                    foreach (var rightPath in right.Paths)
                    {
                        var overlap = RepositoryPathOverlap.Classify(leftPath, rightPath);
                        if (overlap == PathOverlapKind.None)
                        {
                            continue;
                        }

                        collisions.Add(new SliceBatchScopeCollision(
                            left.NodeId,
                            leftPath,
                            right.NodeId,
                            rightPath,
                            overlap == PathOverlapKind.Exact
                                ? ScopeCollisionKind.ExactFile
                                : ScopeCollisionKind.DirectoryPrefix));
                    }
                }
            }
        }

        return collisions;
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
        else if (!scopes.Any(scope => scope.IsTrusted))
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
        else if (!scopes.Any(scope => scope.IsTrusted))
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
