using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GateReadyCandidateProjector
{
    private readonly Func<GoalId, GateReadyCandidateRevisionPair> _readRevisions;
    private readonly Func<GoalId, GateReadyLandingScopeObservation> _resolveLandingScope;
    private readonly Func<GoalId, string, string, GateReadyMergeTreeObservation> _readMergeTree;

    internal GateReadyCandidateProjector(
        Func<GoalId, GateReadyCandidateRevisionPair> readRevisions,
        Func<GoalId, GateReadyLandingScopeObservation> resolveLandingScope,
        Func<GoalId, string, string, GateReadyMergeTreeObservation> readMergeTree)
    {
        _readRevisions = readRevisions ?? throw new ArgumentNullException(nameof(readRevisions));
        _resolveLandingScope = resolveLandingScope ?? throw new ArgumentNullException(nameof(resolveLandingScope));
        _readMergeTree = readMergeTree ?? throw new ArgumentNullException(nameof(readMergeTree));
    }

    internal static GateReadyCandidateProjector CreateForRepository(string executionDirectory, string integrationBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionDirectory);

        string ResolveWorktree(GoalId goalId) =>
            GoalWorktrees.TryResolve(executionDirectory, goalId) ??
            throw new InvalidOperationException(
                $"Goal {goalId.Value[..8]} has no registered worktree.");

        return new GateReadyCandidateProjector(
            goalId => ConductorGitRevisionReader.ReadRequiredPair(ResolveWorktree(goalId), integrationBranch),
            goalId =>
            {
                var result = GoalWorktrees.ResolveChangedFilesAgainstHead(executionDirectory, goalId);
                return new GateReadyLandingScopeObservation(
                    result.Succeeded,
                    result.Files,
                    result.FailureReason);
            },
            (goalId, branchRevision, mainRevision) =>
            {
                var status = new WorkerGitContext().ReadReviewerMergeTreeStatus(
                    ResolveWorktree(goalId),
                    mainRevision,
                    branchRevision);
                return new GateReadyMergeTreeObservation(status.IsClean, status.ConflictPaths);
            });
    }

    internal GateReadyCandidateProjectionResult Project(GateReadyCandidateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!TryReadRevisions(input.GoalId, out var initialRevisions))
        {
            return Excluded(GateReadyCandidateExclusionReason.RevisionUnknown);
        }
        if (input.LifecycleState != GoalLifecycleState.Verified)
        {
            return Excluded(GateReadyCandidateExclusionReason.LifecycleNotReady);
        }
        if (!input.VerificationGateSatisfied)
        {
            return Excluded(GateReadyCandidateExclusionReason.GateNotReady);
        }
        if (!input.ChangeRiskTier.HasValue ||
            !Enum.IsDefined(typeof(ChangeRiskTier), input.ChangeRiskTier.Value) ||
            !input.AutoPromotionDisposition.HasValue ||
            !Enum.IsDefined(typeof(ConductorTransitionDecision), input.AutoPromotionDisposition.Value))
        {
            return Excluded(GateReadyCandidateExclusionReason.RiskUnknown);
        }
        if (input.AutoPromotionDisposition != ConductorTransitionDecision.Auto)
        {
            return Excluded(GateReadyCandidateExclusionReason.RiskNotAutoPromotable);
        }

        GateReadyLandingScopeObservation scopeObservation;
        RepositoryLandingScope normalizedScope;
        try
        {
            scopeObservation = _resolveLandingScope(input.GoalId);
            if (scopeObservation is null || !scopeObservation.Succeeded || scopeObservation.Files is null)
            {
                return Excluded(GateReadyCandidateExclusionReason.ScopeResolutionFailed);
            }

            normalizedScope = RepositoryLandingScopeNormalization.Normalize(scopeObservation.Files);
        }
        catch
        {
            return Excluded(GateReadyCandidateExclusionReason.ScopeResolutionFailed);
        }

        if (normalizedScope.Paths.Count == 0)
        {
            return Excluded(GateReadyCandidateExclusionReason.ScopeEmpty);
        }
        if (normalizedScope.ResourceKeys.Count == 0)
        {
            return Excluded(GateReadyCandidateExclusionReason.ResourcesEmpty);
        }

        GateReadyMergeTreeObservation mergeTree;
        try
        {
            mergeTree = _readMergeTree(
                input.GoalId,
                initialRevisions.BranchRevision!,
                initialRevisions.MainRevision!);
        }
        catch
        {
            return Excluded(GateReadyCandidateExclusionReason.MergeIndeterminate);
        }

        if (mergeTree is null)
        {
            return Excluded(GateReadyCandidateExclusionReason.MergeIndeterminate);
        }
        if (!mergeTree.IsClean)
        {
            return new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.MergeConflict)
            {
                ConflictPaths = Array.AsReadOnly((mergeTree.ConflictPaths ?? []).ToArray())
            };
        }

        if (!TryReadRevisions(input.GoalId, out var finalRevisions))
        {
            return Excluded(GateReadyCandidateExclusionReason.RevisionUnknown);
        }
        if (!initialRevisions.BranchRevision!.Equals(finalRevisions.BranchRevision, StringComparison.Ordinal) ||
            !initialRevisions.MainRevision!.Equals(finalRevisions.MainRevision, StringComparison.Ordinal))
        {
            return Excluded(GateReadyCandidateExclusionReason.RevisionStale);
        }

        var mergeEvidence = new GateReadyMergeEvidence(
            initialRevisions.BranchRevision,
            initialRevisions.MainRevision,
            GateReadyMergeStatus.Clean,
            GateReadyMergeReason.NoConflictsDetected);
        return new GateReadyCandidateProjectionResult.Ready(
            new GateReadyCandidateProjection(
                input.GoalId,
                input.LifecycleState,
                GateReadyVerificationState.Satisfied,
                input.ChangeRiskTier.Value,
                input.AutoPromotionDisposition.Value,
                normalizedScope.Paths,
                normalizedScope.ResourceKeys,
                mergeEvidence));
    }

    private bool TryReadRevisions(
        GoalId goalId,
        out GateReadyCandidateRevisionPair normalizedRevisions)
    {
        normalizedRevisions = new GateReadyCandidateRevisionPair(null, null);
        try
        {
            var observed = _readRevisions(goalId);
            if (observed is null ||
                !ConductorGitRevisionReader.TryNormalize(observed.BranchRevision, out var branchRevision) ||
                !ConductorGitRevisionReader.TryNormalize(observed.MainRevision, out var mainRevision))
            {
                return false;
            }

            normalizedRevisions = new GateReadyCandidateRevisionPair(branchRevision, mainRevision);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static GateReadyCandidateProjectionResult.Excluded Excluded(
        GateReadyCandidateExclusionReason reason) => new(reason);
}
