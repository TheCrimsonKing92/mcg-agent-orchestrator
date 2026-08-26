using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorParallelAcceptanceCandidate(
    Goal Goal,
    int SlotIndex,
    IReadOnlyList<string> ScopePaths,
    IReadOnlyList<string> ResourceKeys,
    IReadOnlyList<string> ConflictScopePaths,
    IReadOnlyList<string> ExcludedConflictScopePaths,
    string? BranchHeadSha = null,
    string? MainHeadSha = null)
{
    public string GoalPrefix => Goal.Id.Value[..8];

    public bool Overlaps(ConductorParallelAcceptanceCandidate other)
    {
        if (ResourceKeys.Intersect(other.ResourceKeys, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        return ConflictScopePaths.Any(left =>
            other.ConflictScopePaths.Any(right => RepositoryPathOverlap.Overlaps(left, right)));
    }

    internal IReadOnlyList<string> GetDocumentationExclusionEvidence(
        ConductorParallelAcceptanceCandidate other)
    {
        if (Overlaps(other) || !OverlapsWithoutDocumentationExclusion(other))
        {
            return [];
        }

        return ExcludedConflictScopePaths
            .Concat(other.ExcludedConflictScopePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CandidateKey =>
        $"{Goal.Id.Value}:{BranchHeadSha ?? "unknown-branch"}:{MainHeadSha ?? "unknown-main"}";

    public static ConductorParallelAcceptanceCandidate Create(
        Goal goal,
        int slotIndex,
        IReadOnlyList<string> fileScopes,
        string? branchHeadSha = null,
        string? mainHeadSha = null)
    {
        var scope = RepositoryLandingScopeNormalization.Normalize(
            fileScopes,
            reserveUnknownScope: true);

        return new ConductorParallelAcceptanceCandidate(
            goal,
            slotIndex,
            scope.Paths,
            scope.ResourceKeys,
            scope.ConflictPaths,
            scope.ExcludedConflictPaths,
            branchHeadSha,
            mainHeadSha);
    }

    internal static bool IsDocumentationExcludedFromConflict(string path)
        => RepositoryLandingScopeNormalization.IsDocumentationExcludedFromConflict(path);

    private bool OverlapsWithoutDocumentationExclusion(ConductorParallelAcceptanceCandidate other)
    {
        var resources = RepositoryLandingScopeNormalization.BuildResourceKeys(
            ScopePaths,
            reserveUnknownScope: ScopePaths.Count == 0);
        var otherResources = RepositoryLandingScopeNormalization.BuildResourceKeys(
            other.ScopePaths,
            reserveUnknownScope: other.ScopePaths.Count == 0);
        if (resources.Intersect(otherResources, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        return ScopePaths.Any(left =>
            other.ScopePaths.Any(right => RepositoryPathOverlap.Overlaps(left, right)));
    }

}

internal sealed record ConductorSpeculativeAcceptanceCandidate(
    GoalId GoalId,
    GateReadyCandidateProjectionResult ProjectionResult);

internal sealed record ConductorSpeculativeAcceptanceCohort(
    IReadOnlyList<GateReadyCandidateProjection> Members);

internal abstract record ConductorSpeculativeAcceptanceExclusionEvidence
{
    private ConductorSpeculativeAcceptanceExclusionEvidence()
    {
    }

    internal sealed record Upstream(GateReadyCandidateExclusionReason Reason)
        : ConductorSpeculativeAcceptanceExclusionEvidence;

    internal sealed record MainRevisionMismatch(
        GoalId ConflictingGoalId,
        string ExpectedMainRevision,
        string ActualMainRevision)
        : ConductorSpeculativeAcceptanceExclusionEvidence;

    internal sealed record LandingPathOverlap(
        GoalId ConflictingGoalId,
        string Path,
        string ConflictingPath)
        : ConductorSpeculativeAcceptanceExclusionEvidence;

    internal sealed record SerializedResourceOverlap(
        GoalId ConflictingGoalId,
        string ResourceKey)
        : ConductorSpeculativeAcceptanceExclusionEvidence;
}

internal enum ConductorSpeculativeAcceptanceDeferralReason
{
    CohortCapacity,
    InsufficientCompatiblePeers
}

internal abstract record ConductorSpeculativeAcceptanceDisposition(GoalId GoalId)
{
    internal sealed record Proposed(GoalId GoalId, int CohortIndex)
        : ConductorSpeculativeAcceptanceDisposition(GoalId);

    internal sealed record Excluded(
        GoalId GoalId,
        ConductorSpeculativeAcceptanceExclusionEvidence Evidence)
        : ConductorSpeculativeAcceptanceDisposition(GoalId);

    internal sealed record Deferred(
        GoalId GoalId,
        ConductorSpeculativeAcceptanceDeferralReason Reason)
        : ConductorSpeculativeAcceptanceDisposition(GoalId);
}

internal sealed record ConductorSpeculativeAcceptancePlan(
    int ReadyCandidateCount,
    IReadOnlyList<ConductorSpeculativeAcceptanceCohort> Cohorts,
    IReadOnlyList<ConductorSpeculativeAcceptanceDisposition> Dispositions)
{
    internal string FormatReceipt(int tick)
    {
        const int maxRenderedOutcomes = 8;
        const int maxReceiptLength = 1024;

        var members = Cohorts.Count == 0
            ? "none"
            : string.Join(',', Cohorts[0].Members.Select(member => Prefix(member.GoalId)));
        var nonProposed = Dispositions
            .Where(disposition => disposition is not ConductorSpeculativeAcceptanceDisposition.Proposed)
            .ToArray();
        var rendered = nonProposed.Take(maxRenderedOutcomes).ToArray();
        var exclusions = rendered
            .OfType<ConductorSpeculativeAcceptanceDisposition.Excluded>()
            .Select(exclusion => $"{Prefix(exclusion.GoalId)}:{Render(exclusion.Evidence)}")
            .ToArray();
        var deferrals = rendered
            .OfType<ConductorSpeculativeAcceptanceDisposition.Deferred>()
            .Select(deferral => $"{Prefix(deferral.GoalId)}:{deferral.Reason}")
            .ToArray();
        var receipt =
            $"SPECULATIVE_COHORT_PLAN tick={tick} advisory=true ready={ReadyCandidateCount} members={members} " +
            $"exclusions={(exclusions.Length == 0 ? "none" : string.Join(',', exclusions))} " +
            $"deferred={(deferrals.Length == 0 ? "none" : string.Join(',', deferrals))} " +
            $"omitted={nonProposed.Length - rendered.Length}";
        if (receipt.Length <= maxReceiptLength)
        {
            return receipt;
        }

        const string suffix = " truncated=true";
        return receipt[..(maxReceiptLength - suffix.Length)] + suffix;
    }

    private static string Prefix(GoalId goalId) => goalId.Value[..Math.Min(8, goalId.Value.Length)];

    private static string Render(ConductorSpeculativeAcceptanceExclusionEvidence evidence) => evidence switch
    {
        ConductorSpeculativeAcceptanceExclusionEvidence.Upstream upstream => upstream.Reason.ToString(),
        ConductorSpeculativeAcceptanceExclusionEvidence.MainRevisionMismatch mismatch =>
            $"MainRevisionMismatch(conflict={Prefix(mismatch.ConflictingGoalId)},expected={Bound(mismatch.ExpectedMainRevision)},actual={Bound(mismatch.ActualMainRevision)})",
        ConductorSpeculativeAcceptanceExclusionEvidence.LandingPathOverlap overlap =>
            $"LandingPathOverlap(conflict={Prefix(overlap.ConflictingGoalId)},path={Bound(overlap.Path)},other={Bound(overlap.ConflictingPath)})",
        ConductorSpeculativeAcceptanceExclusionEvidence.SerializedResourceOverlap overlap =>
            $"SerializedResourceOverlap(conflict={Prefix(overlap.ConflictingGoalId)},resource={Bound(overlap.ResourceKey)})",
        _ => throw new InvalidOperationException($"Unknown speculative acceptance exclusion evidence {evidence.GetType().Name}.")
    };

    private static string Bound(string value)
    {
        const int maxLength = 96;
        var singleToken = value
            .Replace(' ', '_')
            .Replace('\t', '_')
            .Replace('\r', '_')
            .Replace('\n', '_');
        return singleToken.Length <= maxLength ? singleToken : singleToken[..maxLength];
    }
}

internal static class ConductorSpeculativeAcceptanceCohortPlanner
{
    internal const int MaximumCohortSize = 4;
    private const int MinimumCohortSize = 2;

    internal static ConductorSpeculativeAcceptancePlan Plan(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);

        var selected = new List<GateReadyCandidateProjection>(MaximumCohortSize);
        var dispositions = new List<ConductorSpeculativeAcceptanceDisposition>(orderedCandidates.Count);
        var readyCandidateCount = 0;
        foreach (var candidate in orderedCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(candidate.ProjectionResult);

            if (candidate.ProjectionResult is GateReadyCandidateProjectionResult.Excluded upstream)
            {
                dispositions.Add(new ConductorSpeculativeAcceptanceDisposition.Excluded(
                    candidate.GoalId,
                    new ConductorSpeculativeAcceptanceExclusionEvidence.Upstream(upstream.Reason)));
                continue;
            }

            readyCandidateCount++;
            var projection = ((GateReadyCandidateProjectionResult.Ready)candidate.ProjectionResult).Projection;
            if (projection.GoalId != candidate.GoalId)
            {
                throw new InvalidOperationException(
                    $"Speculative acceptance candidate identity {candidate.GoalId.Value} does not match projection {projection.GoalId.Value}.");
            }

            var exclusion = FindExclusion(projection, selected);
            if (exclusion is not null)
            {
                dispositions.Add(new ConductorSpeculativeAcceptanceDisposition.Excluded(
                    candidate.GoalId,
                    exclusion));
                continue;
            }

            if (selected.Count >= MaximumCohortSize)
            {
                dispositions.Add(new ConductorSpeculativeAcceptanceDisposition.Deferred(
                    candidate.GoalId,
                    ConductorSpeculativeAcceptanceDeferralReason.CohortCapacity));
                continue;
            }

            dispositions.Add(new ConductorSpeculativeAcceptanceDisposition.Proposed(candidate.GoalId, 0));
            selected.Add(projection);
        }

        if (selected.Count < MinimumCohortSize)
        {
            var proposedIds = selected.Select(candidate => candidate.GoalId).ToHashSet();
            for (var index = 0; index < dispositions.Count; index++)
            {
                if (dispositions[index] is ConductorSpeculativeAcceptanceDisposition.Proposed proposed &&
                    proposedIds.Contains(proposed.GoalId))
                {
                    dispositions[index] = new ConductorSpeculativeAcceptanceDisposition.Deferred(
                        proposed.GoalId,
                        ConductorSpeculativeAcceptanceDeferralReason.InsufficientCompatiblePeers);
                }
            }

            return new ConductorSpeculativeAcceptancePlan(
                readyCandidateCount,
                Array.Empty<ConductorSpeculativeAcceptanceCohort>(),
                Array.AsReadOnly(dispositions.ToArray()));
        }

        return new ConductorSpeculativeAcceptancePlan(
            readyCandidateCount,
            Array.AsReadOnly(
            [
                new ConductorSpeculativeAcceptanceCohort(Array.AsReadOnly(selected.ToArray()))
            ]),
            Array.AsReadOnly(dispositions.ToArray()));
    }

    private static ConductorSpeculativeAcceptanceExclusionEvidence? FindExclusion(
        GateReadyCandidateProjection candidate,
        IReadOnlyList<GateReadyCandidateProjection> selected)
    {
        foreach (var existing in selected)
        {
            if (!candidate.MainRevision.Equals(existing.MainRevision, StringComparison.Ordinal))
            {
                return new ConductorSpeculativeAcceptanceExclusionEvidence.MainRevisionMismatch(
                    existing.GoalId,
                    existing.MainRevision,
                    candidate.MainRevision);
            }

            foreach (var path in candidate.LandingPaths)
            {
                foreach (var existingPath in existing.LandingPaths)
                {
                    if (RepositoryPathOverlap.Overlaps(path, existingPath))
                    {
                        return new ConductorSpeculativeAcceptanceExclusionEvidence.LandingPathOverlap(
                            existing.GoalId,
                            path,
                            existingPath);
                    }
                }
            }

            var sharedResource = candidate.ResourceKeys.FirstOrDefault(resource =>
                existing.ResourceKeys.Contains(resource, StringComparer.OrdinalIgnoreCase));
            if (sharedResource is not null)
            {
                return new ConductorSpeculativeAcceptanceExclusionEvidence.SerializedResourceOverlap(
                    existing.GoalId,
                    sharedResource);
            }
        }

        return null;
    }
}

internal sealed record ConductorParallelAcceptanceRunResult(
    ConductorParallelAcceptanceCandidate Candidate,
    AcceptanceVerificationSummary? Acceptance,
    ConductorAdvanceResult? EarlyResult,
    Exception? Exception,
    ConductorParallelAcceptanceEarlyOutcome? EarlyOutcome = null,
    FocusedEvidenceRunResult? FocusedEvidence = null)
{
    public static ConductorParallelAcceptanceRunResult Accepted(
        ConductorParallelAcceptanceCandidate candidate,
        AcceptanceVerificationSummary acceptance) =>
        new(candidate, acceptance, null, null);

    public static ConductorParallelAcceptanceRunResult Early(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAdvanceResult result,
        ConductorParallelAcceptanceEarlyOutcome? outcome = null) =>
        new(candidate, null, result, null, outcome);

    public static ConductorParallelAcceptanceRunResult Fault(
        ConductorParallelAcceptanceCandidate candidate,
        Exception exception) =>
        new(candidate, null, null, exception);

    public static ConductorParallelAcceptanceRunResult Focused(
        ConductorParallelAcceptanceCandidate candidate,
        FocusedEvidenceRunResult focusedEvidence) =>
        new(candidate, null, null, null, FocusedEvidence: focusedEvidence);
}

internal sealed record ConductorParallelAcceptanceEarlyOutcome(
    string Kind,
    GoalLifecycleState State,
    string Detail)
{
    public const string MissingBranchRetiredKind = "missing-branch-retired";
    public const string PreLandingEscalatedKind = "pre-landing-escalated";

    public static ConductorParallelAcceptanceEarlyOutcome MissingBranchRetired(
        GoalLifecycleState state,
        string detail) =>
        new(MissingBranchRetiredKind, state, detail);

    public static ConductorParallelAcceptanceEarlyOutcome PreLandingEscalated(
        GoalLifecycleState state,
        string detail) =>
        new(PreLandingEscalatedKind, state, detail);
}
