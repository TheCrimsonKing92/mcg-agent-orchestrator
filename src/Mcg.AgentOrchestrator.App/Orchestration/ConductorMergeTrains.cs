using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

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
    // Two compatible goals remain the existing production cohort's domain. A train starts at three,
    // then may shrink to two only after conflict ejection or bounded RED bisection.
    internal const int MinimumCompositionMembers = 2;
    internal const int MinimumMembers = 3;
    internal const int MaximumMembers = 3;

    internal static ConductorMergeTrainSelection? Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates,
        IReadOnlySet<string>? suppressedPairFingerprints = null,
        IReadOnlySet<string>? ineligibleGoalIds = null,
        IReadOnlySet<string>? attributedMemberKeys = null,
        IReadOnlySet<string>? trainImplicatedMemberKeys = null)
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
            if (ineligibleGoalIds?.Contains(candidate.GoalId.Value) == true ||
                attributedMemberKeys?.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, projection.CandidateRevision)) == true ||
                trainImplicatedMemberKeys?.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, projection.CandidateRevision)) == true ||
                projection.GoalId != candidate.GoalId ||
                projection.LandingPaths.Count == 0 ||
                projection.ResourceKeys.Count == 0 ||
                GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(projection.LandingPaths) ==
                    DotnetShardDisposition.DocsTreeOnlyCandidate ||
                (selected.Count > 0 && !projection.MainRevision.Equals(selected[0].MainRevision, StringComparison.Ordinal)) ||
                selected.Any(existing => Overlaps(existing, projection) ||
                    suppressedPairFingerprints?.Contains(
                        ConductorAcceptanceCohortSelector.PairFingerprint(existing, projection)) == true))
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
