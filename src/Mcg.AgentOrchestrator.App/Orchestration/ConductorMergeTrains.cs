using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorMergeTrainSelection(IReadOnlyList<GateReadyCandidateProjection> Members)
{
    internal IReadOnlyList<MergeTrainMemberBinding> BindMembers() => Members.Select(member =>
        new MergeTrainMemberBinding(
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

internal sealed record ConductorMergeTrainRunResult(
    MergeTrainReceipt? Receipt,
    IReadOnlyDictionary<string, ConductorAdvanceResult> MemberResults,
    IReadOnlyList<MergeTrainEjection> Ejections,
    string Detail);

internal static class ConductorMergeTrainSelector
{
    internal const int MinimumMembers = 2;
    internal const int MaximumMembers = 3;

    internal static ConductorMergeTrainSelection? Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        var selected = new List<GateReadyCandidateProjection>(MaximumMembers);
        foreach (var candidate in orderedCandidates)
        {
            if (candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready ready)
            {
                continue;
            }
            var projection = ready.Projection;
            if (projection.GoalId != candidate.GoalId ||
                selected.Any(existing => Overlaps(existing, projection)))
            {
                continue;
            }
            selected.Add(projection);
            if (selected.Count == MaximumMembers)
            {
                break;
            }
        }
        return selected.Count >= MinimumMembers
            ? new ConductorMergeTrainSelection(Array.AsReadOnly(selected.ToArray()))
            : null;
    }

    private static bool Overlaps(GateReadyCandidateProjection left, GateReadyCandidateProjection right) =>
        left.ResourceKeys.Intersect(right.ResourceKeys, StringComparer.OrdinalIgnoreCase).Any() ||
        left.LandingPaths.Any(leftPath => right.LandingPaths.Any(rightPath =>
            RepositoryPathOverlap.Overlaps(leftPath, rightPath)));
}
