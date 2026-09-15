using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorAcceptanceCohortPairExclusionReason
{
    UpstreamExcluded,
    DuplicateGoal,
    IncompleteLandingScope,
    DocsTreeOnlyCandidate,
    IncompleteResourceKeys,
    MainRevisionMismatch,
    LandingPathOverlap,
    SerializedResourceOverlap,
    SuppressedInteraction
}

internal sealed record ConductorAcceptanceCohortPairExclusion(
    GoalId FirstGoalId,
    GoalId SecondGoalId,
    ConductorAcceptanceCohortPairExclusionReason Reason,
    string? Evidence = null);

internal sealed record ConductorAcceptanceCohortSelection(
    IReadOnlyList<GateReadyCandidateProjection> Members,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions)
{
    internal IReadOnlyList<AcceptanceCohortMemberBinding> BindMembers() =>
        Members.Select(member => new AcceptanceCohortMemberBinding(
            member.GoalId,
            member.BranchRevision,
            member.CandidateRevision,
            member.LandingPaths,
            member.ResourceKeys,
            member.ChangeRiskTier,
            member.AutoPromotionDisposition,
            member.MergeEvidence.Status.ToString(),
            member.MergeEvidence.Reason.ToString())).ToArray();
}

internal sealed record ConductorAcceptanceCohortSelectionResult(
    ConductorAcceptanceCohortSelection? Selection,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions);

internal sealed record ConductorAcceptanceCohortRunResult(
    AcceptanceCohortReceipt? Receipt,
    IReadOnlyDictionary<string, ConductorAdvanceResult> MemberResults,
    string Detail);

internal static class ConductorAcceptanceCohortSelector
{
    internal const int CohortSize = 2;

