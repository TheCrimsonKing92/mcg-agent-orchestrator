using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static bool CanOverlapNonPartitionDotnetTests(
        IReadOnlyList<AcceptanceManifestCheck> checks,
        int? slotIndex,
        DotnetBuildEnvironmentLease? lease,
        DotnetTestBuildPhase? buildPhase,
        int budget)
    {
        return AcceptanceDotnetTestBatchScheduler.CanOverlap(checks,
            slotIndex is not null && lease is not null && buildPhase is not null, budget,
            check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                     UsesMicrosoftTestingPlatform(check),
            check => TryGetInfrastructurePartitionId(check, out _, out _),
            check => check.ExclusiveResourceKeys.Count > 0);
    }

    private async Task<CheckBatchResult> RunOverlappedDotnetTestBatchAsync(
        IReadOnlyList<AcceptanceManifestCheck> checks,
        AcceptancePartitionVerdictCache? cacheContext,
        string worktreePath,
        GoalId? goalId,
        int slotIndex,
        DotnetBuildEnvironmentLease lease,
        DotnetTestBuildPhase buildPhase,
        int budget,
        CancellationToken cancellationToken,
        bool continueAfterFailure)
    {
        var groups = AcceptanceDotnetTestBatchScheduler.Group(checks,
            check => TryGetInfrastructurePartitionId(check, out _, out _));
        var parts = await AcceptanceDotnetTestBatchScheduler.RunAsync(groups, budget,
            buildPhase.BuildCompleted.Task,
            () => buildPhase.Run?.Result.Passed == true,
            () => cacheContext?.SharedApparatusInvalidation is not null,
            (group, slots, stopToken) => RunInfrastructureShardBatchAsync(group.Checks,
                cacheContext, worktreePath, goalId, slotIndex, lease, buildPhase, budget,
                cancellationToken, slots, stopToken),
            async (check, stopToken) =>
            {
                var run = await RunCheckWithPartitionVerdictCacheAsync(check,
                    cacheContext, worktreePath, goalId, slotIndex, lease, buildPhase,
                    stopToken).ConfigureAwait(false);
                return (new CheckBatchResult([run.Result], run.Retried),
                    !run.Result.Passed && !continueAfterFailure && ShouldStopAfterFailedCheck(cacheContext, check));
            }, cancellationToken).ConfigureAwait(false);
        var results = parts.Where(part => part is not null).SelectMany(part => part!.Results).ToArray();
        return new CheckBatchResult(cacheContext?.ApplySharedApparatusInvalidation(results) ?? results,
            parts.Any(part => part?.Retried == true));
    }
}
