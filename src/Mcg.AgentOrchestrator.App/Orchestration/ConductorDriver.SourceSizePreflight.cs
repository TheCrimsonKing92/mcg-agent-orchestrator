using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorParallelAcceptanceRunResult? RunParallelLandingSourceSizePreflight(
        ConductorParallelAcceptanceCandidate candidate) =>
        SourceSizeLandingPreflightExecutor.RunParallelLandingSourceSizePreflight(
            candidate,
            GoalWorktrees.WorktreePath(_executionDirectory!, candidate.Goal.Id));

    private AcceptanceVerificationSummary? RunInlineLandingSourceSizePreflight(Goal goal) =>
        SourceSizeLandingPreflightExecutor.RunInlineLandingSourceSizePreflight(
            _executionDirectory is null ? null : GoalWorktrees.WorktreePath(_executionDirectory, goal.Id),
            _executionDirectory,
            TryResolveGitHead);

    private AcceptanceCohortReceipt? RunAcceptanceCohortSourceSizePreflight(
        string integrationPath,
        AcceptanceCohortIdentity identity,
        CohortAcceptanceStore store) =>
        SourceSizeLandingPreflightExecutor.RunAcceptanceCohortSourceSizePreflight(
            integrationPath, identity, store, BoundCohortDetail, _utcNow());

    private static MergeTrainReceipt? RunMergeTrainSourceSizePreflight(
        string integrationPath,
        MergeTrainIdentity identity,
        MergeTrainAcceptanceStore store) =>
        SourceSizeLandingPreflightExecutor.RunMergeTrainSourceSizePreflight(
            integrationPath, identity, store, BoundCohortDetail);
}
