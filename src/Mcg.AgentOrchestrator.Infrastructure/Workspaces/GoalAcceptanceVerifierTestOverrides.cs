namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GoalAcceptanceVerifierTestOverrides
{
    internal TimeSpan? HeartbeatInterval { get; set; }
    internal TimeSpan? ProgressInterval { get; set; }
    internal TimeSpan? CapturePublicationInterval { get; set; }
    internal TimeSpan? TransientNoHolderBuildLockWaitWindow { get; set; }
    internal TimeSpan? TransientNoHolderBuildLockPollInterval { get; set; }
    internal int? TransientNoHolderBuildLockMaxRetryCycles { get; set; }
    internal Func<string, string?>? ResolveBaseBuildMainShaForTests { get; set; }
    internal Func<string, string?>? ResolvePartitionVerdictCandidateTreeShaForTests { get; set; }
    internal Func<string, string?>? ResolvePartitionVerdictMainShaForTests { get; set; }
    internal Func<string, string?>? ResolvePartitionVerdictVerifyingCommitShaForTests { get; set; }
    internal Func<string, string?>? ResolveMainWorktreePathForTests { get; set; }
    internal Func<string, string[]>? ResolveDeletedTestFilesForTests { get; set; }
    internal Func<int>? ResolveShardCoreBudgetForTests { get; set; }
    internal Func<int>? ResolveGateShardBudgetForTests { get; set; }
    internal string? ShardPermitRootForTests { get; set; }
    internal TimeSpan? ShardPermitPollInterval { get; set; }
    internal Action<string>? OnShardPermitWaitingForTests { get; set; }
    internal Action<string>? OnShardPermitAcquiredForTests { get; set; }
    internal Action<string>? OnShardPermitReleasedForTests { get; set; }
    internal Action<string>? OnInfrastructureShardResourcesAcquiredForTests { get; set; }
    internal Action? OnStructuralCoverageStartedForTests { get; set; }
    internal TimeSpan? StructuralCoveragePermitWaitBound { get; set; }
    internal TimeSpan? StructuralCoveragePermitWaitHeartbeatInterval { get; set; }
    internal Action<AcceptanceGateProgress>? OnStructuralCoveragePermitWaitForTests { get; set; }
    internal Action? OnBuildArtifactIoRetryLeaseReleasedForTests { get; set; }
    internal bool PartitionVerdictWithinAttemptRerunEnabled { get; set; } = true;
    internal DotnetBaseBuildCache? BaseBuildCacheForTests { get; set; }
    internal DotnetBuildStorageRoot? BuildStorageRootForTests { get; set; }

    internal GoalAcceptanceVerifierTestOverrides Snapshot() => new()
    {
        HeartbeatInterval = HeartbeatInterval,
        ProgressInterval = ProgressInterval,
        CapturePublicationInterval = CapturePublicationInterval,
        TransientNoHolderBuildLockWaitWindow = TransientNoHolderBuildLockWaitWindow,
        TransientNoHolderBuildLockPollInterval = TransientNoHolderBuildLockPollInterval,
        TransientNoHolderBuildLockMaxRetryCycles = TransientNoHolderBuildLockMaxRetryCycles,
        ResolveBaseBuildMainShaForTests = ResolveBaseBuildMainShaForTests,
        ResolvePartitionVerdictCandidateTreeShaForTests = ResolvePartitionVerdictCandidateTreeShaForTests,
        ResolvePartitionVerdictMainShaForTests = ResolvePartitionVerdictMainShaForTests,
        ResolvePartitionVerdictVerifyingCommitShaForTests = ResolvePartitionVerdictVerifyingCommitShaForTests,
        ResolveMainWorktreePathForTests = ResolveMainWorktreePathForTests,
        ResolveDeletedTestFilesForTests = ResolveDeletedTestFilesForTests,
        ResolveShardCoreBudgetForTests = ResolveShardCoreBudgetForTests,
        ResolveGateShardBudgetForTests = ResolveGateShardBudgetForTests,
        ShardPermitRootForTests = ShardPermitRootForTests,
        ShardPermitPollInterval = ShardPermitPollInterval,
        OnShardPermitWaitingForTests = OnShardPermitWaitingForTests,
        OnShardPermitAcquiredForTests = OnShardPermitAcquiredForTests,
        OnShardPermitReleasedForTests = OnShardPermitReleasedForTests,
        OnInfrastructureShardResourcesAcquiredForTests = OnInfrastructureShardResourcesAcquiredForTests,
        OnStructuralCoverageStartedForTests = OnStructuralCoverageStartedForTests,
        StructuralCoveragePermitWaitBound = StructuralCoveragePermitWaitBound,
        StructuralCoveragePermitWaitHeartbeatInterval = StructuralCoveragePermitWaitHeartbeatInterval,
        OnStructuralCoveragePermitWaitForTests = OnStructuralCoveragePermitWaitForTests,
        OnBuildArtifactIoRetryLeaseReleasedForTests = OnBuildArtifactIoRetryLeaseReleasedForTests,
        PartitionVerdictWithinAttemptRerunEnabled = PartitionVerdictWithinAttemptRerunEnabled,
        BaseBuildCacheForTests = BaseBuildCacheForTests,
        BuildStorageRootForTests = BuildStorageRootForTests
    };
}
