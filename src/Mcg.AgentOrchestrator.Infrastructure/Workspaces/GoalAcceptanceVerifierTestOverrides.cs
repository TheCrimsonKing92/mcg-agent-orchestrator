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
    internal Action<string>? OnTrustedMainBaselineBuildStartingForTests { get; set; }
    internal Action? OnStructuralCoveragePreparationFinishedForTests { get; set; }
    internal TimeSpan? StructuralCoveragePermitWaitBound { get; set; }
    internal TimeSpan? StructuralCoveragePermitWaitHeartbeatInterval { get; set; }
    internal Action<AcceptanceGateProgress>? OnStructuralCoveragePermitWaitForTests { get; set; }
    internal Action? OnBuildArtifactIoRetryLeaseReleasedForTests { get; set; }
    internal Action<string>? OnGateWorktreeCleanupLineForTests { get; set; }
    internal bool PartitionVerdictWithinAttemptRerunEnabled { get; set; } = true;
    internal bool DisableLaneReuseShadowForTests { get; set; }
    internal DotnetBaseBuildCache? BaseBuildCacheForTests { get; set; }
    internal MainBaselineDiscoveryCache? MainBaselineDiscoveryCacheForTests { get; set; }
    internal bool MainBaselineDiscoveryCacheEnabled { get; set; } = true;
    internal DotnetBuildStorageRoot? BuildStorageRootForTests { get; set; }
    internal IWorkerIntegrityLabeler? BaselineIntegrityLabelerForTests { get; set; }
    internal TimeSpan? GateChildExitConfirmationBudget { get; set; }
    internal IGateChildReapSeam? GateChildReapSeamForTests { get; set; }
    internal IRemoteLaneExecutor? RemoteLaneExecutorForTests { get; set; }
    internal Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>>? RemoteLaneTransportRunnerForTests { get; set; }
    internal Func<string, int, string[], GitCli.GitResult>? RemoteLaneGitRunnerForTests { get; set; }
    internal TimeProvider? RemoteLaneTimeProviderForTests { get; set; }
    internal TimeSpan? RemoteLanePollInterval { get; set; }
    internal string? RemoteLaneExecutorConfigurationPathForTests { get; set; }
    internal Action<string>? OnRemoteLaneProgressLineForTests { get; set; }
    internal Action<string, RemoteLaneOutcomeCode>? OnRemoteLaneOutcomeForTests { get; set; }
    internal Action<string>? OnRemoteLaneEventForTests { get; set; }
    internal Action<string>? OnRemoteLaneFallbackWaitingForLocalSlotForTests { get; set; }
    internal Func<GoalAcceptanceVerifier.AcceptanceManifestCheck, string?>? ResolvePartitionVerdictClosureHashForTests { get; set; }

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
        OnTrustedMainBaselineBuildStartingForTests = OnTrustedMainBaselineBuildStartingForTests,
        OnStructuralCoveragePreparationFinishedForTests = OnStructuralCoveragePreparationFinishedForTests,
        StructuralCoveragePermitWaitBound = StructuralCoveragePermitWaitBound,
        StructuralCoveragePermitWaitHeartbeatInterval = StructuralCoveragePermitWaitHeartbeatInterval,
        OnStructuralCoveragePermitWaitForTests = OnStructuralCoveragePermitWaitForTests,
        OnBuildArtifactIoRetryLeaseReleasedForTests = OnBuildArtifactIoRetryLeaseReleasedForTests,
        OnGateWorktreeCleanupLineForTests = OnGateWorktreeCleanupLineForTests,
        PartitionVerdictWithinAttemptRerunEnabled = PartitionVerdictWithinAttemptRerunEnabled,
        DisableLaneReuseShadowForTests = DisableLaneReuseShadowForTests,
        BaseBuildCacheForTests = BaseBuildCacheForTests,
        MainBaselineDiscoveryCacheForTests = MainBaselineDiscoveryCacheForTests,
        MainBaselineDiscoveryCacheEnabled = MainBaselineDiscoveryCacheEnabled,
        BuildStorageRootForTests = BuildStorageRootForTests,
        BaselineIntegrityLabelerForTests = BaselineIntegrityLabelerForTests,
        GateChildExitConfirmationBudget = GateChildExitConfirmationBudget,
        GateChildReapSeamForTests = GateChildReapSeamForTests,
        RemoteLaneExecutorForTests = RemoteLaneExecutorForTests,
        RemoteLaneTransportRunnerForTests = RemoteLaneTransportRunnerForTests,
        RemoteLaneGitRunnerForTests = RemoteLaneGitRunnerForTests,
        RemoteLaneTimeProviderForTests = RemoteLaneTimeProviderForTests,
        RemoteLanePollInterval = RemoteLanePollInterval,
        RemoteLaneExecutorConfigurationPathForTests = RemoteLaneExecutorConfigurationPathForTests,
        OnRemoteLaneProgressLineForTests = OnRemoteLaneProgressLineForTests,
        OnRemoteLaneOutcomeForTests = OnRemoteLaneOutcomeForTests,
        OnRemoteLaneEventForTests = OnRemoteLaneEventForTests,
        OnRemoteLaneFallbackWaitingForLocalSlotForTests = OnRemoteLaneFallbackWaitingForLocalSlotForTests,
        ResolvePartitionVerdictClosureHashForTests = ResolvePartitionVerdictClosureHashForTests
    };
}
