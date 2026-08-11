using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorAcceptanceCohortPairExclusionReason
{
    UpstreamExcluded,
    DuplicateGoal,
    IncompleteLandingScope,
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

internal sealed record ConductorAcceptanceCohortRunResult(
    AcceptanceCohortReceipt? Receipt,
    IReadOnlyDictionary<string, ConductorAdvanceResult> MemberResults,
    string Detail);

internal static class ConductorAcceptanceCohortSelector
{
    internal const int CohortSize = 2;

    internal static ConductorAcceptanceCohortSelection? Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates,
        GoalId? forcedCandidate = null,
        IReadOnlySet<string>? suppressedPairFingerprints = null)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        var ready = new List<GateReadyCandidateProjection>(orderedCandidates.Count);
        foreach (var candidate in orderedCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready projected)
            {
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
            if (forcedIndex >= 0)
            {
                var forced = ready[forcedIndex];
                ready.RemoveAt(forcedIndex);
                ready.Insert(0, forced);
                forcedCandidateIsReady = true;
            }
        }

        var exclusions = new List<ConductorAcceptanceCohortPairExclusion>();
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
                    return new ConductorAcceptanceCohortSelection(
                        Array.AsReadOnly([first, second]),
                        Array.AsReadOnly(exclusions.ToArray()));
                }
                exclusions.Add(exclusion);
            }
        }

        return null;
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
