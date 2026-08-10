using System.Collections.ObjectModel;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GateReadyCandidateExclusionReason
{
    LifecycleNotReady,
    GateNotReady,
    RevisionUnknown,
    RevisionStale,
    RiskUnknown,
    RiskNotAutoPromotable,
    ScopeResolutionFailed,
    ScopeEmpty,
    MergeConflict,
    MergeIndeterminate
}

internal enum GateReadyVerificationState
{
    Satisfied
}

internal enum GateReadyMergeStatus
{
    Clean
}

internal enum GateReadyMergeReason
{
    NoConflictsDetected
}

internal sealed record GateReadyCandidateRevisionPair(
    string? BranchRevision,
    string? MainRevision)
{
    public string Fingerprint => $"branch={BranchRevision};main={MainRevision}";
}

internal sealed record GateReadyCandidateInput(
    GoalId GoalId,
    GoalLifecycleState LifecycleState,
    bool VerificationGateSatisfied,
    ChangeRiskTier? ChangeRiskTier,
    ConductorTransitionDecision? AutoPromotionDisposition);

internal sealed record GateReadyLandingScopeObservation(
    bool Succeeded,
    IReadOnlyList<string> Files,
    string? FailureReason = null);

internal sealed record GateReadyMergeTreeObservation(bool IsClean);

internal sealed record GateReadyMergeEvidence(
    string BranchRevision,
    string MainRevision,
    GateReadyMergeStatus Status,
    GateReadyMergeReason Reason);

internal abstract record GateReadyCandidateProjectionResult
{
    private GateReadyCandidateProjectionResult()
    {
    }

    internal sealed record Ready(GateReadyCandidateProjection Projection)
        : GateReadyCandidateProjectionResult;

    internal sealed record Excluded(GateReadyCandidateExclusionReason Reason)
        : GateReadyCandidateProjectionResult;
}

internal sealed class GateReadyCandidateProjection : IEquatable<GateReadyCandidateProjection>
{
    internal GateReadyCandidateProjection(
        GoalId goalId,
        GoalLifecycleState lifecycleState,
        GateReadyVerificationState verificationState,
        ChangeRiskTier changeRiskTier,
        ConductorTransitionDecision autoPromotionDisposition,
        IReadOnlyList<string> landingPaths,
        IReadOnlyList<string> resourceKeys,
        GateReadyMergeEvidence mergeEvidence)
    {
        ArgumentNullException.ThrowIfNull(landingPaths);
        ArgumentNullException.ThrowIfNull(resourceKeys);
        ArgumentNullException.ThrowIfNull(mergeEvidence);
        if (lifecycleState != GoalLifecycleState.Verified)
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycleState));
        }
        if (verificationState != GateReadyVerificationState.Satisfied)
        {
            throw new ArgumentOutOfRangeException(nameof(verificationState));
        }
        if (!Enum.IsDefined(typeof(ChangeRiskTier), changeRiskTier))
        {
            throw new ArgumentOutOfRangeException(nameof(changeRiskTier));
        }
        if (autoPromotionDisposition != ConductorTransitionDecision.Auto)
        {
            throw new ArgumentOutOfRangeException(nameof(autoPromotionDisposition));
        }
        if (landingPaths.Count == 0)
        {
            throw new ArgumentException("A gate-ready projection requires a non-empty landing scope.", nameof(landingPaths));
        }
        if (!ConductorGitRevisionReader.IsValid(mergeEvidence.BranchRevision) ||
            !ConductorGitRevisionReader.IsValid(mergeEvidence.MainRevision) ||
            mergeEvidence.Status != GateReadyMergeStatus.Clean ||
            mergeEvidence.Reason != GateReadyMergeReason.NoConflictsDetected)
        {
            throw new ArgumentException("A gate-ready projection requires clean revision-bound merge evidence.", nameof(mergeEvidence));
        }

        GoalId = goalId;
        LifecycleState = lifecycleState;
        VerificationState = verificationState;
        ChangeRiskTier = changeRiskTier;
        AutoPromotionDisposition = autoPromotionDisposition;
        LandingPaths = CopyAsReadOnly(landingPaths);
        ResourceKeys = CopyAsReadOnly(resourceKeys);
        MergeEvidence = mergeEvidence;
    }

    public GoalId GoalId { get; }
    public string BranchRevision => MergeEvidence.BranchRevision;
    public string MainRevision => MergeEvidence.MainRevision;
    public GoalLifecycleState LifecycleState { get; }
    public GateReadyVerificationState VerificationState { get; }
    public ChangeRiskTier ChangeRiskTier { get; }
    public ConductorTransitionDecision AutoPromotionDisposition { get; }
    public IReadOnlyList<string> LandingPaths { get; }
    public IReadOnlyList<string> ResourceKeys { get; }
    public GateReadyMergeEvidence MergeEvidence { get; }

    public bool Equals(GateReadyCandidateProjection? other) =>
        other is not null &&
        GoalId == other.GoalId &&
        BranchRevision.Equals(other.BranchRevision, StringComparison.Ordinal) &&
        MainRevision.Equals(other.MainRevision, StringComparison.Ordinal) &&
        LifecycleState == other.LifecycleState &&
        VerificationState == other.VerificationState &&
        ChangeRiskTier == other.ChangeRiskTier &&
        AutoPromotionDisposition == other.AutoPromotionDisposition &&
        LandingPaths.SequenceEqual(other.LandingPaths, StringComparer.Ordinal) &&
        ResourceKeys.SequenceEqual(other.ResourceKeys, StringComparer.Ordinal) &&
        MergeEvidence == other.MergeEvidence;

    public override bool Equals(object? obj) => Equals(obj as GateReadyCandidateProjection);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GoalId);
        hash.Add(BranchRevision, StringComparer.Ordinal);
        hash.Add(MainRevision, StringComparer.Ordinal);
        hash.Add(LifecycleState);
        hash.Add(VerificationState);
        hash.Add(ChangeRiskTier);
        hash.Add(AutoPromotionDisposition);
        foreach (var path in LandingPaths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }
        foreach (var resource in ResourceKeys)
        {
            hash.Add(resource, StringComparer.Ordinal);
        }
        hash.Add(MergeEvidence);
        return hash.ToHashCode();
    }

    private static ReadOnlyCollection<string> CopyAsReadOnly(IEnumerable<string> values) =>
        Array.AsReadOnly(values.ToArray());
}