    internal static ConductorAcceptanceCohortSelectionResult Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates,
        GoalId? forcedCandidate = null,
        IReadOnlySet<string>? suppressedPairFingerprints = null)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        var ready = new List<GateReadyCandidateProjection>(orderedCandidates.Count);
        var exclusions = new List<ConductorAcceptanceCohortPairExclusion>();
        foreach (var candidate in orderedCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready projected)
            {
                var evidence = candidate.ProjectionResult is GateReadyCandidateProjectionResult.Excluded excluded
                    ? excluded.Reason.ToString()
                    : candidate.ProjectionResult.GetType().Name;
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId,
                    candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.UpstreamExcluded,
                    evidence));
                continue;
            }
            if (projected.Projection.GoalId != candidate.GoalId)
            {
                throw new InvalidOperationException(
                    $"Cohort candidate {candidate.GoalId.Value} does not match its Ready projection {projected.Projection.GoalId.Value}.");
            }
            ready.Add(projected.Projection);
        }

        var forcedCandidateIsReady = false;
        if (forcedCandidate is not null)
        {
            var forcedIndex = ready.FindIndex(candidate => candidate.GoalId == forcedCandidate);
            if (forcedIndex < 0)
            {
                return new ConductorAcceptanceCohortSelectionResult(
                    Selection: null,
                    Array.AsReadOnly((exclusions.Count > 0
                        ? exclusions
                        : [new ConductorAcceptanceCohortPairExclusion(
                            forcedCandidate,
                            forcedCandidate,
                            ConductorAcceptanceCohortPairExclusionReason.UpstreamExcluded,
                            "forced candidate has no current Ready projection")]).ToArray()));
            }
            else
            {
                var forced = ready[forcedIndex];
                ready.RemoveAt(forcedIndex);
                ready.Insert(0, forced);
                forcedCandidateIsReady = true;
            }
        }

        var firstCandidateLimit = forcedCandidateIsReady ? Math.Min(1, ready.Count - 1) : ready.Count - 1;
        for (var firstIndex = 0; firstIndex < firstCandidateLimit; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < ready.Count; secondIndex++)
            {
                var first = ready[firstIndex];
                var second = ready[secondIndex];
                var exclusion = FindExclusion(first, second, suppressedPairFingerprints);
                if (exclusion is null)
                {
                    var frozenExclusions = Array.AsReadOnly(exclusions.ToArray());
                    return new ConductorAcceptanceCohortSelectionResult(
                        new ConductorAcceptanceCohortSelection(
                            Array.AsReadOnly([first, second]),
                            frozenExclusions),
                        frozenExclusions);
                }
                exclusions.Add(exclusion);
            }
        }

        return new ConductorAcceptanceCohortSelectionResult(
            Selection: null,
            Array.AsReadOnly(exclusions.ToArray()));
    }

    internal static string PairFingerprint(GateReadyCandidateProjection first, GateReadyCandidateProjection second) =>
        $"{first.GoalId.Value}:{first.CandidateRevision}:{second.GoalId.Value}:{second.CandidateRevision}:{first.MainRevision}";

    private static ConductorAcceptanceCohortPairExclusion? FindExclusion(
        GateReadyCandidateProjection first,
        GateReadyCandidateProjection second,
        IReadOnlySet<string>? suppressedPairFingerprints)
    {
        if (first.GoalId == second.GoalId)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.DuplicateGoal);
        }
        if (first.LandingPaths.Count == 0 || second.LandingPaths.Count == 0)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.IncompleteLandingScope);
        }
        var firstDisposition = GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(first.LandingPaths);
        var secondDisposition = GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(second.LandingPaths);
        if (firstDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate ||
            secondDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate)
        {
            var docsGoalId = firstDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate
                ? first.GoalId
                : second.GoalId;
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.DocsTreeOnlyCandidate,
                $"goal={docsGoalId.Value}; disposition=docs-tree-only-candidate");
        }
        if (first.ResourceKeys.Count == 0 || second.ResourceKeys.Count == 0)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.IncompleteResourceKeys);
        }
        if (!first.MainRevision.Equals(second.MainRevision, StringComparison.Ordinal))
        {
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.MainRevisionMismatch,
                $"{first.MainRevision}:{second.MainRevision}");
        }
        foreach (var firstPath in first.LandingPaths)
        {
            foreach (var secondPath in second.LandingPaths)
            {
                if (RepositoryPathOverlap.Overlaps(firstPath, secondPath))
                {
                    return Excluded(
                        first,
                        second,
                        ConductorAcceptanceCohortPairExclusionReason.LandingPathOverlap,
                        $"{firstPath}:{secondPath}");
                }
            }
        }
        var sharedResource = first.ResourceKeys.FirstOrDefault(resource =>
            second.ResourceKeys.Contains(resource, StringComparer.OrdinalIgnoreCase));
        if (sharedResource is not null)
        {
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.SerializedResourceOverlap,
                sharedResource);
        }
        if (suppressedPairFingerprints?.Contains(PairFingerprint(first, second)) == true)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.SuppressedInteraction);
        }
        return null;
    }

    private static ConductorAcceptanceCohortPairExclusion Excluded(
        GateReadyCandidateProjection first,
        GateReadyCandidateProjection second,
        ConductorAcceptanceCohortPairExclusionReason reason,
        string? evidence = null) => new(first.GoalId, second.GoalId, reason, evidence);
}

internal static class ConductorAcceptanceCohortAttribution
{
    internal static AcceptanceCohortAttributionOutcome Classify(
        AcceptanceCohortGateOutcome first,
        AcceptanceCohortGateOutcome second)
    {
        if (first is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated ||
            second is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated)
        {
            return AcceptanceCohortAttributionOutcome.Indeterminate;
        }
        if (first == AcceptanceCohortGateOutcome.Failed && second == AcceptanceCohortGateOutcome.Passed)
        {
            return AcceptanceCohortAttributionOutcome.FirstMemberFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Passed && second == AcceptanceCohortGateOutcome.Failed)
        {
            return AcceptanceCohortAttributionOutcome.SecondMemberFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Failed && second == AcceptanceCohortGateOutcome.Failed)
        {
            return AcceptanceCohortAttributionOutcome.BothMembersFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Passed && second == AcceptanceCohortGateOutcome.Passed)
        {
            return AcceptanceCohortAttributionOutcome.InteractionOnly;
        }
        return AcceptanceCohortAttributionOutcome.Indeterminate;
    }
}
